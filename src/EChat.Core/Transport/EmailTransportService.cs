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
        _logger.LogInformation("Reconnecting transport for account {Email}", account.Email);

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
            _logger.LogWarning(ex, "Error during disconnect before reconnect");
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
        _logger.LogInformation("Transport reconnected for {Email}", account.Email);

        // Start sync loop based on SyncEngine strategy
        _idleCts = new CancellationTokenSource();
        _pollingCts = new CancellationTokenSource();
        var cts = _idleCts;
        _idleTask = StartSyncLoopAsync(account, cts.Token);
        _ = _idleTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogError(t.Exception?.GetBaseException(), "Sync loop stopped for {Email}", account.Email);
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
            _logger.LogWarning(ex, "Could not load sync timestamp; defaulting to 30 days ago");
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
            var ids = await db.Messages
                .Where(m => m.MessageId != null && m.ReceivedAt >= since)
                .Select(m => m.MessageId!)
                .ToListAsync();
            knownIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load known message IDs; eChat sync may reprocess messages");
            knownIds = new HashSet<string>();
        }

        // Sync eChat folder before starting IDLE/polling
        _syncEngine.RecordWakeup();
        await _imapService.SyncEchatFolderAsync(EchatFolder, knownIds, since.UtcDateTime, ct);

        // Save current time as the new high-water mark
        await SaveSyncTimestampAsync(settingKey);

        // Determine strategy
        var strategy = _syncEngine.GetCurrentStrategy(batteryLevel: 100, isMetered: false, isCellular: false);

        if (strategy.UseIdle)
        {
            _logger.LogInformation("Starting IMAP IDLE for {Email} (sync interval={Interval}min)",
                account.Email, strategy.PollingInterval.TotalMinutes);
            await _imapService.StartIdleAsync(InboxFolder, EchatFolder, ct, knownIds, strategy.PollingInterval);
        }
        else
        {
            _logger.LogInformation("Starting polling loop for {Email} (interval={Interval})",
                account.Email, strategy.PollingInterval);
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

                // Load known IDs only from last sync window (with 2-day overlap)
                var overlapSince = lastSync.AddDays(-2);
                var ids = await db.Messages
                    .Where(m => m.MessageId != null && m.ReceivedAt >= overlapSince)
                    .Select(m => m.MessageId!)
                    .ToListAsync();
                var knownIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);

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
                _logger.LogWarning(ex, "Polling error for {Email}", _accountConfig.Email);
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
            _logger.LogWarning(ex, "Error during disconnect");
        }

        IsConnected = false;
    }

    public async Task SendMessageAsync(OutgoingMessage message)
    {
        // Look up the appropriate public key for encryption
        if (message.RecipientPublicKey == null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

                if (message.GroupId != null)
                {
                    // Group message — encrypt with the shared group public key
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
                _logger.LogWarning(ex, "Failed to look up public key for message");
            }
        }

        await _batchQueue.Enqueue(message);
    }

    private async Task SendSingleAsync(OutgoingMessage message)
    {
        var email = await _builder.BuildSingleAsync(message);

        // Add self as a recipient so the message lands in our own IMAP inbox.
        // Other devices (e.g. desktop) will pick it up and show it as a sent message.
        // The sending device deduplicates it via the DB MessageId check in IncomingMessageService.
        var selfEmail = _accountConfig.Email;
        if (!string.IsNullOrEmpty(selfEmail) &&
            !email.To.Mailboxes.Any(m => m.Address.Equals(selfEmail, StringComparison.OrdinalIgnoreCase)))
        {
            email.To.Add(new MailboxAddress("", selfEmail));
        }

        await _smtpService.SendAsync(email);
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

        var success = await _smtpService.SendAsync(email);
        if (!success)
        {
            _logger.LogWarning("Batch send failed, retrying individually");
            foreach (var msg in messages)
                await SendSingleAsync(msg);
        }
    }

    private async Task OnMessageReceivedAsync(MimeKit.MimeMessage email, long imapUid, string imapFolder)
    {
        try
        {
            _fileLogger.Write("INFO", "OnMessageReceived", $"Email received: uid={imapUid}, folder={imapFolder}, from={email.From.Mailboxes.FirstOrDefault()?.Address}, to={string.Join(",", email.To.Mailboxes.Select(m => m.Address))}, subject={email.Subject}");

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
            foreach (var msg in messages)
            {
                if (msg.Headers.Encryption != "pgp-inline" || string.IsNullOrEmpty(msg.Content))
                    continue;

                _fileLogger.Write("INFO", "OnMessageReceived", $"Decrypting message: {msg.Headers.MessageId}, encryption={msg.Headers.Encryption}");

                string? privateKey = null;
                string password = string.Empty;

                // For group messages, use the group's shared private key
                if (!string.IsNullOrEmpty(msg.Headers.GroupId))
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                        var groupKey = await db.GroupKeyPairs.FindAsync(msg.Headers.GroupId);
                        if (!string.IsNullOrEmpty(groupKey?.PrivateKey))
                        {
                            privateKey = groupKey.PrivateKey;
                            password = string.Empty; // group keys always use empty password
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to look up group key for {GroupId}", msg.Headers.GroupId);
                    }
                }

                // Fall back to the account's personal private key
                if (privateKey == null && _accountConfig.PrivateKey != null)
                {
                    privateKey = _accountConfig.PrivateKey;
                    password = _accountConfig.KeyPassword ?? string.Empty;
                }

                if (privateKey == null)
                {
                    _fileLogger.Write("WARN", "OnMessageReceived", $"No private key available for message {msg.Headers.MessageId}");
                    continue;
                }

                try
                {
                    var decrypted = await _pgpService.DecryptAsync(msg.Content, privateKey, password);
                    // Parse metadata that was embedded inside the encrypted body
                    _parser.ApplyDecryptedContent(msg, decrypted);
                    msg.IsEncrypted = false;
                    _fileLogger.Write("INFO", "OnMessageReceived", $"Decrypted successfully. Content preview: {msg.Content.Substring(0, Math.Min(50, msg.Content.Length))}");
                }
                catch (Exception ex)
                {
                    _fileLogger.Write("ERROR", "OnMessageReceived", $"Decrypt failed for {msg.Headers.MessageId}: {ex.Message}");
                    _logger.LogWarning(ex, "Failed to decrypt message {Id}", msg.Headers.MessageId);
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
            _logger.LogError(ex, "Error processing received message");
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
            _logger.LogWarning(ex, "Failed to delete IMAP messages for chat {ChatId}", chatId);
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
            _logger.LogInformation("Stored public key for {Email}", senderEmail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store Autocrypt key for {Email}", senderEmail);
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
            _logger.LogWarning(ex, "Failed to save sync timestamp");
        }
    }
}
