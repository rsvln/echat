using EChat.Core.Crypto;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Protocol;
using EChat.Core.Services;
using EChat.Core.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EChat.Core.Transport;

/// <summary>
/// Manages an independent IMAP IDLE connection for one account.
/// Used for non-active accounts so they keep receiving messages
/// while the UI is focused on a different account.
/// </summary>
public class AccountImapWorker
{
    private readonly ImapService _imapService;
    private readonly ChatMessageParser _parser;
    private readonly PgpService _pgpService;
    private readonly MessageDeduplicator _deduplicator;
    private readonly FileLogger _fileLogger;
    private readonly Account _account;
    private readonly IServiceScopeFactory _scopeFactory;

    private CancellationTokenSource? _cts;

    private const string InboxFolder = "INBOX";
    private const string EchatFolder = "eChat";

    // Tag used as prefix in every log line for this worker — full email for easy grepping
    private string LogTag => _account.Email ?? _account.AccountId[..Math.Min(8, _account.AccountId.Length)];

    public event Func<string, List<ParsedMessage>, Task>? MessagesReceived;

    public AccountImapWorker(
        Account account,
        ILogger<ImapService> imapLogger,
        ChatMessageParser parser,
        PgpService pgpService,
        MessageDeduplicator deduplicator,
        IServiceScopeFactory scopeFactory,
        FileLogger fileLogger)
    {
        _account = account;
        _fileLogger = fileLogger;
        _parser = parser;
        _pgpService = pgpService;
        _deduplicator = deduplicator;
        _scopeFactory = scopeFactory;
        _imapService = new ImapService(imapLogger, fileLogger);
        _imapService.MessageReceived += OnMessageReceivedAsync;
    }

    /// <summary>
    /// Loads the polling interval from per-account settings.
    /// Background accounts never use IMAP IDLE — polling is cheaper (no persistent TCP connection).
    /// Falls back to 15 minutes if not configured.
    /// </summary>
    private async Task<TimeSpan> LoadPollingIntervalAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var key = $"acct_{_account.AccountId}_polling_interval";
            var setting = await db.Settings.FindAsync(key);
            if (setting != null && int.TryParse(setting.Value, out var minutes) && minutes > 0)
            {
                _fileLogger.Write("INFO", "AccountImapWorker",
                    $"[{LogTag}] Loaded polling interval: {minutes}min");
                return TimeSpan.FromMinutes(minutes);
            }
        }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "AccountImapWorker",
                $"[{LogTag}] Could not load polling_interval setting: {ex.Message}");
        }
        _fileLogger.Write("INFO", "AccountImapWorker",
            $"[{LogTag}] Using default polling interval: 15min");
        return TimeSpan.FromMinutes(15);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            // Load per-account polling interval once at startup.
            // Background accounts always poll — never IDLE (IDLE needs a persistent TCP
            // connection which is the most resource-intensive sync mode).
            var pollingInterval = await LoadPollingIntervalAsync();

            // Read folder name from per-account settings; fall back to default.
            string echatFolder = EchatFolder;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                var folderSetting = await db.Settings.FindAsync($"acct_{_account.AccountId}_folder_name");
                if (folderSetting != null && !string.IsNullOrWhiteSpace(folderSetting.Value))
                    echatFolder = folderSetting.Value.Trim();
            }
            catch (Exception ex)
            {
                _fileLogger.Write("WARN", "AccountImapWorker", $"[{LogTag}] Could not load folder_name setting; using default '{EchatFolder}': {ex.Message}");
            }
            _fileLogger.Write("INFO", "AccountImapWorker", $"[{LogTag}] Using eChat folder: '{echatFolder}' for account {_account.Email}");

            // UID sync state helpers scoped to this worker's account + folder.
            async Task<(uint UidValidity, uint LastSyncedUid)> LoadSyncState(string folder)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                    var state = await db.ImapFolderStates.FindAsync(_account.AccountId, folder);
                    return state != null ? (state.UidValidity, state.LastSyncedUid) : (0u, 0u);
                }
                catch (Exception ex)
                {
                    _fileLogger.Write("WARN", "AccountImapWorker", $"[{LogTag}] Could not load sync state for {folder}: {ex.Message}");
                    return (0u, 0u);
                }
            }

            async Task SaveSyncState(string folder, uint uidValidity, uint lastSyncedUid)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                    var state = await db.ImapFolderStates.FindAsync(_account.AccountId, folder);
                    if (state == null)
                    {
                        state = new ImapFolderSyncState { AccountId = _account.AccountId, FolderName = folder };
                        db.ImapFolderStates.Add(state);
                    }
                    state.UidValidity = uidValidity;
                    state.LastSyncedUid = lastSyncedUid;
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _fileLogger.Write("WARN", "AccountImapWorker", $"[{LogTag}] Could not save sync state for {folder}: {ex.Message}");
                }
            }

            Task<(uint, uint)> LoadEchatState() => LoadSyncState(echatFolder);
            Task SaveEchatState(uint v, uint u) => SaveSyncState(echatFolder, v, u);
            Task<(uint, uint)> LoadInboxStateLocal() => LoadSyncState(InboxFolder);
            Task SaveInboxStateLocal(uint v, uint u) => SaveSyncState(InboxFolder, v, u);

            // Restart loop: if the sync loop exits unexpectedly, restart it after a delay.
            // Only stops when ct is cancelled (i.e. Stop() is called).
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _imapService.ConnectAsync(
                        _account.ImapServer, _account.ImapPort,
                        _account.Email, _account.Password, _account.ImapUseSsl);

                    // Move any new eChat messages from INBOX.
                    var (inboxV, inboxUid) = await LoadInboxStateLocal();
                    var (newInboxV, newInboxUid) = await _imapService.SyncInboxAsync(
                        InboxFolder, echatFolder, inboxV, inboxUid, ct);
                    await SaveInboxStateLocal(newInboxV, newInboxUid);

                    // UID-based sync of the eChat folder.
                    var (uidValidity, lastSyncedUid) = await LoadEchatState();
                    _fileLogger.Write("INFO", "AccountImapWorker",
                        $"[{LogTag}] Starting eChat sync: lastSyncedUid={lastSyncedUid}");
                    var (newValidity, newLastUid) = await _imapService.SyncEchatFolderAsync(
                        echatFolder, uidValidity, lastSyncedUid, ct);
                    await SaveEchatState(newValidity, newLastUid);

                    // Background accounts poll on interval — no IMAP IDLE.
                    // IDLE requires a persistent TCP connection (most resource-intensive mode),
                    // which is wrong for accounts that are not currently active in the UI.
                    _fileLogger.Write("INFO", "AccountImapWorker",
                        $"[{LogTag}] Entering polling loop (interval={pollingInterval.TotalMinutes:F0}min) for {_account.Email}");

                    while (!ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(pollingInterval, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }

                        // If the server closed the connection between polls, let the outer
                        // restart loop reconnect rather than trying to sync on a dead socket.
                        if (!_imapService.IsConnected)
                            throw new InvalidOperationException("IMAP connection lost during polling interval");

                        _fileLogger.Write("INFO", "AccountImapWorker",
                            $"[{LogTag}] Poll tick for {_account.Email}");

                        var (iv2, iu2) = await LoadInboxStateLocal();
                        var (niv2, niu2) = await _imapService.SyncInboxAsync(
                            InboxFolder, echatFolder, iv2, iu2, ct);
                        await SaveInboxStateLocal(niv2, niu2);

                        var (ev2, eu2) = await LoadEchatState();
                        var (nev2, neu2) = await _imapService.SyncEchatFolderAsync(
                            echatFolder, ev2, eu2, ct);
                        await SaveEchatState(nev2, neu2);

                        _fileLogger.Write("INFO", "AccountImapWorker",
                            $"[{LogTag}] Poll complete for {_account.Email}, next in {pollingInterval.TotalMinutes:F0}min");
                    }

                    // Polling loop exits normally only when ct is cancelled.
                    // If we reach here without cancellation, something is wrong.
                    if (!ct.IsCancellationRequested)
                        _fileLogger.Write("WARN", "AccountImapWorker",
                            $"[{LogTag}] Polling loop exited unexpectedly for {_account.Email} — restarting in 30s");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break; // intentional stop — do not restart
                }
                catch (Exception ex)
                {
                    _fileLogger.Write("ERROR", "AccountImapWorker", $"[{LogTag}] Sync loop crashed for {_account.Email}: {ex.Message} — restarting in 30s");
                }

                if (ct.IsCancellationRequested) break;

                // Brief pause before restart to avoid hammering the server on repeated failures
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { break; }

                // Re-establish the IMAP connection before restarting the sync loop.
                if (!ct.IsCancellationRequested)
                {
                    try
                    {
                        _fileLogger.Write("INFO", "AccountImapWorker", $"[{LogTag}] Reconnecting IMAP before sync loop restart for {_account.Email}");
                        try { await _imapService.DisconnectAsync(); } catch { }
                        await _imapService.ConnectAsync(
                            _account.ImapServer, _account.ImapPort,
                            _account.Email, _account.Password, _account.ImapUseSsl);
                    }
                    catch (Exception ex)
                    {
                        _fileLogger.Write("WARN", "AccountImapWorker", $"[{LogTag}] IMAP reconnect before restart failed for {_account.Email}: {ex.Message} — will retry in 30s");
                        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { break; }
                    }
                }
            }
            _fileLogger.Write("INFO", "AccountImapWorker", $"[{LogTag}] Sync loop permanently stopped for {_account.Email}");
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task OnMessageReceivedAsync(MimeMessage email, long imapUid, string imapFolder)
    {
        var messages = _parser.Parse(email);

        foreach (var m in messages)
        {
            m.ImapUid = imapUid > 0 ? imapUid : null;
            m.ImapFolder = string.IsNullOrEmpty(imapFolder) ? null : imapFolder;
        }

        // Decrypt PGP-inline encrypted messages — try group key first, then personal key
        foreach (var msg in messages)
        {
            if (msg.Headers.Encryption != "pgp-inline" || string.IsNullOrEmpty(msg.Content))
                continue;

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
                        }
                        catch
                        {
                            _fileLogger.Write("DEBUG", "AccountImapWorker", $"[{LogTag}] Group key found but decryption failed for msgId={msg.Headers.MessageId}, trying personal key");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _fileLogger.Write("WARN", "AccountImapWorker", $"[{LogTag}] Failed to look up group key for groupId={msg.Headers.GroupId}: {ex.Message}");
                }
            }

            // Step 2: Fall back to the account's personal private key
            if (!decrypted && _account.PrivateKey != null)
            {
                try
                {
                    var decryptedContent = await _pgpService.DecryptAsync(msg.Content, _account.PrivateKey, _account.Password);
                    _parser.ApplyDecryptedContent(msg, decryptedContent);
                    msg.IsEncrypted = false;
                }
                catch (Exception ex)
                {
                    _fileLogger.Write("WARN", "AccountImapWorker", $"[{LogTag}] Failed to decrypt message msgId={msg.Headers.MessageId}: {ex.Message}");
                }
            }
        }

        foreach (var msg in messages)
            _fileLogger.Write("DEBUG", "AccountImapWorker",
                $"[{LogTag}] Parsed msg msgId={msg.Headers.MessageId}: encrypted={msg.IsEncrypted}, attachments={msg.Attachments?.Count ?? 0}, contentLen={msg.Content?.Length ?? 0}");

        var newMessages = messages.Where(m => !_deduplicator.IsDuplicate(_account.AccountId, m)).ToList();
        _fileLogger.Write("INFO", "AccountImapWorker", $"[{LogTag}] Dedup for {_account.Email}: {messages.Count} total, {newMessages.Count} new, {messages.Count - newMessages.Count} duplicates");
        if (newMessages.Any() && MessagesReceived != null)
            await MessagesReceived(_account.AccountId, newMessages);
    }
}
