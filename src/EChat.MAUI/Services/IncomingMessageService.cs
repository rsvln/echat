using EChat.Core.Data;
using EChat.Core.Models;
using static EChat.Core.Models.MessageStatus;
using EChat.Core.Protocol;
using EChat.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Chat = EChat.Core.Models.Chat;
using ChatMessage = EChat.Core.Models.ChatMessage;
using Contact = EChat.Core.Models.Contact;
using ChatType = EChat.Core.Models.ChatType;

namespace EChat.Maui.Services;

public class IncomingMessageService
{
    private readonly ILogger<IncomingMessageService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChatEventService _chatEvents;

    public IncomingMessageService(
        ILogger<IncomingMessageService> logger,
        IServiceScopeFactory scopeFactory,
        ChatEventService chatEvents)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _chatEvents = chatEvents;
    }

    public async Task SaveAsync(string accountId, List<ParsedMessage> parsed)
    {

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var updatedChats = new HashSet<string>();
        var batchContacts = new Dictionary<string, Contact>(StringComparer.OrdinalIgnoreCase);
        var batchGroupChats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var batchSenderChatIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Load this account's chat IDs once — deduplication must be per-account
        var accountChatIds = await db.Chats
            .Where(c => c.AccountId == accountId || c.AccountId == null)
            .Select(c => c.ChatId)
            .ToListAsync();
        var accountChatIdSet = new HashSet<string>(accountChatIds, StringComparer.OrdinalIgnoreCase);

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

        // ── Handle regular new messages ───────────────────────────────────────
        foreach (var pm in parsed.Where(m =>
            m.Headers.EditOf == null &&
            m.Headers.DeleteOf == null &&
            (m.Headers.ReadOf == null || m.Headers.ReadOf.Count == 0)))
        {
            if (string.IsNullOrEmpty(pm.Headers.MessageId) || string.IsNullOrEmpty(pm.Sender))
            {
                continue;
            }

            // Dedup scoped to this account — same MessageId can be received by two accounts legitimately
            if (accountChatIdSet.Count > 0 &&
                await db.Messages.AnyAsync(m => m.MessageId == pm.Headers.MessageId && accountChatIds.Contains(m.ChatId)))
            {
                continue;
            }

            string chatId;

            if (!string.IsNullOrEmpty(pm.Headers.GroupId))
            {
                if (!batchGroupChats.TryGetValue(pm.Headers.GroupId, out chatId!))
                {
                    var groupChat = await db.Chats.FirstOrDefaultAsync(c => c.ChatId == pm.Headers.GroupId);
                    if (groupChat == null)
                    {
                        groupChat = new Chat
                        {
                            ChatId = pm.Headers.GroupId,
                            Type = ChatType.Group,
                            Name = pm.Headers.GroupName ?? "Group Chat",
                            AccountId = accountId,
                            CreatedAt = DateTimeOffset.UtcNow,
                            LastActivityAt = pm.Headers.Timestamp
                        };
                        db.Chats.Add(groupChat);
                        accountChatIdSet.Add(pm.Headers.GroupId);
                        accountChatIds.Add(pm.Headers.GroupId);
                    }
                    chatId = pm.Headers.GroupId;
                    batchGroupChats[pm.Headers.GroupId] = chatId;
                }
            }
            else
            {
                if (!batchSenderChatIds.TryGetValue(pm.Sender, out chatId!))
                {
                    var existingChatId = await db.Messages
                        .Where(m => m.Sender == pm.Sender && accountChatIds.Contains(m.ChatId))
                        .Select(m => m.ChatId)
                        .FirstOrDefaultAsync();

                    if (existingChatId != null)
                    {
                        chatId = existingChatId;
                    }
                    else
                    {
                        if (!batchContacts.TryGetValue(pm.Sender, out var contact))
                            contact = await db.Contacts.FindAsync(pm.Sender);

                        var chatName = contact?.DisplayName ?? pm.Sender.Split('@')[0];

                        var namedChat = await db.Chats.FirstOrDefaultAsync(c =>
                            c.Type == ChatType.OneToOne &&
                            (c.AccountId == accountId || c.AccountId == null) &&
                            c.Name == chatName);

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
                                    Email = pm.Sender,
                                    DisplayName = pm.Sender.Split('@')[0]
                                };
                                db.Contacts.Add(contact);
                                batchContacts[pm.Sender] = contact;
                            }

                            var newChat = new Chat
                            {
                                ChatId = Guid.NewGuid().ToString(),
                                Type = ChatType.OneToOne,
                                Name = contact.DisplayName ?? pm.Sender.Split('@')[0],
                                AccountId = accountId,
                                CreatedAt = DateTimeOffset.UtcNow,
                                LastActivityAt = pm.Headers.Timestamp
                            };
                            db.Chats.Add(newChat);
                            chatId = newChat.ChatId;
                            accountChatIdSet.Add(chatId);
                            accountChatIds.Add(chatId);
                        }
                    }
                    batchSenderChatIds[pm.Sender] = chatId;
                }
            }

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
                InReplyTo = pm.Headers.InReplyTo
            });

            var chat = await db.Chats.FindAsync(chatId);
            if (chat != null)
            {
                if (pm.Headers.Timestamp > (chat.LastActivityAt ?? DateTimeOffset.MinValue))
                    chat.LastActivityAt = pm.Headers.Timestamp;
                chat.UnreadCount++;
            }

            updatedChats.Add(chatId);
        } // end regular messages loop

        if (updatedChats.Count > 0)
        {
            try
            {
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save incoming messages");
                throw;
            }
            foreach (var chatId in updatedChats)
                _chatEvents.NotifyChatUpdated(chatId);
        }
    }
}
