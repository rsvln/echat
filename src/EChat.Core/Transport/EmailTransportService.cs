using System.IO;
using EChat.Core.Crypto;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Protocol;
using EChat.Core.Services;
using EChat.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EChat.Core.Transport;

public class EmailTransportService
{
    private readonly ILogger<EmailTransportService> _logger;
    private readonly ImapService _imapService;
    private readonly SmtpService _smtpService;
    private readonly ChatMessageParser _parser;
    private readonly ChatMessageBuilder _builder;
    private readonly BatchQueue _batchQueue;
    private readonly MessageDeduplicator _deduplicator;
    private readonly AccountConfig _accountConfig;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PgpService _pgpService;
    private readonly SyncEngine _syncEngine;
    private readonly FileLogger _fileLogger;

    public bool IsConnected { get; private set; }

    private CancellationTokenSource? _idleCts;
    private CancellationTokenSource? _pollingCts;
    private Task? _idleTask;
    private Task? _pollingTask;
    private const string InboxFolder = "INBOX";
    private const string EchatFolder = "eChat";

    public event Func<List<ParsedMessage>, Task>? MessagesReceived;

    public EmailTransportService(
        ILogger<EmailTransportService> logger,
        ImapService imapService,
        SmtpService smtpService,
        ChatMessageParser parser,
        ChatMessageBuilder builder,
        MessageDeduplicator deduplicator,
        AccountConfig accountConfig,
        IServiceScopeFactory scopeFactory,
        PgpService pgpService,
        SyncEngine syncEngine,
        FileLogger fileLogger)
    {
        _logger = logger;
        _imapService = imapService;
        _smtpService = smtpService;
        _parser = parser;
        _builder = builder;
        _deduplicator = deduplicator;
        _accountConfig = accountConfig;
        _scopeFactory = scopeFactory;
        _pgpService = pgpService;
        _syncEngine = syncEngine;
        _fileLogger = fileLogger;

        _batchQueue = new BatchQueue(
            SendBatchedAsync,
            SendSingleAsync,
            TimeSpan.FromSeconds(10),
            syncEngine.GetAdaptiveBatchWindow);

        _imapService.MessageReceived += OnMessageReceivedAsync;
    }

    public async Task ConnectAsync(EmailAccountConfig config, string password)
    {
        await _imapService.ConnectAsync(config.ImapServer, config.ImapPort, config.Email, password, config.UseSsl);
        await _smtpService.ConnectAsync(config.SmtpServer, config.SmtpPort, config.Email, password, config.UseSsl);
        IsConnected = true;
    }

    public async Task ReconnectAsync(Account account, string deviceId)
    {
        _fileLogger.Write("INFO", "EmailTransportService", $"Reconnecting transport for account {account.Email}");

        // Stop old IDLE first — before disconnecting, so the task sees cancellation
        // instead of a yanked-out connection (which causes InvalidOperationException)
        _idleCts?.Cancel();
        if (_idleTask != null)
        {
            try { await _idleTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* timeout or cancelled — that's fine */ }
            _idleTask = null;
        }

        try
        {
            await _imapService.DisconnectAsync();
            await _smtpService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "EmailTransportService", $"Error during disconnect before reconnect: {ex.Message}");
        }

        IsConnected = false;

        _accountConfig.AccountId = account.AccountId;
        _accountConfig.Email = account.Email;
        _accountConfig.DeviceId = deviceId;
        _accountConfig.PublicKey = account.PublicKey;
        _accountConfig.PrivateKey = account.PrivateKey;
        _accountConfig.KeyPassword = account.Password;

        // Update parser with current account email for batch filtering
        try { _parser.SetMyEmail(account.Email); } catch { }

        var emailConfig = new EmailAccountConfig
        {
            Email = account.Email,
            ImapServer = account.ImapServer,
            ImapPort = account.ImapPort,
            SmtpServer = account.SmtpServer,
            SmtpPort = account.SmtpPort,
            UseSsl = account.ImapUseSsl,
            DisplayName = account.DisplayName
        };

        await ConnectAsync(emailConfig, account.Password);
        _fileLogger.Write("INFO", "EmailTransportService", $"Transport reconnected for {account.Email}");

        // Start sync loop based on SyncEngine strategy
        _idleCts = new CancellationTokenSource();
        _pollingCts = new CancellationTokenSource();
        var cts = _idleCts;
        _idleTask = StartSyncLoopAsync(account, cts.Token);
        _ = _idleTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _fileLogger.Write("ERROR", "EmailTransportService", $"Sync loop stopped for {account.Email}: {t.Exception?.GetBaseException()?.Message}");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private const string SyncTimestampKeyPrefix = "imap_sync_last_at_";

    private async Task StartSyncLoopAsync(Account account, CancellationToken ct)
    {
        var settingKey = SyncTimestampKeyPrefix + account.AccountId;

        // Load last-sync timestamp from DB; default to 30 days ago on first run.
        DateTimeOffset lastSync;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var setting = await db.Settings.FindAsync(settingKey);
            lastSync = setting != null && DateTimeOffset.TryParse(setting.Value, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow.AddDays(-30);
        }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "EmailTransportService", $"Could not load sync timestamp; defaulting to 30 days ago: {ex.Message}");
            lastSync = DateTimeOffset.UtcNow.AddDays(-30);
        }

        // 2-day overlap guards against clock skew and partial syncs
        var since = lastSync.AddDays(-2);
        if ((DateTimeOffset.UtcNow - since).TotalDays > 30)
            since = DateTimeOffset.UtcNow.AddDays(-30);

        // Load only recent known IDs — bounded by the sync window, not the whole DB
        HashSet<string> knownIds;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            // DateTimeOffset comparison doesn't translate to SQLite — filter date client-side
            var rows = await db.Messages
                .Where(m => m.MessageId != null)
                .Select(m => new { m.MessageId, m.ReceivedAt })
                .ToListAsync();
            var ids = rows
                .Where(m => m.ReceivedAt >= since)
                .Select(m => m.MessageId!)
                .ToList();
            knownIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "EmailTransportService", $"Could not load known message IDs; eChat sync may reprocess messages: {ex.Message}");
            knownIds = new HashSet<string>();
        }

        // Retry messages that were stuck in Sending status (app was killed mid-send)
        await RetryStuckSendingAsync(account, ct);

        // Sync eChat folder before starting IDLE/polling
        _syncEngine.RecordWakeup();
        await _imapService.SyncEchatFolderAsync(EchatFolder, knownIds, since.UtcDateTime, ct);

        // Save current time as the new high-water mark
        await SaveSyncTimestampAsync(settingKey);

        // Determine strategy
        var strategy = _syncEngine.GetCurrentStrategy(batteryLevel: 100, isMetered: false, isCellular: false);

        if (strategy.UseIdle)
        {
            _fileLogger.Write("INFO", "EmailTransportService", $"Starting IMAP IDLE for {account.Email} (sync interval={strategy.PollingInterval.TotalMinutes}min)");
            await _imapService.StartIdleAsync(InboxFolder, EchatFolder, ct, knownIds, strategy.PollingInterval);
        }
        else
        {
            _fileLogger.Write("INFO", "EmailTransportService", $"Starting polling loop for {account.Email} (interval={strategy.PollingInterval})");
            await StartPollingLoopAsync(strategy.PollingInterval, since, ct);
        }
    }

    private async Task StartPollingLoopAsync(TimeSpan interval, DateTimeOffset since, CancellationToken ct)
    {
        var lastSync = since;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

                // Load known IDs only from last sync window (with 2-day overlap).
                // DateTimeOffset comparison can't be translated by EF Core SQLite — filter in C#.
                var overlapSince = lastSync.AddDays(-2);
                var allRows = await db.Messages
                    .Where(m => m.MessageId != null)
                    .Select(m => new { m.MessageId, m.ReceivedAt })
                    .ToListAsync();
                var knownIds = new HashSet<string>(
                    allRows.Where(m => m.ReceivedAt >= overlapSince).Select(m => m.MessageId!),
                    StringComparer.OrdinalIgnoreCase);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(interval);

                _syncEngine.RecordWakeup();
                await _imapService.SyncEchatFolderAsync(EchatFolder, knownIds, overlapSince.UtcDateTime, timeoutCts.Token);
                await _imapService.SyncInboxAsync(InboxFolder, knownIds, timeoutCts.Token);

                lastSync = DateTimeOffset.UtcNow;

                // Save sync timestamp
                var settingKey = SyncTimestampKeyPrefix + _accountConfig.AccountId;
                var setting = await db.Settings.FindAsync(settingKey);
                var now = DateTimeOffset.UtcNow.ToString("O");
                if (setting == null)
                    db.Settings.Add(new Setting { Key = settingKey, Value = now, UpdatedAt = DateTimeOffset.UtcNow });
                else
                {
                    setting.Value = now;
                    setting.UpdatedAt = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _fileLogger.Write("WARN", "EmailTransportService", $"Polling error for {_accountConfig.Email}: {ex.Message}");
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(interval, ct); } catch { break; }
            }
        }
    }

    public async Task DisconnectAsync()
    {
        _idleCts?.Cancel();
        _pollingCts?.Cancel();
        if (_idleTask != null)
        {
            try { await _idleTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
            _idleTask = null;
        }
        if (_pollingTask != null)
        {
            try { await _pollingTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
            _pollingTask = null;
        }

        try
        {
            await _imapService.DisconnectAsync();
            await _smtpService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "EmailTransportService", $"Error during disconnect: {ex.Message}");
        }

        IsConnected = false;
    }

    public async Task SendMessageAsync(OutgoingMessage message)
    {
        // Look up the appropriate public key for encryption
        // IMPORTANT: If RecipientPublicKey is already set (e.g., from ChatList for group-create
        // messages), preserve it — group-create must be encrypted per-recipient with their
        // personal key, not the shared group key.
        if (message.RecipientPublicKey == null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

                if (message.GroupId != null)
                {
                    // Regular group message (not group-create) — encrypt with the shared group public key
                    // Group-create messages have RecipientPublicKey already set from the UI
                    // and should NOT use the group key (they need per-recipient encryption)
                    var groupKey = await db.GroupKeyPairs.FindAsync(message.GroupId);
                    if (!string.IsNullOrEmpty(groupKey?.PublicKey))
                        message.RecipientPublicKey = groupKey.PublicKey;
                }
                else if (message.Recipients.Count == 1)
                {
                    // 1:1 message — encrypt with the contact's personal public key
                    var contact = await db.Contacts.FindAsync(message.Recipients[0]);
                    if (contact?.PublicKey != null)
                        message.RecipientPublicKey = contact.PublicKey;
                }
            }
            catch (Exception ex)
            {
                _fileLogger.Write("WARN", "EmailTransportService", $"Failed to look up public key for message: {ex.Message}");
            }
        }

        await _batchQueue.Enqueue(message);
    }

    private async Task RetryStuckSendingAsync(Account account, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

            // Find messages saved locally but never confirmed sent (app killed mid-send)
            var stuck = await db.Messages
                .Where(m => m.Status == MessageStatus.Sending && m.Sender == account.Email)
                .OrderBy(m => m.Timestamp)
                .ToListAsync(ct);

            if (stuck.Count == 0) return;

            _fileLogger.Write("INFO", "EmailTransportService", $"Retrying {stuck.Count} stuck Sending message(s) for {account.Email}");

            foreach (var msg in stuck)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var chat = await db.Chats.FindAsync(new object[] { msg.ChatId }, ct);
                    if (chat == null || chat.Deleted) continue;

                    // Build recipients list
                    List<string> recipients;
                    string? groupId = null;
                    if (chat.Type == ChatType.Group)
                    {
                        recipients = await db.GroupMembers
                            .Where(m => m.GroupId == chat.ChatId)
                            .Select(m => m.MemberEmail)
                            .ToListAsync(ct);
                        groupId = chat.ChatId;
                    }
                    else
                    {
                        recipients = string.IsNullOrEmpty(chat.PartnerEmail)
                            ? new List<string>()
                            : new List<string> { chat.PartnerEmail };
                    }

                    if (recipients.Count == 0) continue;

                    // Load attachments from disk
                    var dbAtts = await db.Attachments
                        .Where(a => a.MessageId == msg.MessageId)
                        .ToListAsync(ct);

                    List<AttachmentInfo>? attachments = null;
                    if (dbAtts.Count > 0)
                    {
                        attachments = new List<AttachmentInfo>();
                        foreach (var att in dbAtts)
                        {
                            try
                            {
                                var data = att.FilePath != null && File.Exists(att.FilePath)
                                    ? await File.ReadAllBytesAsync(att.FilePath, ct)
                                    : Array.Empty<byte>();
                                attachments.Add(new AttachmentInfo
                                {
                                    FileName = att.FileName ?? "file",
                                    ContentType = att.ContentType ?? "application/octet-stream",
                                    Size = data.Length,
                                    Data = data
                                });
                            }
                            catch (Exception ex)
                            {
                                _fileLogger.Write("WARN", "EmailTransportService", $"Could not load attachment {att.FileName} for retry: {ex.Message}");
                            }
                        }
                    }

                    await SendMessageAsync(new OutgoingMessage
                    {
                        MessageId = msg.MessageId,
                        Content = msg.Content,
                        Recipients = recipients,
                        GroupId = groupId,
                        Timestamp = msg.Timestamp,
                        InReplyTo = msg.InReplyTo,
                        Tier = BatchTier.Immediate,
                        Attachments = attachments?.Count > 0 ? attachments : null
                    });

                    _fileLogger.Write("INFO", "EmailTransportService", $"Retried stuck message {msg.MessageId}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _fileLogger.Write("WARN", "EmailTransportService", $"Failed to retry stuck message {msg.MessageId}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "EmailTransportService", $"RetryStuckSendingAsync failed: {ex.Message}");
        }
    }

    private async Task SendSingleAsync(OutgoingMessage message)
    {
        var email = await _builder.BuildSingleAsync(message);

        _fileLogger.Write("INFO", "SendSingle", $"SENDING email: msgId={message.MessageId}, systemType={message.SystemType}, " +
            $"groupId={message.GroupId}, type={message.Type}, encrypt={message.Encrypt}, " +
            $"recipientKey={message.RecipientPublicKey != null}, " +
            $"recipients={string.Join(",", message.Recipients)}, " +
            $"from={_accountConfig.Email}, subject={email.Subject}");

        // Add self as a recipient so the message lands in our own IMAP inbox.
        // Other devices (e.g. desktop) will pick it up and show it as a sent message.
        // The sending device deduplicates it via the DB MessageId check in IncomingMessageService.
        var selfEmail = _accountConfig.Email;
        if (!string.IsNullOrEmpty(selfEmail) &&
            !email.To.Mailboxes.Any(m => m.Address.Equals(selfEmail, StringComparison.OrdinalIgnoreCase)))
        {
            email.To.Add(new MailboxAddress("", selfEmail));
            _fileLogger.Write("DEBUG", "SendSingle", $"Added self-CC for {selfEmail}");
        }

        var result = await _smtpService.SendAsync(email);
        _fileLogger.Write("INFO", "SendSingle", $"SMTP send result for {message.MessageId}: {result}");

        await UpdateMessageStatusAsync(message.MessageId, result);
    }

    private async Task UpdateMessageStatusAsync(string messageId, SmtpSendResult result)
    {
        var newStatus = result switch
        {
            SmtpSendResult.Sent          => MessageStatus.Sent,
            SmtpSendResult.Permanent     => MessageStatus.Failed,
            // RateLimited / TransientError: leave as Sending — RetryStuckSendingAsync will retry
            _                            => (MessageStatus?)null
        };

        if (newStatus == null) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            await db.Messages
                .Where(m => m.MessageId == messageId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, newStatus.Value));
        }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "SendSingle", $"Failed to update message status for {messageId}: {ex.Message}");
        }
    }

    private async Task SendBatchedAsync(List<OutgoingMessage> messages)
    {
        var tier = messages.First().Tier;
        var email = _builder.BuildBatch(messages, tier);

        // Same self-copy logic as SendSingleAsync: include own email so other devices sync it.
        var selfEmail = _accountConfig.Email;
        if (!string.IsNullOrEmpty(selfEmail) &&
            !email.To.Mailboxes.Any(m => m.Address.Equals(selfEmail, StringComparison.OrdinalIgnoreCase)))
        {
            email.To.Add(new MailboxAddress("", selfEmail));
        }

        var result = await _smtpService.SendAsync(email);
        if (result == SmtpSendResult.Sent)
        {
            foreach (var msg in messages)
                await UpdateMessageStatusAsync(msg.MessageId, SmtpSendResult.Sent);
        }
        else if (result == SmtpSendResult.Permanent)
        {
            foreach (var msg in messages)
                await UpdateMessageStatusAsync(msg.MessageId, SmtpSendResult.Permanent);
        }
        else
        {
            // Transient / rate-limited — try individually, each will update its own status
            _fileLogger.Write("WARN", "EmailTransportService", $"Batch send result={result}, retrying individually");
            foreach (var msg in messages)
                await SendSingleAsync(msg);
        }
    }

    private async Task OnMessageReceivedAsync(MimeKit.MimeMessage email, long imapUid, string imapFolder)
    {
        try
        {
            _fileLogger.Write("INFO", "OnMessageReceived", $"Email received: uid={imapUid}, folder={imapFolder}, from={email.From.Mailboxes.FirstOrDefault()?.Address}, to={string.Join(",", email.To.Mailboxes.Select(m => m.Address))}, subject={email.Subject}");

            // Log group-related headers for debugging
            var chatGroupId = email.Headers["Chat-Group-ID"];
            var chatSystemType = email.Headers["Chat-System-Type"];
            var chatEncryption = email.Headers["Chat-Encryption"];
            if (chatGroupId != null || chatSystemType != null)
            {
                _fileLogger.Write("INFO", "OnMessageReceived", $"GROUP-RELATED headers: Chat-Group-ID={chatGroupId}, Chat-System-Type={chatSystemType}, Chat-Encryption={chatEncryption}");
            }

            var autocrypt = email.Headers["Autocrypt"];
            if (!string.IsNullOrEmpty(autocrypt))
                await StoreAutocryptKeyAsync(email.From.Mailboxes.FirstOrDefault()?.Address, autocrypt);

            var messages = _parser.Parse(email);
            _fileLogger.Write("INFO", "OnMessageReceived", $"Parsed {messages.Count} message(s). Sender={messages.FirstOrDefault()?.Sender}, messageId={messages.FirstOrDefault()?.Headers?.MessageId}");

            // Attach IMAP location so IncomingMessageService can store it for later deletion
            foreach (var m in messages)
            {
                m.ImapUid = imapUid > 0 ? imapUid : null;
                m.ImapFolder = string.IsNullOrEmpty(imapFolder) ? null : imapFolder;
            }

            // Decrypt PGP-inline encrypted messages
            // Try group key first (if applicable), then fall back to personal key.
            // group-create messages are encrypted with the recipient's personal key,
            // so if group key is found but decryption fails, we retry with the personal key.
            foreach (var msg in messages)
            {
                if (msg.Headers.Encryption != "pgp-inline" || string.IsNullOrEmpty(msg.Content))
                    continue;

                _fileLogger.Write("INFO", "OnMessageReceived", $"Decrypting message: {msg.Headers.MessageId}, encryption={msg.Headers.Encryption}, groupId={msg.Headers.GroupId}");

                bool decrypted = false;

                // Step 1: For group messages, try the group's shared private key first
                if (!string.IsNullOrEmpty(msg.Headers.GroupId))
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                        var groupKey = await db.GroupKeyPairs.FindAsync(msg.Headers.GroupId);
                        if (!string.IsNullOrEmpty(groupKey?.PrivateKey))
                        {
                            try
                            {
                                var decryptedContent = await _pgpService.DecryptAsync(msg.Content, groupKey.PrivateKey, string.Empty);
                                _parser.ApplyDecryptedContent(msg, decryptedContent);
                                msg.IsEncrypted = false;
                                decrypted = true;
                                _fileLogger.Write("INFO", "OnMessageReceived", $"Decrypted with group key: {msg.Headers.MessageId}");
                            }
                            catch (Exception ex)
                            {
                                _fileLogger.Write("WARN", "OnMessageReceived", $"Group key found but decryption failed for {msg.Headers.MessageId}, trying personal key: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _fileLogger.Write("WARN", "OnMessageReceived", $"Failed to look up group key for {msg.Headers.GroupId}: {ex.Message}");
                    }
                }

                // Step 2: Fall back to the account's personal private key
                // (used for group-create messages encrypted per-recipient, regular 1:1 messages,
                //  or when group key decryption failed)
                if (!decrypted && _accountConfig.PrivateKey != null)
                {
                    try
                    {
                        var decryptedContent = await _pgpService.DecryptAsync(msg.Content, _accountConfig.PrivateKey, _accountConfig.KeyPassword ?? string.Empty);
                        _parser.ApplyDecryptedContent(msg, decryptedContent);
                        msg.IsEncrypted = false;
                        decrypted = true;
                        _fileLogger.Write("INFO", "OnMessageReceived", $"Decrypted with personal key: {msg.Headers.MessageId}");
                    }
                    catch (Exception ex)
                    {
                        _fileLogger.Write("WARN", "OnMessageReceived", $"Personal key decryption also failed for {msg.Headers.MessageId}: {ex.Message}");
                    }
                }

                if (!decrypted)
                {
                    _fileLogger.Write("WARN", "OnMessageReceived", $"Could not decrypt message {msg.Headers.MessageId}");
                    _fileLogger.Write("WARN", "OnMessageReceived", $"Could not decrypt message {msg.Headers.MessageId}");
                }
            }

            var newMessages = messages.Where(m => !_deduplicator.IsDuplicate(_accountConfig.AccountId, m)).ToList();
            _fileLogger.Write("INFO", "OnMessageReceived", $"Dedup: {messages.Count} total, {newMessages.Count} new, {messages.Count - newMessages.Count} duplicates");
            foreach (var m in newMessages)
            {
                _fileLogger.Write("DEBUG", "OnMessageReceived", $"NEW msg: sender={m.Sender}, msgId={m.Headers.MessageId}, recipients={string.Join(",", m.Recipients)}");
            }

            if (newMessages.Any() && MessagesReceived != null)
            {
                _fileLogger.Write("INFO", "OnMessageReceived", $"Forwarding {newMessages.Count} message(s) to MessagesReceived handler");
                await MessagesReceived(newMessages);
            }
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "OnMessageReceived", $"Exception: {ex.Message}\n{ex.StackTrace}");
            _fileLogger.Write("ERROR", "EmailTransportService", $"Error processing received message: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes all IMAP emails associated with the given chat from the server.
    /// Groups deletions by folder for efficiency.
    /// </summary>
    public async Task DeleteChatImapMessagesAsync(string chatId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

            var imapRecords = await db.Messages
                .Where(m => m.ChatId == chatId && m.ImapUid != null && m.ImapFolder != null)
                .Select(m => new { m.ImapUid, m.ImapFolder })
                .ToListAsync();

            if (imapRecords.Count == 0) return;

            var byFolder = imapRecords
                .GroupBy(r => r.ImapFolder!)
                .ToDictionary(g => g.Key, g => g.Select(r => r.ImapUid!.Value).ToList());

            foreach (var (folder, uids) in byFolder)
                await _imapService.DeleteMessagesAsync(folder, uids);
        }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "EmailTransportService", $"Failed to delete IMAP messages for chat {chatId}: {ex.Message}");
        }
    }

    private async Task StoreAutocryptKeyAsync(string? senderEmail, string autocryptHeader)
    {
        if (string.IsNullOrEmpty(senderEmail)) return;

        // Parse: addr=...; keydata=...
        string? keydata = null;
        foreach (var part in autocryptHeader.Split(';'))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("keydata", StringComparison.OrdinalIgnoreCase))
            {
                keydata = kv[1].Trim();
                break;
            }
        }

        if (string.IsNullOrEmpty(keydata)) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

            var contact = await db.Contacts.FindAsync(senderEmail);
            if (contact == null)
            {
                contact = new Contact
                {
                    Email = senderEmail,
                    DisplayName = senderEmail.Split('@')[0],
                    PublicKey = keydata
                };
                db.Contacts.Add(contact);
            }
            else if (contact.PublicKey != keydata)
            {
                contact.PublicKey = keydata;
            }

            // Compute fingerprint if missing
            if (string.IsNullOrEmpty(contact.KeyFingerprint) && !string.IsNullOrEmpty(contact.PublicKey))
            {
                try
                {
                    contact.KeyFingerprint = _pgpService.GetFingerprint(contact.PublicKey);
                }
                catch { }
            }

            await db.SaveChangesAsync();
            _fileLogger.Write("INFO", "EmailTransportService", $"Stored public key for {senderEmail}");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "EmailTransportService", $"Failed to store Autocrypt key for {senderEmail}: {ex.Message}");
        }
    }

    private async Task SaveSyncTimestampAsync(string settingKey)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var setting = await db.Settings.FindAsync(settingKey);
            var now = DateTimeOffset.UtcNow.ToString("O");
            if (setting == null)
                db.Settings.Add(new Setting { Key = settingKey, Value = now, UpdatedAt = DateTimeOffset.UtcNow });
            else
            {
                setting.Value = now;
                setting.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "EmailTransportService", $"Failed to save sync timestamp: {ex.Message}");
        }
    }
}
