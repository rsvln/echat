using System.IO;
using EChat.Core.Crypto;
using EChat.Core.Data;
using EChat.Core.Models;
using static EChat.Core.ServiceCollectionExtensions;
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
    private readonly DatabasePathInfo _dbPathInfo;
    private readonly NtpTimeService _ntpTimeService;

    public bool IsConnected { get; private set; }

    /// <summary>Set when SMTP is currently rate-limited; cleared automatically when the window expires.</summary>
    public bool IsRateLimited { get; private set; }
    public DateTimeOffset? RateLimitedUntil { get; private set; }

    /// <summary>Fired when SMTP rate-limit starts. Argument = earliest retry time.</summary>
    public event Action<DateTimeOffset>? RateLimitStarted;
    /// <summary>Fired when the rate-limit window expires and sending may resume.</summary>
    public event Action? RateLimitCleared;

    private CancellationTokenSource? _rateLimitCts;
    private const int RateLimitCooldownMinutes = 5;

    private void OnSmtpRateLimited()
    {
        // Capture the sender email NOW — _accountConfig.Email may change if the user
        // switches accounts before the 5-minute cooldown expires, which would cause
        // RetryStuckMessagesAsync to query the wrong account and find nothing.
        var senderEmailSnapshot = _accountConfig.Email;

        // Cancel any previous cooldown timer so multiple rapid 451s don't stack
        _rateLimitCts?.Cancel();
        _rateLimitCts = new CancellationTokenSource();

        var retryAfter = DateTimeOffset.UtcNow.AddMinutes(RateLimitCooldownMinutes);
        IsRateLimited = true;
        RateLimitedUntil = retryAfter;
        RateLimitStarted?.Invoke(retryAfter);
        _fileLogger.Write("WARN", "EmailTransportService",
            $"SMTP rate-limited ({senderEmailSnapshot}) — will auto-retry at {retryAfter.ToLocalTime():HH:mm:ss}");

        var cts = _rateLimitCts;
        _ = Task.Delay(TimeSpan.FromMinutes(RateLimitCooldownMinutes), cts.Token)
            .ContinueWith(async t =>
            {
                if (t.IsCanceled) return;
                IsRateLimited = false;
                RateLimitedUntil = null;
                RateLimitCleared?.Invoke();
                _fileLogger.Write("INFO", "EmailTransportService",
                    $"SMTP rate-limit window expired — retrying stuck messages for {senderEmailSnapshot}");
                // Pass the captured email so the retry targets the right account
                // even if the user has switched accounts in the meantime.
                await RetryStuckMessagesAsync(senderEmailSnapshot);
            }, TaskScheduler.Default);
    }

    /// <summary>Retries messages stuck in Sending state after a rate-limit window expires.</summary>
    /// <param name="senderEmail">
    /// The account email to retry for. Pass the value captured at rate-limit time so the
    /// retry targets the correct account even if the user has switched accounts since then.
    /// Falls back to <see cref="AccountConfig.Email"/> if null.
    /// </param>
    private async Task RetryStuckMessagesAsync(string? senderEmail = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            senderEmail ??= _accountConfig.Email;
            if (string.IsNullOrEmpty(senderEmail)) return;

            // Messages stuck in Sending for more than 24 hours will never succeed
            // (daily sending limit, deleted account, etc.) — mark them Failed so the
            // user can see what happened and optionally retry manually.
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var abandoned = await db.Messages
                .Where(m => m.Status == MessageStatus.Sending &&
                            m.Sender == senderEmail)
                .ToListAsync();
            abandoned = abandoned.Where(m => m.Timestamp < cutoff).ToList();
            if (abandoned.Count > 0)
            {
                foreach (var msg in abandoned)
                    msg.Status = MessageStatus.Failed;
                await db.SaveChangesAsync();
                _fileLogger.Write("WARN", "EmailTransportService",
                    $"Abandoned {abandoned.Count} message(s) stuck in Sending >24h for {senderEmail}");
            }

            // Only retry messages within the 24h window (older ones were just abandoned above)
            var stuck = await db.Messages
                .Where(m => m.Status == MessageStatus.Sending &&
                            m.Sender == senderEmail)
                .ToListAsync();
            stuck = stuck.Where(m => m.Timestamp >= cutoff)
                         .OrderBy(m => m.Timestamp).ToList();

            if (stuck.Count == 0)
            {
                _fileLogger.Write("INFO", "EmailTransportService", "Rate-limit retry: no stuck messages found");
                return;
            }

            _fileLogger.Write("INFO", "EmailTransportService",
                $"Rate-limit retry: re-queuing {stuck.Count} stuck message(s) for {senderEmail}");

            foreach (var msg in stuck)
            {
                try
                {
                    var chat = await db.Chats.FindAsync(msg.ChatId);
                    if (chat == null || chat.Deleted) continue;

                    List<string> recipients;
                    string? groupId = null;
                    if (chat.Type == ChatType.Group)
                    {
                        recipients = await db.GroupMembers
                            .Where(m => m.GroupId == chat.GroupId)
                            .Select(m => m.MemberEmail)
                            .ToListAsync();
                        groupId = chat.GroupId;
                    }
                    else
                    {
                        recipients = string.IsNullOrEmpty(chat.ContactEmail)
                            ? new List<string>()
                            : new List<string> { chat.ContactEmail };
                    }

                    if (recipients.Count == 0) continue;

                    await SendMessageAsync(new OutgoingMessage
                    {
                        MessageId = msg.MessageId,
                        Content = msg.Content,
                        Recipients = recipients,
                        Timestamp = msg.Timestamp,
                        Type = MessageType.Regular,
                        GroupId = groupId,
                        Tier = BatchTier.Immediate,
                        Encrypt = true
                    });
                }
                catch (Exception ex)
                {
                    _fileLogger.Write("WARN", "EmailTransportService",
                        $"Rate-limit retry failed for msg {msg.MessageId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "EmailTransportService",
                $"RetryStuckMessagesAsync failed: {ex.Message}");
        }
    }

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
        FileLogger fileLogger,
        DatabasePathInfo dbPathInfo,
        NtpTimeService ntpTimeService)
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
        _dbPathInfo = dbPathInfo;
        _ntpTimeService = ntpTimeService;

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
        _ntpTimeService.AddFallbackHost(config.ImapServer);
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

        // Start sync loop based on SyncEngine strategy.
        // RunSyncWithRestartAsync wraps StartSyncLoopAsync in a restart loop so that
        // an unexpected fault or exit is automatically recovered without requiring a
        // full app restart or account switch.
        _idleCts = new CancellationTokenSource();
        _pollingCts = new CancellationTokenSource();
        _idleTask = RunSyncWithRestartAsync(account, _idleCts);
    }

    private const string SyncTimestampKeyPrefix = "imap_sync_last_at_";

    /// <summary>
    /// Runs <see cref="StartSyncLoopAsync"/> and automatically restarts it if it
    /// faults or exits unexpectedly. Only stops when the <paramref name="cts"/> is
    /// explicitly cancelled (i.e. account disconnect / reconnect).
    /// </summary>
    private async Task RunSyncWithRestartAsync(Account account, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await StartSyncLoopAsync(account, ct);
                // StartSyncLoopAsync only returns normally when ct is cancelled.
                // If it returns without cancellation, something exited the loop unexpectedly.
                if (!ct.IsCancellationRequested)
                    _fileLogger.Write("WARN", "EmailTransportService",
                        $"Sync loop exited unexpectedly for {account.Email} — restarting in 30s");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // intentional stop — do not restart
            }
            catch (Exception ex)
            {
                _fileLogger.Write("ERROR", "EmailTransportService",
                    $"Sync loop crashed for {account.Email}: {ex.Message} — restarting in 30s");
            }

            if (ct.IsCancellationRequested) break;

            // Brief pause before restart to avoid hammering the server on repeated failures
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }

            // Re-establish the IMAP connection before restarting the sync loop.
            // SMTP is intentionally NOT reconnected here: it is a stateless send-only
            // protocol that reconnects itself on demand inside SmtpService.SendInternalAsync
            // (via its own IsConnected check). Tying SMTP lifecycle to IMAP restarts would
            // interrupt outgoing messages for no reason.
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    _fileLogger.Write("INFO", "EmailTransportService",
                        $"Reconnecting IMAP before sync loop restart for {account.Email}");
                    try { await _imapService.DisconnectAsync(); } catch { }
                    await _imapService.ConnectAsync(
                        account.ImapServer, account.ImapPort,
                        account.Email, account.Password,
                        account.ImapUseSsl);
                }
                catch (Exception ex)
                {
                    _fileLogger.Write("WARN", "EmailTransportService",
                        $"IMAP reconnect before restart failed for {account.Email}: {ex.Message} — will retry in 30s");
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        _fileLogger.Write("INFO", "EmailTransportService",
            $"Sync loop permanently stopped for {account.Email}");
    }

    private async Task StartSyncLoopAsync(Account account, CancellationToken ct)
    {
        // Load folder name from per-account settings; fall back to default.
        string echatFolder = EchatFolder;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var folderSetting = await db.Settings.FindAsync($"acct_{account.AccountId}_folder_name");
            if (folderSetting != null && !string.IsNullOrWhiteSpace(folderSetting.Value))
                echatFolder = folderSetting.Value.Trim();
        }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "EmailTransportService", $"Could not load folder_name setting; using default '{EchatFolder}': {ex.Message}");
        }
        _fileLogger.Write("INFO", "EmailTransportService", $"Using eChat folder: '{echatFolder}' for account {account.Email}");

        // Retry messages that were stuck in Sending status (app was killed mid-send)
        await RetryStuckSendingAsync(account, ct);

        // Helper: load UID sync state for the eChat folder from DB.
        async Task<(uint UidValidity, uint LastSyncedUid)> LoadSyncState()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                var state = await db.ImapFolderStates.FindAsync(account.AccountId, echatFolder);
                return state != null ? (state.UidValidity, state.LastSyncedUid) : (0u, 0u);
            }
            catch (Exception ex)
            {
                _fileLogger.Write("WARN", "EmailTransportService", $"Could not load IMAP sync state: {ex.Message}");
                return (0u, 0u);
            }
        }

        // Helper: persist updated UID sync state after a successful sync batch.
        async Task SaveSyncState(uint uidValidity, uint lastSyncedUid)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                var state = await db.ImapFolderStates.FindAsync(account.AccountId, echatFolder);
                if (state == null)
                {
                    state = new ImapFolderSyncState { AccountId = account.AccountId, FolderName = echatFolder };
                    db.ImapFolderStates.Add(state);
                }
                state.UidValidity = uidValidity;
                state.LastSyncedUid = lastSyncedUid;
                await db.SaveChangesAsync(ct);
                _fileLogger.Write("DEBUG", "EmailTransportService",
                    $"Saved IMAP sync state for {account.Email}/{echatFolder}: uidValidity={uidValidity}, lastSyncedUid={lastSyncedUid}");
            }
            catch (Exception ex)
            {
                _fileLogger.Write("WARN", "EmailTransportService", $"Could not save IMAP sync state: {ex.Message}");
            }
        }

        // Helper: load/save sync state for INBOX (separate cursor from eChat folder).
        async Task<(uint UidValidity, uint LastSyncedUid)> LoadInboxState()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                var state = await db.ImapFolderStates.FindAsync(account.AccountId, InboxFolder);
                return state != null ? (state.UidValidity, state.LastSyncedUid) : (0u, 0u);
            }
            catch (Exception ex)
            {
                _fileLogger.Write("WARN", "EmailTransportService", $"Could not load INBOX sync state: {ex.Message}");
                return (0u, 0u);
            }
        }

        async Task SaveInboxState(uint uidValidity, uint lastSyncedUid)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                var state = await db.ImapFolderStates.FindAsync(account.AccountId, InboxFolder);
                if (state == null)
                {
                    state = new ImapFolderSyncState { AccountId = account.AccountId, FolderName = InboxFolder };
                    db.ImapFolderStates.Add(state);
                }
                state.UidValidity = uidValidity;
                state.LastSyncedUid = lastSyncedUid;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _fileLogger.Write("WARN", "EmailTransportService", $"Could not save INBOX sync state: {ex.Message}");
            }
        }

        // Startup: scan INBOX for any eChat messages that arrived while offline, move them to eChat folder.
        // SyncEchatFolderAsync will then process them via UID cursor.
        _syncEngine.RecordWakeup();
        var (inboxValidity, inboxLastUid) = await LoadInboxState();
        var (newInboxValidity, newInboxLastUid) = await _imapService.SyncInboxAsync(
            InboxFolder, echatFolder, inboxValidity, inboxLastUid, ct);
        await SaveInboxState(newInboxValidity, newInboxLastUid);

        // Sync eChat folder using UID-based cursor — no date anchors.
        var (uidValidity, lastSyncedUid) = await LoadSyncState();
        _fileLogger.Write("INFO", "EmailTransportService",
            $"Starting eChat sync for {account.Email}: storedUidValidity={uidValidity}, lastSyncedUid={lastSyncedUid}");
        var (newValidity, newLastUid) = await _imapService.SyncEchatFolderAsync(
            echatFolder, uidValidity, lastSyncedUid, ct);
        await SaveSyncState(newValidity, newLastUid);

        // Determine strategy
        var strategy = _syncEngine.GetCurrentStrategy(batteryLevel: 100, isMetered: false, isCellular: false);

        if (strategy.UseIdle)
        {
            _fileLogger.Write("INFO", "EmailTransportService", $"Starting IMAP IDLE for {account.Email} (sync interval={strategy.PollingInterval.TotalMinutes}min)");
            await _imapService.StartIdleAsync(
                InboxFolder, echatFolder, ct,
                getEchatSyncState: LoadSyncState,
                saveEchatSyncState: SaveSyncState,
                echatSyncInterval: strategy.PollingInterval);
        }
        else
        {
            _fileLogger.Write("INFO", "EmailTransportService", $"Starting polling loop for {account.Email} (interval={strategy.PollingInterval})");
            await StartPollingLoopAsync(strategy.PollingInterval, ct, echatFolder,
                LoadSyncState, SaveSyncState, LoadInboxState, SaveInboxState);
        }
    }

    private async Task StartPollingLoopAsync(
        TimeSpan interval, CancellationToken ct, string echatFolder,
        Func<Task<(uint UidValidity, uint LastSyncedUid)>> loadSyncState,
        Func<uint, uint, Task> saveSyncState,
        Func<Task<(uint UidValidity, uint LastSyncedUid)>> loadInboxState,
        Func<uint, uint, Task> saveInboxState)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(interval);

                _syncEngine.RecordWakeup();

                // Move any new eChat messages from INBOX.
                var (inboxV, inboxUid) = await loadInboxState();
                var (newInboxV, newInboxUid) = await _imapService.SyncInboxAsync(
                    InboxFolder, echatFolder, inboxV, inboxUid, timeoutCts.Token);
                await saveInboxState(newInboxV, newInboxUid);

                // UID-based sync of the eChat folder.
                var (uidValidity, lastSyncedUid) = await loadSyncState();
                var (newValidity, newLastUid) = await _imapService.SyncEchatFolderAsync(
                    echatFolder, uidValidity, lastSyncedUid, timeoutCts.Token);
                await saveSyncState(newValidity, newLastUid);
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
                    var contact = await db.Contacts.FindAsync(_accountConfig.AccountId, message.Recipients[0]);
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
                .ToListAsync(ct);
            stuck = stuck.OrderBy(m => m.Timestamp).ToList();

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
                            .Where(m => m.GroupId == chat.GroupId)
                            .Select(m => m.MemberEmail)
                            .ToListAsync(ct);
                        groupId = chat.GroupId;
                    }
                    else
                    {
                        recipients = string.IsNullOrEmpty(chat.ContactEmail)
                            ? new List<string>()
                            : new List<string> { chat.ContactEmail };
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
                                var absPath = _dbPathInfo.ResolveFilePath(att.FilePath);
                                var data = !string.IsNullOrEmpty(absPath) && File.Exists(absPath)
                                    ? await File.ReadAllBytesAsync(absPath, ct)
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

                    // Mark as Failed if encryption error is permanent (corrupted key, invalid format, etc.)
                    if (IsPermanentEncryptionError(ex))
                    {
                        try
                        {
                            msg.Status = MessageStatus.Failed;
                            await db.SaveChangesAsync(ct);
                            _fileLogger.Write("INFO", "EmailTransportService", $"Marked message {msg.MessageId} as Failed (permanent encryption error)");
                        }
                        catch (Exception saveEx)
                        {
                            _fileLogger.Write("WARN", "EmailTransportService", $"Failed to update status for {msg.MessageId}: {saveEx.Message}");
                        }
                    }
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
        try
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

            if (result == SmtpSendResult.RateLimited) OnSmtpRateLimited();
            await UpdateMessageStatusAsync(message.MessageId, result);

            // Throw on permanent errors so callers (e.g., UI ContinueWith) can detect failure
            if (result == SmtpSendResult.Permanent)
                throw new InvalidOperationException($"SMTP send failed permanently for message {message.MessageId}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ensure status is updated to Failed on any error (encryption failure, SMTP error, etc.)
            // so the message doesn't stay stuck in "Sending" forever
            _fileLogger.Write("ERROR", "SendSingle", $"Send failed for {message.MessageId}: {ex.Message}");
            try
            {
                await UpdateMessageStatusAsync(message.MessageId, SmtpSendResult.Permanent);
            }
            catch (Exception statusEx)
            {
                _fileLogger.Write("WARN", "SendSingle", $"Also failed to update status for {message.MessageId}: {statusEx.Message}");
            }
            throw;
        }
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

    private static bool IsPermanentEncryptionError(Exception ex)
    {
        var msg = ex.Message.ToLowerInvariant();
        return msg.Contains("invalid type token")
            || msg.Contains("invalid key")
            || msg.Contains("no public keys provided")
            || msg.Contains("no valid public keys")
            || msg.Contains("public key is empty");
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
        if (result == SmtpSendResult.RateLimited) OnSmtpRateLimited();
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

            // Skip regular (non-eChat) emails — they have no Chat-* headers.
            // IMAP subject filter catches most; this guard handles any that slipped through.
            bool isEChat = email.Headers["Chat-Message-ID"] != null
                        || email.Headers["Chat-System-Type"] != null
                        || email.Headers["Chat-Batch"] != null
                        || email.Headers["Chat-Encryption"] != null;
            if (!isEChat)
            {
                _fileLogger.Write("INFO", "OnMessageReceived", $"Skipping non-eChat email uid={imapUid} from={email.From.Mailboxes.FirstOrDefault()?.Address}: no Chat-* headers");
                return;
            }

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

            var contact = await db.Contacts.FindAsync(_accountConfig.AccountId, senderEmail);
            bool keyChanged;
            if (contact == null)
            {
                contact = new Contact
                {
                    AccountId = _accountConfig.AccountId,
                    Email = senderEmail,
                    DisplayName = senderEmail.Split('@')[0],
                    PublicKey = keydata
                };
                db.Contacts.Add(contact);
                keyChanged = true;
            }
            else if (contact.PublicKey != keydata)
            {
                contact.PublicKey = keydata;
                keyChanged = true;
            }
            else
            {
                keyChanged = false;
            }

            // Compute fingerprint if missing
            if (string.IsNullOrEmpty(contact.KeyFingerprint) && !string.IsNullOrEmpty(contact.PublicKey))
            {
                try
                {
                    contact.KeyFingerprint = _pgpService.GetFingerprint(contact.PublicKey);
                    keyChanged = true; // fingerprint filled in — worth persisting
                }
                catch { }
            }

            if (keyChanged)
            {
                await db.SaveChangesAsync();
                _fileLogger.Write("INFO", "EmailTransportService", $"Stored public key for {senderEmail}");
            }
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
