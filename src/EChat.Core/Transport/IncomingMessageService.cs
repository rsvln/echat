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
    private readonly InviteService _inviteService;

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
        FileLogger fileLogger,
        InviteService inviteService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _chatEvents = chatEvents;
        _pgpService = pgpService;
        _accountConfig = accountConfig;
        _fileLogger = fileLogger;
        _inviteService = inviteService;
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

        // Pre-load blocked senders so we can filter the entire batch in O(1)
        var blockedSenders = await db.Contacts
            .Where(c => c.AccountId == accountId && c.IsBlocked)
            .Select(c => c.Email)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase);

        if (blockedSenders.Count > 0)
        {
            var before = parsed.Count;
            parsed = parsed.Where(p => !blockedSenders.Contains(p.Sender)).ToList();
            var dropped = before - parsed.Count;
            if (dropped > 0)
                _fileLogger.Write("INFO", "SaveAsync", $"Dropped {dropped} message(s) from blocked sender(s)");
            if (parsed.Count == 0) return;
        }

        _fileLogger.Write("INFO", "SaveAsync", $"accountEmail={accountEmail}, accountId={accountId}");

        // Full email used as log prefix so multi-account logs are immediately readable.
        var logAcct = accountEmail ?? accountId[..Math.Min(8, accountId.Length)];

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

        _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] accountChatIds count={accountChatIds.Count}");
        if (accountChatIds.Count > 0)
        {
            _fileLogger.Write("DEBUG", "SaveAsync", $"[{logAcct}] First few chatIds: {string.Join(", ", accountChatIds.Take(5))}");
        }

        // ── Handle read receipts (Chat-Read-Of) ─────────────────────────────
        // ExecuteUpdateAsync commits directly to SQLite without going through SaveChangesAsync,
        // so we fire NotifyChatUpdated immediately after — UI refresh is NOT gated on the
        // SaveChangesAsync at the bottom which may fail for unrelated reasons (e.g. duplicate
        // regular message arriving in the same batch).
        foreach (var pm in parsed.Where(m => m.Headers.ReadOf != null && m.Headers.ReadOf.Count > 0))
        {
            var ids = pm.Headers.ReadOf!;
            // Mark our outgoing messages as Read — only messages in this account's chats
            var affected = await db.Messages
                .Where(m => ids.Contains(m.MessageId) && accountChatIds.Contains(m.ChatId))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Read));
            if (affected > 0)
            {
                var affectedChats = await db.Messages
                    .Where(m => ids.Contains(m.MessageId) && accountChatIds.Contains(m.ChatId))
                    .Select(m => m.ChatId)
                    .Distinct()
                    .ToListAsync();
                foreach (var cid in affectedChats)
                {
                    updatedChats.Add(cid);
                    // Notify immediately — status is already committed by ExecuteUpdateAsync.
                    // This ensures the UI updates even if SaveChangesAsync later fails for
                    // other items in this batch (e.g. a regular message with a duplicate key).
                    _chatEvents.NotifyChatUpdated(cid);
                }
                _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] Read receipt processed: {affected} message(s) marked Read, chats=[{string.Join(",", affectedChats)}]");
            }
            else
            {
                _fileLogger.Write("WARN", "SaveAsync", $"[{logAcct}] Read receipt arrived but found 0 matching messages in DB. ReadOf=[{string.Join(",", ids)}], accountChatIds.Count={accountChatIds.Count}");
            }
        }

        // ── Handle edits (Chat-Edit-Of) ──────────────────────────────────────
        foreach (var pm in parsed.Where(m => m.Headers.EditOf != null))
        {
            var target = await db.Messages
                .FirstOrDefaultAsync(m => m.MessageId == pm.Headers.EditOf && accountChatIds.Contains(m.ChatId));
            if (target == null) continue;
            target.Content = pm.Content; target.FormattedContent = HtmlFormatter.Format(pm.Content); target.IsEdited = true;
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
            // Exception: group-delete must be re-processed if the chat is currently not deleted,
            // to handle cases where a stale group-create resurrected the chat after the delete.
            bool isDuplicate = pm.Headers.MessageId != null &&
                await db.Messages.AnyAsync(m => m.MessageId == pm.Headers.MessageId);

            if (isDuplicate && pm.Headers.SystemType != "group-delete" && pm.Headers.SystemType != "group-create")
            {
                _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] System message {pm.Headers.SystemType} msgId={pm.Headers.MessageId} already in DB, skipping");
                continue;
            }

            // For group-delete, check if the target chat is currently deleted.
            // If it's not deleted, re-apply the delete to fix resurrection by stale emails.
            if (isDuplicate && pm.Headers.SystemType == "group-delete")
            {
                var deleteGroupId = pm.Headers.GroupId;
                if (string.IsNullOrEmpty(deleteGroupId) && !string.IsNullOrEmpty(pm.Content))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(pm.Content);
                        deleteGroupId = doc.RootElement.GetProperty("group_id").GetString();
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(deleteGroupId))
                {
                    var targetChat = await db.Chats.FirstOrDefaultAsync(c => c.GroupId == deleteGroupId && c.AccountId == accountId);
                    if (targetChat != null && targetChat.Deleted)
                    {
                        _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] System message group-delete msgId={pm.Headers.MessageId} already in DB and chat is deleted, skipping");
                        continue;
                    }
                    _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] System message group-delete msgId={pm.Headers.MessageId} already in DB but chat is NOT deleted, re-applying delete");
                }
            }

            // For group-create, if it's a duplicate but the chat name is still the fallback "Group Chat",
            // re-process it to update the metadata (name, members, etc.).
            if (isDuplicate && pm.Headers.SystemType == "group-create")
            {
                var createGroupId = pm.Headers.GroupId;
                if (string.IsNullOrEmpty(createGroupId) && !string.IsNullOrEmpty(pm.Content))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(pm.Content);
                        createGroupId = doc.RootElement.GetProperty("group_id").GetString();
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(createGroupId))
                {
                    var targetChat = await db.Chats.FirstOrDefaultAsync(c => c.GroupId == createGroupId && c.AccountId == accountId);
                    if (targetChat != null && targetChat.Name != "Group Chat" && !string.IsNullOrEmpty(targetChat.Name))
                    {
                        _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] System message group-create msgId={pm.Headers.MessageId} already in DB and chat name is set, skipping");
                        continue;
                    }
                    _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] System message group-create msgId={pm.Headers.MessageId} already in DB but chat name is missing/fallback, re-applying create");
                }
            }

            _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] Processing system message: type={pm.Headers.SystemType}, msgId={pm.Headers.MessageId}, sender={pm.Sender}, groupId={pm.Headers.GroupId}");

            switch (pm.Headers.SystemType)
            {
                case "group-create":
                    await HandleGroupCreateAsync(db, pm, accountId, accountEmail, updatedChats, accountChatIdSet, accountChatIds);
                    break;
                case "group-delete":
                    await HandleGroupDeleteAsync(db, pm, accountId, accountEmail, updatedChats, accountChatIdSet, accountChatIds);
                    break;
                case "group-leave":
                    await HandleGroupLeaveAsync(db, pm, accountId, accountEmail, updatedChats, accountChatIdSet, accountChatIds);
                    break;
                case "chat-delete":
                    await HandleChatDeleteAsync(db, pm, accountId, accountEmail, updatedChats, accountChatIdSet, accountChatIds);
                    break;
                case "group-member-add":
                    await HandleGroupMemberAddAsync(db, pm, accountId, accountEmail, updatedChats);
                    break;
                case "group-member-remove":
                    await HandleGroupMemberRemoveAsync(db, pm, accountId, accountEmail, updatedChats);
                    break;
                case "group-rename":
                    await HandleGroupRenameAsync(db, pm, accountId, accountEmail, updatedChats);
                    break;
            }
        }

        _fileLogger.Write("DEBUG", "SaveAsync", $"[{logAcct}] After system messages: updatedChats=[{string.Join(",", updatedChats)}]");

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

            _fileLogger.Write("DEBUG", "SaveAsync", $"[{logAcct}] Msg ROUTING: id={pm.Headers.MessageId}, sender={pm.Sender}, accountEmail={accountEmail}, isSentSync={isSentSync}, chatPartner={chatPartner}, recipients={string.Join(",", pm.Recipients)}");

            // ── Invite / key-exchange ────────────────────────────────────────
            if (!isSentSync && !string.IsNullOrEmpty(pm.Headers.InviteToken))
                await HandleInviteTokenAsync(db, pm, accountId, accountEmail ?? "", logAcct);

            if (string.IsNullOrEmpty(pm.Headers.GroupId) && string.IsNullOrEmpty(chatPartner))
            {
                _fileLogger.Write("WARN", "SaveAsync", $"[{logAcct}] Skipping msg {pm.Headers.MessageId}: no groupId and no chatPartner");
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
                _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] Duplicate msg {pm.Headers.MessageId}, inMemory={inMemoryDuplicate}, inDb={alreadyInDb}");
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

                    // Check for any deleted tombstone before creating a new chat from a regular message.
                    // This prevents stale IMAP messages from resurrecting a deleted group.
                    // Only HandleGroupCreateAsync with a higher version can resurrect the chat.
                    if (groupChat == null)
                    {
                        var tombstone = await db.Chats
                            .FirstOrDefaultAsync(c => c.GroupId == pm.Headers.GroupId &&
                                                      c.AccountId == accountId &&
                                                      c.Deleted);
                        if (tombstone != null)
                        {
                            _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] Deleted tombstone exists for groupId={pm.Headers.GroupId} (version={tombstone.TombstoneVersion}), skipping chat creation for regular message");
                            continue; // Skip this message entirely — group is deleted
                        }
                    }

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
                                contact = await db.Contacts.FindAsync(accountId, chatPartner);

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
                                        AccountId = accountId,
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

            _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] ADDING msg to chat: id={pm.Headers.MessageId}, chatId={chatId}, sender={pm.Sender}, isSentSync={isSentSync}");

            db.Messages.Add(new ChatMessage
            {
                MessageId = pm.Headers.MessageId,
                ChatId = chatId,
                Sender = pm.Sender,
                Content = pm.Content,
                FormattedContent = HtmlFormatter.Format(pm.Content),
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
            _fileLogger.Write("DEBUG", "SaveAsync", $"[{logAcct}] Msg {pm.Headers.MessageId}: attachments={pm.Attachments?.Count ?? 0}");
            if (pm.Attachments != null && pm.Attachments.Count > 0)
            {
                var attDir = Path.Combine(_fileLogger.AppDir, "attachments");
                _fileLogger.Write("DEBUG", "SaveAsync", $"[{logAcct}] Saving {pm.Attachments.Count} attachment(s) to {attDir}");
                Directory.CreateDirectory(attDir);
                foreach (var att in pm.Attachments)
                {
                    try
                    {
                        var safe = Path.GetFileName(att.FileName); // strip any path component
                        var attPath = Path.Combine(attDir, $"{pm.Headers.MessageId}_{safe}");
                        await File.WriteAllBytesAsync(attPath, att.Data);
                        db.Attachments.Add(new Attachment
                        {
                            Id = Guid.NewGuid().ToString(),
                            MessageId = pm.Headers.MessageId,
                            FileName = safe,
                            ContentType = att.ContentType,
                            Size = att.Size,
                            FilePath = Path.GetFileName(attPath),
                        });
                    }
                    catch (Exception ex)
                    {
                        _fileLogger.Write("WARN", "SaveAsync", $"[{logAcct}] Failed to save attachment {att.FileName} for msg {pm.Headers.MessageId}: {ex.Message}");
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
                var senderName = pm.Sender ?? "Unknown";
                if (batchContacts.TryGetValue(pm.Sender ?? "", out var sc) &&
                    !string.IsNullOrEmpty(sc.DisplayName))
                    senderName = sc.DisplayName;
                var rawPreview = string.IsNullOrWhiteSpace(pm.Content)
                    ? (pm.Attachments?.Count > 0 ? "📎 Attachment" : "New message")
                    : pm.Content;
                var stripped = HtmlFormatter.StripFormatting(rawPreview);
                var preview = stripped.Length > 80 ? stripped[..80] + "…" : stripped;
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
                    _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] Saved {updatedChats.Count} chats successfully");
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
                        _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] UNIQUE constraint on early attempt — already saved by concurrent call. Treating as success.");
                        db.ChangeTracker.Clear();
                        uniqueConstraintHandled = true;
                        break;
                    }
                    lastEx = ex;
                    var innerMsg = ex.InnerException != null ? $" Inner: {ex.InnerException.Message}" : "";
                    _fileLogger.Write("WARN", "SaveAsync", $"[{logAcct}] Save attempt {attempt} failed: {ex.Message}{innerMsg}. Retrying...");
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
                    _fileLogger.Write("ERROR", "SaveAsync", $"[{logAcct}] Save failed after {attempt} attempts: {ex.Message}{innerMsg}{sqlMsg}");

                    if (sqliteCode == 19)
                    {
                        _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] UNIQUE constraint - message likely already saved by another device. Treating as success.");
                        uniqueConstraintHandled = true;
                        db.ChangeTracker.Clear();
                        break;
                    }

                    _fileLogger.Write("ERROR", "IncomingMessageService", $"[{logAcct}] Failed to save incoming messages batch: {ex.Message}");
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
            _fileLogger.Write("INFO", "SaveAsync", $"[{logAcct}] No chats to update");
        }
    }

    // ── Invite / key-exchange ────────────────────────────────────────────────

    private async Task HandleInviteTokenAsync(ChatDbContext db, ParsedMessage pm,
        string accountId, string accountEmail, string logAcct)
    {
        var token     = pm.Headers.InviteToken!;
        var hmac      = pm.Headers.InviteHmac ?? "";
        var senderKey = pm.Headers.SenderPublicKey;

        bool hmacValid = !string.IsNullOrEmpty(senderKey) &&
            InviteService.VerifyHmac(token, hmac, senderKey, accountEmail);

        _fileLogger.Write("INFO", "HandleInvite",
            $"[{logAcct}] InviteToken from {pm.Sender}: hmacValid={hmacValid}, hasSenderKey={!string.IsNullOrEmpty(senderKey)}");

        if (!hmacValid) return;

        bool consumed = await _inviteService.VerifyAndConsumeAsync(token, accountId);
        _fileLogger.Write(consumed ? "INFO" : "WARN", "HandleInvite",
            $"[{logAcct}] Token consumed={consumed}");

        if (!consumed || string.IsNullOrEmpty(senderKey) || string.IsNullOrEmpty(pm.Sender)) return;

        // Token is valid and burned — trust the sender's public key
        var contact = await db.Contacts.FindAsync(accountId, pm.Sender);
        if (contact == null)
        {
            contact = new Contact
            {
                AccountId   = accountId,
                Email       = pm.Sender,
                DisplayName = pm.Sender.Split('@')[0],
                PublicKey   = senderKey,
                Verified    = true
            };
            db.Contacts.Add(contact);
        }
        else
        {
            contact.PublicKey = senderKey;
            contact.Verified  = true;
        }
        _fileLogger.Write("INFO", "HandleInvite",
            $"[{logAcct}] Contact {pm.Sender} marked Verified with sender's public key");
    }

    private async Task HandleGroupCreateAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        string? accountEmail,
        HashSet<string> updatedChats,
        HashSet<string> accountChatIdSet,
        List<string> accountChatIds)
    {
        var logAcct = accountEmail ?? accountId[..Math.Min(8, accountId.Length)];
        try
        {
            _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"[{logAcct}] ENTERING: accountId={accountId}, sender={pm.Sender}, msgId={pm.Headers.MessageId}, groupId={pm.Headers.GroupId}, contentLength={pm.Content?.Length}");

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

            // member_names: optional dict email→displayName sent by the group creator
            var memberNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("member_names", out var mnEl) && mnEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in mnEl.EnumerateObject())
                    if (!string.IsNullOrEmpty(prop.Value.GetString()))
                        memberNames[prop.Name] = prop.Value.GetString()!;
            }

            _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"[{logAcct}] Parsed: groupId={groupId}, groupName={groupName}, version={version}, " +
                $"members=[{string.Join(",", members)}], admins=[{string.Join(",", admins)}], " +
                $"hasPubKey={!string.IsNullOrEmpty(groupPublicKey)}, sender={pm.Sender}");

            if (string.IsNullOrEmpty(groupId))
            {
                _fileLogger.Write("WARN", "HandleGroupCreateAsync", $"[{logAcct}] groupId is empty, returning");
                return;
            }

            // Check if group chat already exists — may be a deleted tombstone
            var existingChat = await db.Chats.FirstOrDefaultAsync(c => c.GroupId == groupId && c.AccountId == accountId);
            _fileLogger.Write("DEBUG", "HandleGroupCreateAsync", $"[{logAcct}] existingChat check: groupId={groupId}, existingChat={(existingChat != null ? "FOUND" : "NULL")}, deleted={existingChat?.Deleted}");

            if (existingChat != null && existingChat.Deleted)
            {
                // group-delete sets TombstoneVersion to int.MaxValue as a permanent-delete
                // marker.  No amount of re-invites or higher versions should ever resurrect
                // a chat that was explicitly deleted for everyone.
                if (existingChat.TombstoneVersion == int.MaxValue)
                {
                    _fileLogger.Write("INFO", "HandleGroupCreateAsync",
                        $"[{logAcct}] Chat is permanently deleted (group-delete tombstone), refusing resurrection for groupId={groupId}");
                    return;
                }

                // Determine the version threshold: only resurrect if the incoming group-create
                // is STRICTLY NEWER than the event that tombstoned this chat.  This prevents
                // stale group-create emails (from before the user was removed) from resurrecting
                // the chat during IMAP sync, while still allowing legitimate re-invites which
                // carry a higher version.
                var existingGroupForVersionCheck = await db.Groups.FindAsync(groupId);
                int currentDbVersion = existingGroupForVersionCheck?.Version ?? 0;
                int compareAgainst = existingChat.TombstoneVersion ?? currentDbVersion;

                bool imInMembers = !string.IsNullOrEmpty(accountEmail) &&
                    members.Contains(accountEmail, StringComparer.OrdinalIgnoreCase);

                if (version <= compareAgainst)
                {
                    _fileLogger.Write("INFO", "HandleGroupCreateAsync",
                        $"[{logAcct}] Chat is deleted tombstone and incoming version {version} <= tombstoneVersion {compareAgainst}, skipping resurrection (imInMembers={imInMembers})");
                    return;
                }

                _fileLogger.Write("INFO", "HandleGroupCreateAsync",
                    $"[{logAcct}] Restoring deleted tombstone for groupId={groupId}: incoming version {version} > tombstoneVersion {compareAgainst}, imInMembers={imInMembers}");
            }

            // Create chat if it doesn't exist yet (may have been auto-created
            // by a regular message before the group-create system message arrived)
            if (existingChat == null)
            {
                _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"[{logAcct}] Creating new Chat for groupId={groupId}");
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
                _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"[{logAcct}] Chat exists, updating metadata for groupId={groupId}");
                // Chat row exists but may be incomplete — ensure metadata is correct
                if (existingChat.Type != ChatType.Group)
                    existingChat.Type = ChatType.Group;
                if (string.IsNullOrEmpty(existingChat.Name) || existingChat.Name == "Group Chat")
                    existingChat.Name = groupName;
                existingChat.Deleted = false;
                existingChat.LastActivityAt = pm.Headers.Timestamp;
            }

            // Create/update group state.
            // Use version to guard against stale group-create copies re-adding removed members:
            // only update the member list when the received version is >= the current DB version.
            var existingGroup = await db.Groups.FindAsync(groupId);
            bool shouldUpdateMembers = existingGroup == null || version >= existingGroup.Version;

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
            else if (version > existingGroup.Version)
            {
                existingGroup.Version = version;
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

            // Add members that aren't already in the group — only if version is current.
            // A stale group-create (older version) must NOT re-add members that were removed after.
            if (shouldUpdateMembers)
            {
                foreach (var email in members)
                {
                    if (string.IsNullOrEmpty(email)) continue;
                    var existingMember = await db.GroupMembers.FindAsync(groupId, email);

                    var role = admins.Contains(email, StringComparer.OrdinalIgnoreCase)
                        ? GroupRole.Admin
                        : GroupRole.Member;

                    memberNames.TryGetValue(email, out var displayName);

                    if (existingMember != null)
                    {
                        // Update display name if we now have a better value
                        if (!string.IsNullOrEmpty(displayName) && existingMember.DisplayName != displayName)
                            existingMember.DisplayName = displayName;
                        continue;
                    }

                    db.GroupMembers.Add(new GroupMember
                    {
                        GroupId = groupId,
                        MemberEmail = email,
                        Role = role,
                        AddedAt = pm.Headers.Timestamp,
                        AddedBy = pm.Sender,
                        NameColor = GroupPalette.PickColor(email),
                        DisplayName = string.IsNullOrEmpty(displayName) ? null : displayName
                    });
                }
            }
            else
            {
                _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"[{logAcct}] Skipping member update: received version {version} < current DB version {existingGroup!.Version}");
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
                    Content = $"Group \"{groupName}\" created",
                    Timestamp = pm.Headers.Timestamp,
                    DisplayTimestamp = pm.Headers.Timestamp,
                    ReceivedAt = DateTimeOffset.UtcNow,
                    IsSystem = true,
                    Status = MessageStatus.Sent
                });
            }

            accountChatIdSet.Add(existingChat.ChatId);
            accountChatIds.Add(existingChat.ChatId);
            updatedChats.Add(existingChat.ChatId);

            _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"[{logAcct}] SUCCESS: groupId={groupId} added to updatedChats. " +
                $"updatedChats now contains [{string.Join(",", updatedChats)}]");
            _fileLogger.Write("INFO", "HandleGroupCreateAsync", $"[{logAcct}] SUCCESS: group {groupId} ({groupName}) created");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "HandleGroupCreateAsync", $"[{logAcct}] EXCEPTION: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private async Task HandleGroupDeleteAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        string? accountEmail,
        HashSet<string> updatedChats,
        HashSet<string> accountChatIdSet,
        List<string> accountChatIds)
    {
        var logAcct = accountEmail ?? accountId[..Math.Min(8, accountId.Length)];
        try
        {
            var payload = System.Text.Json.JsonDocument.Parse(pm.Content);
            var root = payload.RootElement;
            var groupId = root.GetProperty("group_id").GetString() ?? pm.Headers.GroupId;
            var deletedBy = root.TryGetProperty("deleted_by", out var dEl) ? dEl.GetString() : pm.Sender;

            if (string.IsNullOrEmpty(groupId)) return;

            var chat = await db.Chats.FirstOrDefaultAsync(c => c.GroupId == groupId && c.AccountId == accountId);
            if (chat == null)
            {
                // group-delete arrived before group-create (IMAP ordering race).
                // Create a permanent tombstone so future group-create messages are refused.
                chat = new Chat
                {
                    ChatId = Guid.NewGuid().ToString(),
                    Type = ChatType.Group,
                    GroupId = groupId,
                    Name = "Deleted Group",
                    AccountId = accountId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastActivityAt = pm.Headers.Timestamp,
                    Deleted = true,
                    TombstoneVersion = int.MaxValue
                };
                db.Chats.Add(chat);
                _fileLogger.Write("INFO", "HandleGroupDeleteAsync", $"[{logAcct}] Chat not found for group-delete, created permanent tombstone for groupId={groupId}");
                return;
            }

            if (!chat.Deleted)
            {
                chat.Deleted = true;
                // Mark as permanently deleted — no future group-create (regardless of version)
                // should ever resurrect this chat.  int.MaxValue acts as an unbreachable ceiling.
                chat.TombstoneVersion = int.MaxValue;
                chat.LastActivityAt = pm.Headers.Timestamp;
                updatedChats.Add(chat.ChatId);
            }

            // Clean up group membership on receiver's side
            var membersToRemove = await db.GroupMembers
                .Where(m => m.GroupId == groupId)
                .ToListAsync();
            db.GroupMembers.RemoveRange(membersToRemove);

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
                    Content = $"Group \"{chat.Name}\" deleted by admin",
                    Timestamp = pm.Headers.Timestamp,
                    DisplayTimestamp = pm.Headers.Timestamp,
                    ReceivedAt = DateTimeOffset.UtcNow,
                    IsSystem = true,
                    Status = MessageStatus.Sent
                });
            }

            _fileLogger.Write("INFO", "HandleGroupDeleteAsync", $"[{logAcct}] Group {groupId} deleted by {deletedBy}");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "HandleGroupDeleteAsync", $"[{logAcct}] Failed to handle group-delete: {ex.Message}");
        }
    }

    private async Task HandleGroupLeaveAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        string? accountEmail,
        HashSet<string> updatedChats,
        HashSet<string> accountChatIdSet,
        List<string> accountChatIds)
    {
        var logAcct = accountEmail ?? accountId[..Math.Min(8, accountId.Length)];
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

            var leavingMember = leavingEmail != null ? await db.GroupMembers.FindAsync(groupId, leavingEmail) : null;
            var leavingContact = leavingEmail != null ? await db.Contacts.FindAsync(accountId, leavingEmail) : null;
            var leavingName = leavingMember?.DisplayName ?? leavingContact?.DisplayName ?? leavingEmail ?? "Member";

            db.Messages.Add(new ChatMessage
            {
                MessageId = pm.Headers.MessageId ?? Guid.NewGuid().ToString(),
                ChatId = chat.ChatId,
                Sender = leavingEmail ?? pm.Sender,
                Content = $"{leavingName} left the group",
                Timestamp = pm.Headers.Timestamp,
                DisplayTimestamp = pm.Headers.Timestamp,
                ReceivedAt = DateTimeOffset.UtcNow,
                IsSystem = true,
                Status = MessageStatus.Sent
            });

            updatedChats.Add(chat.ChatId);
            _fileLogger.Write("INFO", "HandleGroupLeaveAsync", $"[{logAcct}] Member {leavingEmail} left group {groupId}");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "HandleGroupLeaveAsync", $"[{logAcct}] Failed to handle group-leave: {ex.Message}");
        }
    }

    private async Task HandleGroupMemberAddAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        string? accountEmail,
        HashSet<string> updatedChats)
    {
        var logAcct = accountEmail ?? accountId[..Math.Min(8, accountId.Length)];
        try
        {
            var payload = System.Text.Json.JsonDocument.Parse(pm.Content);
            var root = payload.RootElement;
            var groupId = root.GetProperty("group_id").GetString() ?? pm.Headers.GroupId;
            var addedEmail = root.TryGetProperty("added_email", out var aEl) ? aEl.GetString() : null;
            var addedName = root.TryGetProperty("added_name", out var nEl) ? nEl.GetString() : null;
            var addedBy = root.TryGetProperty("added_by", out var bEl) ? bEl.GetString() : pm.Sender;

            if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(addedEmail)) return;

            var chat = await db.Chats.FirstOrDefaultAsync(c => c.GroupId == groupId && c.AccountId == accountId);
            if (chat == null) return;

            // If the added member is the current user and the chat was previously deleted
            // (tombstoned after a group-member-remove), resurrect it — they've been re-invited.
            // BUT never resurrect a permanently deleted chat (group-delete → TombstoneVersion=int.MaxValue).
            if (chat.Deleted && addedEmail.Equals(accountEmail, StringComparison.OrdinalIgnoreCase))
            {
                if (chat.TombstoneVersion == int.MaxValue)
                {
                    _fileLogger.Write("INFO", "HandleGroupMemberAddAsync",
                        $"[{logAcct}] Refusing resurrection: groupId={groupId} is permanently deleted (group-delete)");
                    return;
                }
                chat.Deleted = false;
                chat.TombstoneVersion = null;
                chat.LastActivityAt = pm.Headers.Timestamp;
                _fileLogger.Write("INFO", "HandleGroupMemberAddAsync",
                    $"[{logAcct}] Resurrected tombstoned chat for groupId={groupId} — current user re-added");
            }

            if (chat.Deleted) return;

            var existing = await db.GroupMembers.FindAsync(groupId, addedEmail);
            if (existing == null)
            {
                db.GroupMembers.Add(new GroupMember
                {
                    GroupId = groupId,
                    MemberEmail = addedEmail,
                    Role = GroupRole.Member,
                    AddedAt = pm.Headers.Timestamp,
                    AddedBy = addedBy,
                    NameColor = GroupPalette.PickColor(addedEmail),
                    DisplayName = string.IsNullOrEmpty(addedName) ? null : addedName
                });
            }
            else if (!string.IsNullOrEmpty(addedName) && existing.DisplayName != addedName)
            {
                existing.DisplayName = addedName;
            }

            // If I'm the one who added this member, ChatInfoModal already created a local system
            // message. Skip creating a duplicate here — this is just the self-CC copy arriving back.
            bool iSentThis = !string.IsNullOrEmpty(accountEmail) &&
                addedBy?.Equals(accountEmail, StringComparison.OrdinalIgnoreCase) == true;

            if (!iSentThis)
            {
                // Use name from protocol first, then fallback to Contacts
                var resolvedAddedName = !string.IsNullOrEmpty(addedName) ? addedName
                    : (await db.Contacts.FindAsync(accountId, addedEmail))?.DisplayName ?? addedEmail;
                var addedByContact = addedBy != null ? await db.Contacts.FindAsync(accountId, addedBy) : null;
                var addedByMember = addedBy != null ? await db.GroupMembers.FindAsync(groupId, addedBy) : null;
                var addedByName = addedByMember?.DisplayName ?? addedByContact?.DisplayName ?? addedBy ?? "Admin";

                db.Messages.Add(new ChatMessage
                {
                    MessageId = pm.Headers.MessageId ?? Guid.NewGuid().ToString(),
                    ChatId = chat.ChatId,
                    Sender = addedBy ?? pm.Sender,
                    Content = $"{resolvedAddedName} was added to the group by {addedByName}",
                    Timestamp = pm.Headers.Timestamp,
                    DisplayTimestamp = pm.Headers.Timestamp,
                    ReceivedAt = DateTimeOffset.UtcNow,
                    IsSystem = true,
                    Status = MessageStatus.Sent
                });
            }

            updatedChats.Add(chat.ChatId);
            _fileLogger.Write("INFO", "HandleGroupMemberAddAsync", $"[{logAcct}] Member {addedEmail} added to group {groupId} by {addedBy} (iSentThis={iSentThis})");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "HandleGroupMemberAddAsync", $"[{logAcct}] Failed to handle group-member-add: {ex.Message}");
        }
    }

    private async Task HandleGroupMemberRemoveAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        string? accountEmail,
        HashSet<string> updatedChats)
    {
        var logAcct = accountEmail ?? accountId[..Math.Min(8, accountId.Length)];
        try
        {
            var payload = System.Text.Json.JsonDocument.Parse(pm.Content);
            var root = payload.RootElement;
            var groupId = root.GetProperty("group_id").GetString() ?? pm.Headers.GroupId;
            var removedEmail = root.TryGetProperty("removed_email", out var rEl) ? rEl.GetString() : null;
            var removedNameProto = root.TryGetProperty("removed_name", out var rnEl) ? rnEl.GetString() : null;
            var removedBy = root.TryGetProperty("removed_by", out var bEl) ? bEl.GetString() : pm.Sender;

            if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(removedEmail)) return;

            var chat = await db.Chats.FirstOrDefaultAsync(c => c.GroupId == groupId && c.AccountId == accountId && !c.Deleted);
            if (chat == null) return;

            // If I'm the removed member — mark chat as deleted
            if (removedEmail.Equals(accountEmail, StringComparison.OrdinalIgnoreCase))
            {
                chat.Deleted = true;
                // Record the shared ChatGroup.Version at the moment of tombstoning so that
                // HandleGroupCreateAsync can compare incoming version > TombstoneVersion (not
                // against the current DB version, which keeps incrementing with each admin op).
                var groupForVersion = await db.Groups.FindAsync(groupId);
                chat.TombstoneVersion = groupForVersion?.Version ?? 0;
                _fileLogger.Write("INFO", "HandleGroupMemberRemoveAsync",
                    $"[{logAcct}] I was removed from group {groupId}; tombstoneVersion={chat.TombstoneVersion}");
                var selfMember = await db.GroupMembers.FindAsync(groupId, removedEmail);
                if (selfMember != null) db.GroupMembers.Remove(selfMember);
                updatedChats.Add(chat.ChatId);
                return;
            }

            // Otherwise remove that member from my local list
            var member = await db.GroupMembers.FindAsync(groupId, removedEmail);
            if (member != null) db.GroupMembers.Remove(member);

            // If I'm the one who removed the member, ChatInfoModal already created a local system
            // message. Skip creating a duplicate here — this is just the self-CC copy arriving back.
            bool iSentThis = !string.IsNullOrEmpty(accountEmail) &&
                removedBy?.Equals(accountEmail, StringComparison.OrdinalIgnoreCase) == true;

            if (!iSentThis)
            {
                var removedContact = await db.Contacts.FindAsync(accountId, removedEmail);
                var removedName = !string.IsNullOrEmpty(removedNameProto) ? removedNameProto
                    : member?.DisplayName ?? removedContact?.DisplayName ?? removedEmail;

                db.Messages.Add(new ChatMessage
                {
                    MessageId = pm.Headers.MessageId ?? Guid.NewGuid().ToString(),
                    ChatId = chat.ChatId,
                    Sender = removedBy ?? pm.Sender,
                    Content = $"{removedName} was removed from the group",
                    Timestamp = pm.Headers.Timestamp,
                    DisplayTimestamp = pm.Headers.Timestamp,
                    ReceivedAt = DateTimeOffset.UtcNow,
                    IsSystem = true,
                    Status = MessageStatus.Sent
                });
            }

            updatedChats.Add(chat.ChatId);
            _fileLogger.Write("INFO", "HandleGroupMemberRemoveAsync", $"[{logAcct}] Member {removedEmail} removed from group {groupId} by {removedBy} (iSentThis={iSentThis})");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "HandleGroupMemberRemoveAsync", $"[{logAcct}] Failed to handle group-member-remove: {ex.Message}");
        }
    }

    private async Task HandleGroupRenameAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        string? accountEmail,
        HashSet<string> updatedChats)
    {
        var logAcct = accountEmail ?? accountId[..Math.Min(8, accountId.Length)];
        try
        {
            var payload = System.Text.Json.JsonDocument.Parse(pm.Content);
            var root = payload.RootElement;
            var groupId = root.GetProperty("group_id").GetString() ?? pm.Headers.GroupId;
            var newName = root.TryGetProperty("new_name", out var nnEl) ? nnEl.GetString() : null;
            var renamedBy = root.TryGetProperty("renamed_by", out var rbEl) ? rbEl.GetString() : pm.Sender;

            if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(newName)) return;

            var chat = await db.Chats.FirstOrDefaultAsync(c => c.GroupId == groupId && c.AccountId == accountId && !c.Deleted);
            if (chat == null) return;

            var oldName = chat.Name;
            chat.Name = newName;

            var group = await db.Groups.FindAsync(groupId);
            if (group != null) group.Name = newName;

            // Skip system message if I sent this rename (self-CC copy arriving back)
            bool iSentThis = !string.IsNullOrEmpty(accountEmail) &&
                renamedBy?.Equals(accountEmail, StringComparison.OrdinalIgnoreCase) == true;

            if (!iSentThis)
            {
                var renamedByMember = renamedBy != null ? await db.GroupMembers.FindAsync(groupId, renamedBy) : null;
                var renamedByContact = renamedBy != null ? await db.Contacts.FindAsync(accountId, renamedBy) : null;
                var actorName = renamedByMember?.DisplayName ?? renamedByContact?.DisplayName ?? renamedBy ?? "Someone";

                db.Messages.Add(new ChatMessage
                {
                    MessageId = pm.Headers.MessageId ?? Guid.NewGuid().ToString(),
                    ChatId = chat.ChatId,
                    Sender = renamedBy ?? pm.Sender,
                    Content = $"{actorName} renamed the group to \"{newName}\"",
                    Timestamp = pm.Headers.Timestamp,
                    DisplayTimestamp = pm.Headers.Timestamp,
                    ReceivedAt = DateTimeOffset.UtcNow,
                    IsSystem = true,
                    Status = MessageStatus.Sent
                });
            }

            updatedChats.Add(chat.ChatId);
            _fileLogger.Write("INFO", "HandleGroupRenameAsync",
                $"[{logAcct}] Group {groupId} renamed \"{oldName}\" → \"{newName}\" by {renamedBy} (iSentThis={iSentThis})");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "HandleGroupRenameAsync", $"[{logAcct}] Failed to handle group-rename: {ex.Message}");
        }
    }

    private async Task HandleChatDeleteAsync(
        ChatDbContext db,
        ParsedMessage pm,
        string accountId,
        string? accountEmail,
        HashSet<string> updatedChats,
        HashSet<string> accountChatIdSet,
        List<string> accountChatIds)
    {
        var logAcct = accountEmail ?? accountId[..Math.Min(8, accountId.Length)];
        try
        {
            var payload = System.Text.Json.JsonDocument.Parse(pm.Content);
            var root = payload.RootElement;
            var chatId = root.GetProperty("chat_id").GetString() ?? pm.Headers.GroupId;
            var deletedBy = root.TryGetProperty("deleted_by", out var dEl) ? dEl.GetString() : pm.Sender;

            // chat_id in the payload is the sender's local ChatId — useless on the receiver's
            // device. Find the 1:1 chat by the sender's email instead.
            var senderEmail = deletedBy ?? pm.Sender;
            var chat = await db.Chats.FirstOrDefaultAsync(c =>
                c.AccountId == accountId &&
                c.Type == ChatType.OneToOne &&
                c.ContactEmail == senderEmail &&
                !c.Deleted);
            if (chat == null) return;

            chat.Deleted = true;
            chat.LastActivityAt = pm.Headers.Timestamp;

            db.Messages.Add(new ChatMessage
            {
                MessageId = pm.Headers.MessageId ?? Guid.NewGuid().ToString(),
                ChatId = chat.ChatId,
                Sender = senderEmail,
                Content = "Chat deleted by the other side",
                Timestamp = pm.Headers.Timestamp,
                DisplayTimestamp = pm.Headers.Timestamp,
                ReceivedAt = DateTimeOffset.UtcNow,
                IsSystem = true,
                Status = MessageStatus.Sent
            });

            updatedChats.Add(chat.ChatId);
            _fileLogger.Write("INFO", "HandleChatDeleteAsync", $"[{logAcct}] Chat {chatId} deleted by {deletedBy}");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "HandleChatDeleteAsync", $"[{logAcct}] Failed to handle chat-delete: {ex.Message}");
        }
    }
}
