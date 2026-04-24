using System.IO;
using EChat.Core.Crypto;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Protocol;
using EChat.Core.Services;
using static EChat.Core.Models.MessageStatus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Chat = EChat.Core.Models.Chat;
using ChatMessage = EChat.Core.Models.ChatMessage;
using Contact = EChat.Core.Models.Contact;
using ChatType = EChat.Core.Models.ChatType;

namespace EChat.Core.Transport;

public class IncomingMessageService
{
    private readonly ILogger<IncomingMessageService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChatEventService _chatEvents;
    private readonly PgpService _pgpService;
    private readonly AccountConfig _accountConfig;
    private readonly FileLogger _fileLogger;

    // Serialises concurrent SaveAsync calls so two callers processing the same
    // message (e.g. SyncEchatFolderAsync + ProcessChatMessagesAsync) cannot both
    // pass the dedup check simultaneously and race to INSERT the same row.
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public IncomingMessageService(
        ILogger<IncomingMessageService> logger,
        IServiceScopeFactory scopeFactory,
        ChatEventService chatEvents,
        PgpService pgpService,
        AccountConfig accountConfig,
        FileLogger fileLogger)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _chatEvents = chatEvents;
        _pgpService = pgpService;
        _accountConfig = accountConfig;
        _fileLogger = fileLogger;
    }

    public async Task SaveAsync(string accountId, List<ParsedMessage> parsed)
    {
        if (parsed == null || parsed.Count == 0) return;

        await _saveLock.WaitAsync();
        try
        {
        await SaveInternalAsync(accountId, parsed);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task SaveInternalAsync(string accountId, List<ParsedMessage> parsed)
    {
        _fileLogger.Write("INFO", "SaveAsync", $"Entering: accountId={accountId}, messages={parsed.Count}");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        // Load this account's email from DB — _accountConfig may belong to a different account
        var accountEmail = await db.Accounts
            .Where(a => a.AccountId == accountId)
            .Select(a => a.Email)
            .FirstOrDefaultAsync();

        _fileLogger.Write("INFO", "SaveAsync", $"accountEmail={accountEmail}, accountId={accountId}");

        var updatedChats = new HashSet<string>();
        // chatId → (chatName, senderName, preview) for incoming non-self, non-muted messages
        var notificationEntries = new Dictionary<string, (string chatName, string senderName, string preview)>(StringComparer.OrdinalIgnoreCase);
        var batchContacts = new Dictionary<string, Contact>(StringComparer.OrdinalIgnoreCase);
        var batchGroupChats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var batchSenderChatIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Load this account's chat IDs once — deduplication must be strictly per-account.
        // Do NOT include chat rows owned by other accounts (e.g. shared group chats): that
        // would cause account B to see account A's saved copy of a group message as a
        // duplicate and skip it entirely — so UnreadCount is never incremented for account B.
        // Each account must independently receive and store every message via its own IMAP.
        var accountChatIds = await db.Chats
            .Where(c => c.AccountId == accountId)
            .Select(c => c.ChatId)
            .ToListAsync();
        var accountChatIdSet = new HashSet<string>(accountChatIds, StringComparer.OrdinalIgnoreCase);

        _fileLogger.Write("INFO", "SaveAsync", $"accountChatIds count={accountChatIds.Count}");
        if (accountChatIds.Count > 0)
        {
            _fileLogger.Write("DEBUG", "SaveAsync", $"First few chatIds: {string.Join(", ", accountChatIds.Take(5))}");
        }

        // ── Handle read receipts (Chat-Read-Of) ─────────────────────────────
        foreach (var pm in parsed.Where(m => m.Headers.ReadOf != null && m.Headers.ReadOf.Count > 0))
        {
            var ids = pm.Headers.ReadOf!;
            // Mark our outgoing messages as Read — only messages in this account's chats
            var affected = await db.Messages
                .Where(m => ids.Contains(m.MessageId) && accountChatIds.Contains(m.ChatId))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Read));
            if (affected > 0)
            {
                // Fire chat-updated for each affected chat so UI refreshes
                var affectedChats = await db.Messages
                    .Where(m => ids.Contains(m.MessageId) && accountChatIds.Contains(m.ChatId))
                    .Select(m => m.ChatId)
                    .Distinct()
                    .ToListAsync();
                foreach (var cid in affectedChats)
                    updatedChats.Add(cid);
            }
        }

        // ── Handle edits (Chat-Edit-Of) ──────────────────────────────────────
        foreach (var pm in parsed.Where(m => m.Headers.EditOf != null))
        {
            var target = await db.Messages
                .FirstOrDefaultAsync(m => m.MessageId == pm.Headers.EditOf && accountChatIds.Contains(m.ChatId));
            if (target == null) continue;
            target.Content = pm.Content;
            target.IsEdited = true;
            target.EditVersion = (pm.Headers.EditVersion ?? target.EditVersion) + 1;
            updatedChats.Add(target.ChatId);
        }

        // ── Handle deletes (Chat-Delete-Of) ──────────────────────────────────
        foreach (var pm in parsed.Where(m => m.Headers.DeleteOf != null))
        {
            var target = await db.Messages
                .FirstOrDefaultAsync(m => m.MessageId == pm.Headers.DeleteOf && accountChatIds.Contains(m.ChatId));
            if (target == null) continue;
            db.Messages.Remove(target);
            updatedChats.Add(target.ChatId);
        }

        // ── Handle reactions (Chat-Reaction header) ──────────────────────────
        foreach (var pm in parsed.Where(m => !string.IsNullOrEmpty(m.Headers.Reaction) && !string.IsNullOrEmpty(m.Headers.ReactionTo)))
        {
            var targetMsgId = pm.Headers.ReactionTo;
            var emoji = pm.Headers.Reaction;
            var sender = pm.Sender;

            var existing = await db.MessageReactions
                .FirstOrDefaultAsync(r => r.MessageId == targetMsgId && r.Emoji == emoji && r.Sender == sender);

            if (existing == null)
            {
                db.MessageReactions.Add(new MessageReaction
                {
                    MessageId = targetMsgId,
                    Emoji = emoji,
                    Sender = sender,
                    Timestamp = pm.Headers.Timestamp
                });
            }
        }

        // ── Handle system messages (Chat-System-Type) ────────────────────────
        foreach (var pm in parsed.Where(m => m.Headers.SystemType != null))
        {
            // Dedup: system messages are stored with their original Chat-Message-ID.
            // If it's already in DB, the message was processed in a previous session — skip.
            if (pm.Headers.MessageId != null &&
                await db.Messages.AnyAsync(m => m.MessageId == pm.Headers.MessageId))
            {
                _fileLogger.Write("INFO", "SaveAsync", $"System message {pm.Headers.SystemType} msgId={pm.Headers.MessageId} already in DB, skipping");
                continue;
            }

            _fileLogger.Write("INFO", "SaveAsync", $"Processing system message: type={pm.Headers.SystemType}, msgId={pm.Headers.MessageId}, sender={pm.Sender}, groupId={pm.Headers.GroupId}");

            switch (pm.Headers.SystemType)
            {
                case "group-create":
                    await HandleGroupCreateAsync(db, pm, accountId, updatedChats, accountChatIdSet, accountChatIds);
                    break;
                case "group-delete":
                    await HandleGroupDeleteAsync(db, pm, accountId, updatedChats, accountChatIdSet, accountChatIds);
                    break;
                case "group-leave":
                    await HandleGroupLeaveAsync(db, pm, accountId, updatedChats, accountChatIdSet, accountChatIds);
                    break;
                case "chat-delete":
                    await HandleChatDeleteAsync(db, pm, accountId, updatedChats, accountChatIdSet, accountChatIds);
                    break;
            }
        }

        _fileLogger.Write("DEBUG", "SaveAsync", $"After system messages: updatedChats=[{string.Join(",", updatedChats)}]");

        // ── Handle regular new messages (exclude reactions, edits, deletes, read receipts, system, sync) ──
        foreach (var pm in parsed.Where(m =>
            m.Headers.EditOf == null &&
            m.Headers.DeleteOf == null &&
            (m.Headers.ReadOf == null || m.Headers.ReadOf.Count == 0) &&
            m.Headers.SystemType == null &&
            string.IsNullOrEmpty(m.Headers.Reaction) &&
            string.IsNullOrEmpty(m.Headers.SyncType)))
        {
            if (string.IsNullOrEmpty(pm.Headers.MessageId) || string.IsNullOrEmpty(pm.Sender))
                continue;

            // Detect a sent-message sync copy: the sender is ourselves (from another device).
            // We CC ourselves when sending so other devices pick up outgoing messages.
            var isSentSync = !string.IsNullOrEmpty(accountEmail) &&
                pm.Sender.Equals(accountEmail, StringComparison.OrdinalIgnoreCase);

            // For 1:1 sent-sync, routing is by the actual recipient, not the sender (self).
            var chatPartner = isSentSync
                ? pm.Recipients.FirstOrDefault(r =>
                    !r.Equals(accountEmail, StringComparison.OrdinalIgnoreCase))
                : pm.Sender;

            _fileLogger.Write("DEBUG", "SaveAsync", $"Msg ROUTING: id={pm.Headers.MessageId}, sender={pm.Sender}, accountEmail={accountEmail}, isSentSync={isSentSync}, chatPartner={chatPartner}, recipients={string.Join(",", pm.Recipients)}");

            if (string.IsNullOrEmpty(pm.Headers.GroupId) && string.IsNullOrEmpty(chatPartner))
            {
                _fileLogger.Write("WARN", "SaveAsync", $"Skipping msg {pm.Headers.MessageId}: no groupId and no chatPartner");
                continue; // can't route — skip
            }

            // Dedup: check only within this account's chats.
            // A global MessageId check would cause account B to skip messages that account A
            // already saved (same email → same Message-ID), so the scope must be per-account.
            var inMemoryDuplicate = db.ChangeTracker.Entries<ChatMessage>()
                .Any(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added && e.Entity.MessageId == pm.Headers.MessageId);

            // accountChatIdSet already includes cross-account group chats this account is a member of.
            var alreadyInDb = await db.Messages.AnyAsync(m =>
                m.MessageId == pm.Headers.MessageId &&
                accountChatIdSet.Contains(m.ChatId));

            if (inMemoryDuplicate || alreadyInDb)
            {
                _fileLogger.Write("INFO", "SaveAsync", $"Duplicate msg {pm.Headers.MessageId}, inMemory={inMemoryDuplicate}, inDb={alreadyInDb}");
                continue;
            }

            string chatId;

            if (!string.IsNullOrEmpty(pm.Headers.GroupId))
            {
                if (!batchGroupChats.TryGetValue(pm.Headers.GroupId, out chatId!))
                {
                    var groupChat = await db.Chats
                        .FirstOrDefaultAsync(c => c.GroupId == pm.Headers.GroupId &&
                                                  c.AccountId == accountId &&
                                                  !c.Deleted);
                    if (groupChat == null)
                    {
                        // Group name is only in Chat-Group-Name header (sent on group creation/rename).
                        // Regular messages don't carry it. Fall back to any existing row for this
                        // group (owned by another account) to get the real name.
                        var groupName = pm.Headers.GroupName;
                        if (string.IsNullOrEmpty(groupName))
                        {
                            groupName = await db.Chats
                                .Where(c => c.GroupId == pm.Headers.GroupId && !c.Deleted)
                                .Select(c => c.Name)
                                .FirstOrDefaultAsync() ?? "Group Chat";
                        }

                        chatId = Guid.NewGuid().ToString();
                        groupChat = new Chat
                        {
                            ChatId = chatId,
                            Type = ChatType.Group,
                            GroupId = pm.Headers.GroupId,
                            Name = groupName,
                            AccountId = accountId,
                            CreatedAt = DateTimeOffset.UtcNow,
                            LastActivityAt = pm.Headers.Timestamp
                        };
                        db.Chats.Add(groupChat);
                        accountChatIdSet.Add(chatId);
                        accountChatIds.Add(chatId);
                    }
                    else
                    {
                        chatId = groupChat.ChatId;
                    }
                    batchGroupChats[pm.Headers.GroupId] = chatId;
                }
            }
            else
            {
                // Route by chatPartner (the other person in the conversation)
                if (!batchSenderChatIds.TryGetValue(chatPartner!, out chatId!))
                {
                    // 1. Try to find by ContactEmail (most reliable for 1:1 chats)
                    var partnerChat = await db.Chats.FirstOrDefaultAsync(c =>
                        c.Type == ChatType.OneToOne &&
                        !c.Deleted &&
                        c.AccountId == accountId &&
                        c.ContactEmail == chatPartner);
                    if (partnerChat != null)
                    {
                        chatId = partnerChat.ChatId;
                    }
                    else
                    {
                        // 2. Fallback: find by messages from the partner (original logic)
                        var existingChatId = await db.Messages
                            .Where(m => m.Sender == chatPartner && accountChatIds.Contains(m.ChatId))
                            .Select(m => m.ChatId)
                            .FirstOrDefaultAsync();

                        if (existingChatId != null)
                        {
                            var chatExists = await db.Chats.AnyAsync(c => c.ChatId == existingChatId && !c.Deleted);
                            if (!chatExists)
                                existingChatId = null;
                        }

                        if (existingChatId != null)
                        {
                            chatId = existingChatId;
                        }
                        else
                        {
                            // 3. Fallback: find by display name or email prefix
                            if (!batchContacts.TryGetValue(chatPartner!, out var contact))
                                contact = await db.Contacts.FindAsync(chatPartner);

                            var chatName = contact?.DisplayName ?? chatPartner!.Split('@')[0];

                            var namedChat = await db.Chats.FirstOrDefaultAsync(c =>
                                c.Type == ChatType.OneToOne &&
                                !c.Deleted &&
                                c.AccountId == accountId &&
                                (c.Name == chatName || c.Name == chatPartner));

                            if (namedChat != null)
                            {
                                chatId = namedChat.ChatId;
                            }
                            else
                            {
                                if (contact == null)
                                {
                                    contact = new Contact
                                    {
                                        Email = chatPartner!,
                                        DisplayName = chatPartner!.Split('@')[0]
                                    };
                                    db.Contacts.Add(contact);
                                    batchContacts[chatPartner!] = contact;
                                }

                                chatId = Guid.NewGuid().ToString();
                                var newChat = new Chat
                                {
                                    ChatId = chatId,
                                    Type = ChatType.OneToOne,
                                    Name = contact.DisplayName ?? chatPartner!.Split('@')[0],
                                    ContactEmail = chatPartner,
                                    AccountId = accountId,
                                    CreatedAt = DateTimeOffset.UtcNow,
                                    LastActivityAt = pm.Headers.Timestamp
                                };
                                db.Chats.Add(newChat);
                                accountChatIdSet.Add(chatId);
                                accountChatIds.Add(chatId);
                            }
                        }
                    }
                    batchSenderChatIds[chatPartner!] = chatId;
                }
            }

            _fileLogger.Write("INFO", "SaveAsync", $"ADDING msg to chat: id={pm.Headers.MessageId}, chatId={chatId}, sender={pm.Sender}, isSentSync={isSentSync}");

            db.Messages.Add(new ChatMessage
            {
                MessageId = pm.Headers.MessageId,
                ChatId = chatId,
                Sender = pm.Sender,
                Content = pm.Content,
                Timestamp = pm.Headers.Timestamp,
                DisplayTimestamp = pm.Headers.Timestamp,
                ReceivedAt = DateTimeOffset.UtcNow,
                Encrypted = pm.IsEncrypted,
                InReplyTo = pm.Headers.InReplyTo,
                Status = isSentSync ? MessageStatus.Sent : MessageStatus.Sent,
                ImapUid = pm.ImapUid,
                ImapFolder = pm.ImapFolder
            });

            // Save attachments from the received email to disk + DB
            _fileLogger.Write("DEBUG", "SaveAsync", $"Msg {pm.Headers.MessageId}: attachments={pm.Attachments?.Count ?? 0}");
            if (pm.Attachments != null && pm.Attachments.Count > 0)
            {
                var attDir = Path.Combine(_fileLogger.AppDir, "attachments");
                _fileLogger.Write("DEBUG", "SaveAsync", $"Saving {pm.Attachments.Count} attachment(s) to {attDir}");
                Directory.CreateDirectory(attDir);
                foreach (var att in pm.Attachments)
                {
                    try
                    {
                        var safe = Path.GetFileName(att.FileName); // strip any path component
                        var attPath = Path.Combine(attDir, $"{pm.Headers.MessageId}_{safe}");
                        await File.WriteAllBytesAsync(attPath, att.Data);
                        var isImage = att.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                        db.Attachments.Add(new Attachment
                        {
                            Id = Guid.NewGuid().ToString(),
                            MessageId = pm.Headers.MessageId,
                            FileName = safe,
                            ContentType = att.ContentType,
                            Size = att.Size,
                            FilePath = Path.GetFileName(attPath),
                            IsImage = isImage
                        });
                    }
                    catch (Exception ex)
                    {
                        _fileLogger.Write("WARN", "SaveAsync", $"Failed to save attachment {att.FileName} for msg {pm.Headers.MessageId}: {ex.Message}");
                    }
                }
            }

            // FindAsync checks the EF change tracker first, so it returns the tracked entity
            // for both newly-created (Added) chats and existing (Unchanged/Modified) ones.
            // _saveLock serialises all SaveAsync calls, so read-modify-write on UnreadCount is safe.
            var chat = await db.Chats.FindAsync(chatId);
            if (chat != null)
            {
                if (pm.Headers.Timestamp > (chat.LastActivityAt ?? DateTimeOffset.MinValue))
                    chat.LastActivityAt = pm.Headers.Timestamp;

                if (!isSentSync)
                    chat.UnreadCount++;
            }

            // Collect notification data (first incoming message per chat wins as preview).
            if (!isSentSync && chat != null && !chat.Muted && !notificationEntries.ContainsKey(chatId))
            {
                var senderName = pm.Sender?.Split('@')[0] ?? "Unknown";
                if (batchContacts.TryGetValue(pm.Sender ?? "", out var sc) &&
                    !string.IsNullOrEmpty(sc.DisplayName))
                    senderName = sc.DisplayName;
                var preview = string.IsNullOrWhiteSpace(pm.Content)
                    ? (pm.Attachments?.Count > 0 ? "📎 Attachment" : "New message")
                    : pm.Content.Length > 80 ? pm.Content[..80] + "…" : pm.Content;
                notificationEntries[chatId] = (chat.Name, senderName, preview);
            }

            updatedChats.Add(chatId);
        } // end regular messages loop

        if (updatedChats.Count > 0)
        {
            const int maxRetries = 3;
            Exception? lastEx = null;
            bool uniqueConstraintHandled = false;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    await db.SaveChangesAsync();
                    _fileLogger.Write("INFO", "SaveAsync", $"Saved {updatedChats.Count} chats successfully");
                    foreach (var chatId in updatedChats)
                        _chatEvents.NotifyChatUpdated(chatId);

                    // Fire OS-level notifications for incoming messages.
                    if (notificationEntries.Count > 0)
                    {
                        var totalUnread = await db.Chats.AsNoTracking()
                            .Where(c => !c.Muted && !c.Archived && !c.Deleted)
                            .SumAsync(c => c.UnreadCount);
                        foreach (var (nid, (chatName, senderName, preview)) in notificationEntries)
                            _chatEvents.NotifyNewMessage(new NewMessagePayload(nid, chatName, senderName, preview, totalUnread));
                    }
                    break;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    // UNIQUE constraint means a concurrent call already saved this — no point retrying.
                    if (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
                    {
                        _fileLogger.Write("INFO", "SaveAsync", "UNIQUE constraint on early attempt — already saved by concurrent call. Treating as success.");
                        db.ChangeTracker.Clear();
                        uniqueConstraintHandled = true;
                        break;
                    }
                    lastEx = ex;
                    var innerMsg = ex.InnerException != null ? $" Inner: {ex.InnerException.Message}" : "";
                    _fileLogger.Write("WARN", "SaveAsync", $"Save attempt {attempt} failed: {ex.Message}{innerMsg}. Retrying...");
                    await Task.Delay(100 * attempt);
                    db.ChangeTracker.Clear();
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    var innerMsg = ex.InnerException != null ? $" Inner({ex.InnerException.GetType().Name}): {ex.InnerException.Message}" : "";
                    string sqlMsg = "";
                    int sqliteCode = -1;
                    if (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx)
                    {
                        sqliteCode = sqliteEx.SqliteErrorCode;
                        sqlMsg = $" SQLite: {sqliteCode} - {sqliteEx.Message}";
                    }
                    _fileLogger.Write("ERROR", "SaveAsync", $"Save failed after {attempt} attempts: {ex.Message}{innerMsg}{sqlMsg}");
                    
                    if (sqliteCode == 19)
                    {
                        _fileLogger.Write("INFO", "SaveAsync", "UNIQUE constraint - message likely already saved by another device. Treating as success.");
                        uniqueConstraintHandled = true;
                        db.ChangeTracker.Clear();
                        break;
                    }
                    
                    _fileLogger.Write("ERROR", "IncomingMessageService", $"Failed to save incoming messages batch: {ex.Message}");
                    db.ChangeTracker.Clear();
                }
            }
            
            if (uniqueConstraintHandled)
            {
                foreach (var chatId in updatedChats)
                    _chatEvents.NotifyChatUpdated(chatId);

                // Still fire OS-level notifications — the message IS saved (by the concurrent call).
                if (notificationEntries.Count > 0)
                {
                    try
                    {
                        using var s2 = _scopeFactory.CreateScope();
                        var db2 = s2.ServiceProvider.GetRequiredService<ChatDbContext>();
                        var totalUnread = await db2.Chats.AsNoTracking()
                            .Where(c => !c.Muted && !c.Archived && !c.Deleted)
                            .SumAsync(c => c.UnreadCount);
                        foreach (var (nid, (chatName, senderName, preview)) in notificationEntries)
                            _chatEvents.NotifyNewMessage(new NewMessagePayload(nid, chatName, senderName, preview, totalUnread));
                    }
                    catch { /* best-effort */ }
                }
            }
        }
        else
        {
            _fileLogger.Write("INFO", "SaveAsync", "No chats to update");
        }
    }

    private async Task HandleGroupCreateAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        HashSet<string> updatedChats,
        HashSet<string> accountChatIdSet,
        List<string> accountChatIds)
    {
        try
        {
            _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"ENTERING: accountId={accountId}, sender={pm.Sender}, msgId={pm.Headers.MessageId}, groupId={pm.Headers.GroupId}, contentLength={pm.Content?.Length}");

            var payload = System.Text.Json.JsonDocument.Parse(pm.Content);
            var root = payload.RootElement;

            var groupId = root.GetProperty("group_id").GetString() ?? pm.Headers.GroupId;
            var groupName = root.GetProperty("group_name").GetString() ?? "Group Chat";
            var version = root.TryGetProperty("version", out var vEl) ? vEl.GetInt32() : 1;
            var groupPublicKey = root.TryGetProperty("group_public_key", out var kEl) ? kEl.GetString() : null;

            var members = root.TryGetProperty("members", out var mEl)
                ? mEl.EnumerateArray().Select(e => e.GetString()!).ToList()
                : new List<string>();

            var admins = root.TryGetProperty("admins", out var aEl)
                ? aEl.EnumerateArray().Select(e => e.GetString()!).ToList()
                : new List<string>();

            _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"Parsed: groupId={groupId}, groupName={groupName}, version={version}, " +
                $"members=[{string.Join(",", members)}], admins=[{string.Join(",", admins)}], " +
                $"hasPubKey={!string.IsNullOrEmpty(groupPublicKey)}, sender={pm.Sender}");

            if (string.IsNullOrEmpty(groupId))
            {
                _fileLogger.Write("WARN", "HandleGroupCreateAsync", "groupId is empty, returning");
                return;
            }

            // Check if group chat already exists — skip if deleted (tombstone)
            var existingChat = await db.Chats.FirstOrDefaultAsync(c => c.GroupId == groupId);
            _fileLogger.Write("DEBUG", "HandleGroupCreateAsync", $"existingChat check: groupId={groupId}, existingChat={(existingChat != null ? "FOUND" : "NULL")}, deleted={existingChat?.Deleted}");
            if (existingChat != null && existingChat.Deleted)
            {
                _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"Chat is deleted tombstone, returning early for groupId={groupId}");
                return;
            }

            // Create chat if it doesn't exist yet (may have been auto-created
            // by a regular message before the group-create system message arrived)
            if (existingChat == null)
            {
                _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"Creating new Chat for groupId={groupId}");
                var newGroupChat = new Chat
                {
                    ChatId = Guid.NewGuid().ToString(),
                    Type = ChatType.Group,
                    GroupId = groupId,
                    Name = groupName,
                    AccountId = accountId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastActivityAt = pm.Headers.Timestamp
                };
                db.Chats.Add(newGroupChat);
                existingChat = newGroupChat;
            }
            else
            {
                _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"Chat exists, updating metadata for groupId={groupId}");
                // Chat row exists but may be incomplete — ensure metadata is correct
                if (existingChat.Type != ChatType.Group)
                    existingChat.Type = ChatType.Group;
                if (string.IsNullOrEmpty(existingChat.Name) || existingChat.Name == "Group Chat")
                    existingChat.Name = groupName;
                existingChat.Deleted = false;
                existingChat.LastActivityAt = pm.Headers.Timestamp;
            }

            // Create group state if not already present
            var existingGroup = await db.Groups.FindAsync(groupId);
            if (existingGroup == null)
            {
                db.Groups.Add(new ChatGroup
                {
                    GroupId = groupId,
                    Name = groupName,
                    Version = version,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }

            // Store group key pair if provided and not already stored
            var groupPrivateKey = root.TryGetProperty("group_private_key", out var pkEl) ? pkEl.GetString() : null;

            if (!string.IsNullOrEmpty(groupPublicKey))
            {
                var existingKey = await db.GroupKeyPairs.FindAsync(groupId);
                if (existingKey == null)
                {
                    var fingerprint = string.Empty;
                    try { fingerprint = _pgpService.GetFingerprint(groupPublicKey); } catch { }

                    db.GroupKeyPairs.Add(new GroupKeyPair
                    {
                        GroupId = groupId,
                        PublicKey = groupPublicKey,
                        PrivateKey = groupPrivateKey ?? string.Empty,
                        Fingerprint = fingerprint,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
            }

            // Add members that aren't already in the group
            foreach (var email in members)
            {
                if (string.IsNullOrEmpty(email)) continue;
                var existingMember = await db.GroupMembers.FindAsync(groupId, email);
                if (existingMember != null) continue;

                var role = admins.Contains(email, StringComparer.OrdinalIgnoreCase)
                    ? GroupRole.Admin
                    : GroupRole.Member;

                db.GroupMembers.Add(new GroupMember
                {
                    GroupId = groupId,
                    MemberEmail = email,
                    Role = role,
                    AddedAt = pm.Headers.Timestamp,
                    AddedBy = pm.Sender,
                    NameColor = GroupPalette.PickColor(email)
                });
            }

            // Add system message — use stable ID based on groupId so that multiple group-create
            // emails (one per member) produce exactly one notification regardless of how many
            // copies arrive in the sender's inbox.
            var stableSystemMsgId = $"sys-group-create-{groupId}";
            if (!await db.Messages.AnyAsync(m => m.MessageId == stableSystemMsgId))
            {
                db.Messages.Add(new ChatMessage
                {
                    MessageId = stableSystemMsgId,
                    ChatId = existingChat.ChatId,
                    Sender = pm.Sender,
                    Content = $"Группа \"{groupName}\" создана",
                    Timestamp = pm.Headers.Timestamp,
                    DisplayTimestamp = pm.Headers.Timestamp,
                    ReceivedAt = DateTimeOffset.UtcNow,
                    Status = MessageStatus.Sent
                });
            }

            accountChatIdSet.Add(existingChat.ChatId);
            accountChatIds.Add(existingChat.ChatId);
            updatedChats.Add(existingChat.ChatId);

            _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"SUCCESS: groupId={groupId} added to updatedChats. " +
                $"updatedChats now contains [{string.Join(",", updatedChats)}]");
            _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"SUCCESS: group {groupId} ({groupName}) created");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "HandleGroupCreateAsync", $"EXCEPTION: {ex.Message}\n{ex.StackTrace}");
            _fileLogger.Write("ERROR", "HandleGroupCreateAsync", $"EXCEPTION: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private async Task HandleGroupDeleteAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        HashSet<string> updatedChats,
        HashSet<string> accountChatIdSet,
        List<string> accountChatIds)
    {
        try
        {
            var payload = System.Text.Json.JsonDocument.Parse(pm.Content);
            var root = payload.RootElement;
            var groupId = root.GetProperty("group_id").GetString() ?? pm.Headers.GroupId;
            var deletedBy = root.TryGetProperty("deleted_by", out var dEl) ? dEl.GetString() : pm.Sender;

            if (string.IsNullOrEmpty(groupId)) return;

            var chat = await db.Chats.FirstOrDefaultAsync(c => c.GroupId == groupId);
            if (chat == null) return;

            if (!chat.Deleted)
            {
                chat.Deleted = true;
                chat.LastActivityAt = pm.Headers.Timestamp;
                updatedChats.Add(chat.ChatId);
            }

            // Always store the system message so the dedup check works on subsequent syncs,
            // even if the chat was already marked deleted (e.g. by the local UI action).
            if (pm.Headers.MessageId != null &&
                !await db.Messages.AnyAsync(m => m.MessageId == pm.Headers.MessageId))
            {
            db.Messages.Add(new ChatMessage
                {
                    MessageId = pm.Headers.MessageId,
                    ChatId = chat.ChatId,
                    Sender = deletedBy ?? pm.Sender,
                    Content = $"Группа \"{chat.Name}\" удалена администратором",
                    Timestamp = pm.Headers.Timestamp,
                    DisplayTimestamp = pm.Headers.Timestamp,
                    ReceivedAt = DateTimeOffset.UtcNow,
                    Status = MessageStatus.Sent
                });
            }

            _fileLogger.Write("INFO", "IncomingMessageService", $"Group {groupId} deleted by {deletedBy}");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "IncomingMessageService", $"Failed to handle group-delete: {ex.Message}");
        }
    }

    private async Task HandleGroupLeaveAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        HashSet<string> updatedChats,
        HashSet<string> accountChatIdSet,
        List<string> accountChatIds)
    {
        try
        {
            var payload = System.Text.Json.JsonDocument.Parse(pm.Content);
            var root = payload.RootElement;
            var groupId = root.GetProperty("group_id").GetString() ?? pm.Headers.GroupId;
            var leavingEmail = root.TryGetProperty("leaving_email", out var lEl) ? lEl.GetString() : pm.Sender;

            if (string.IsNullOrEmpty(groupId)) return;

            var chat = await db.Chats.FirstOrDefaultAsync(c => c.GroupId == groupId);
            if (chat == null) return;

            // Remove member from group
            var member = await db.GroupMembers.FindAsync(groupId, leavingEmail);
            if (member != null)
            {
                db.GroupMembers.Remove(member);
            }

            db.Messages.Add(new ChatMessage
            {
                MessageId = pm.Headers.MessageId ?? Guid.NewGuid().ToString(),
                ChatId = chat.ChatId,
                Sender = leavingEmail ?? pm.Sender,
                Content = $"{leavingEmail?.Split('@')[0] ?? "Участник"} покинул(а) группу",
                Timestamp = pm.Headers.Timestamp,
                DisplayTimestamp = pm.Headers.Timestamp,
                ReceivedAt = DateTimeOffset.UtcNow,
                Status = MessageStatus.Sent
            });

            updatedChats.Add(chat.ChatId);
            _fileLogger.Write("INFO", "IncomingMessageService", $"Member {leavingEmail} left group {groupId}");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "IncomingMessageService", $"Failed to handle group-leave: {ex.Message}");
        }
    }

    private async Task HandleChatDeleteAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        HashSet<string> updatedChats,
        HashSet<string> accountChatIdSet,
        List<string> accountChatIds)
    {
        try
        {
            var payload = System.Text.Json.JsonDocument.Parse(pm.Content);
            var root = payload.RootElement;
            var chatId = root.GetProperty("chat_id").GetString() ?? pm.Headers.GroupId;
            var deletedBy = root.TryGetProperty("deleted_by", out var dEl) ? dEl.GetString() : pm.Sender;

            if (string.IsNullOrEmpty(chatId)) return;

            var chat = await db.Chats.FindAsync(chatId);
            if (chat == null || chat.Deleted) return;

            chat.Deleted = true;
            chat.LastActivityAt = pm.Headers.Timestamp;

            db.Messages.Add(new ChatMessage
            {
                MessageId = pm.Headers.MessageId ?? Guid.NewGuid().ToString(),
                ChatId = chatId,
                Sender = deletedBy ?? pm.Sender,
                Content = "Чат удалён",
                Timestamp = pm.Headers.Timestamp,
                DisplayTimestamp = pm.Headers.Timestamp,
                ReceivedAt = DateTimeOffset.UtcNow,
                Status = MessageStatus.Sent
            });

            updatedChats.Add(chatId);
            _fileLogger.Write("INFO", "IncomingMessageService", $"Chat {chatId} deleted by {deletedBy}");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "IncomingMessageService", $"Failed to handle chat-delete: {ex.Message}");
        }
    }
}
