using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using EChat.Core.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Microsoft.Extensions.Logging;

namespace EChat.Core.Transport;

public class ImapService : IDisposable
{
    private readonly ILogger<ImapService> _logger;
    private readonly ImapClient _client;
    private readonly FileLogger _fileLogger;

    private string? _server;
    private int _port;
    private string? _email;
    private string? _password;
    private bool _useSsl;

    // Short tag for log prefixes: "yarustam", "avchatmail", etc.
    private string LogTag => _email?.Split('@')[0] ?? "imap";

    // Accept certificates where only revocation status is unknown (common on Android/mobile).
    // Reject certificates with real errors (expired, wrong host, untrusted root, etc.).
    private static bool AllowRevocationUnknown(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None) return true;
        if (chain == null) return false;
        foreach (var status in chain.ChainStatus)
        {
            if (status.Status is X509ChainStatusFlags.RevocationStatusUnknown
                              or X509ChainStatusFlags.OfflineRevocation)
                continue;
            if (status.Status != X509ChainStatusFlags.NoError)
                return false;
        }
        // Only revocation-unknown errors remain — allow
        return (sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;
    }

    /// <summary>
    /// Fired for each incoming eChat email.
    /// Parameters: (message, imapUid, folderName).
    /// </summary>
    public event Func<MimeMessage, long, string, Task>? MessageReceived;

    /// <summary>
    /// True when the underlying IMAP client is connected and authenticated.
    /// </summary>
    public bool IsConnected => _client.IsConnected && _client.IsAuthenticated;

    public ImapService(ILogger<ImapService> logger, FileLogger? fileLogger = null)
    {
        _logger = logger;
        _fileLogger = fileLogger ?? new FileLogger(".");
        _client = new ImapClient
        {
            ServerCertificateValidationCallback = AllowRevocationUnknown,
            // Allow up to 5 min per IMAP operation (default is 2 min which is too short
            // for large messages / slow servers and causes TaskCanceledException mid-fetch).
            Timeout = (int)TimeSpan.FromMinutes(5).TotalMilliseconds
        };
   }

    /// <summary>
    /// Timeout used when fetching messages (connect, auth, download). Default 5 min.
    /// Kept large so big attachments don't time out.
    /// </summary>
    private int _fetchTimeout = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;

    /// <summary>
    /// Short timeout used only around the IDLE command itself (DONE + OK round-trip).
    /// On Android set to ~30 s so a silently-dead TCP connection is detected quickly.
    /// </summary>
    private int _idleTimeout = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;

    /// <summary>
    /// Sets the IDLE-specific timeout (DONE round-trip). The fetch timeout stays at 5 min
    /// so large attachment downloads don't fail. Call with 30 s on Android.
    /// </summary>
    public void SetIdleTimeout(TimeSpan timeout)
    {
        _idleTimeout = (int)timeout.TotalMilliseconds;
        _fileLogger.Write("INFO", "ImapService", $"[{LogTag}] IDLE timeout set to {timeout.TotalSeconds}s");
    }

    public async Task ConnectAsync(string server, int port, string email, string password, bool useSsl = true)
    {
        _server = server; _port = port; _email = email; _password = password; _useSsl = useSsl;
        try
        {
            // If already connected, disconnect first to avoid "already connected" errors
            if (_client.IsConnected)
            {
                try { await _client.DisconnectAsync(false); } catch { }
            }
            await _client.ConnectAsync(server, port, useSsl);
            await _client.AuthenticateAsync(email, password);
            _fileLogger.Write("INFO", "ImapService", $"[{LogTag}] Connected to IMAP server {server}");
       }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "ImapService", $"[{LogTag}] Failed to connect to IMAP server: {ex.Message}");
            throw;
       }
   }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected && _client.IsAuthenticated) return;
        if (_server == null) throw new InvalidOperationException("IMAP never connected");
        _fileLogger.Write("INFO", "ImapService", $"[{LogTag}] IMAP reconnecting to {_server}");
        try { await _client.DisconnectAsync(false); } catch { }
        await _client.ConnectAsync(_server, _port, _useSsl, ct);
        await _client.AuthenticateAsync(_email, _password, ct);
   }

    public async Task<IMailFolder> GetOrCreateFolderAsync(string folderName)
    {
        var personalNamespace = _client.PersonalNamespaces[0];
        var parentFolder = await _client.GetFolderAsync(personalNamespace.Path);

        // Case-insensitive match: "Echat" on server == "eChat" in settings.
        // Enumerate subfolders rather than calling GetFolderAsync by exact name
        // so we never create a duplicate folder with different casing.
        var subfolders = await parentFolder.GetSubfoldersAsync(false);
        var existing = subfolders.FirstOrDefault(f =>
            string.Equals(f.Name, folderName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return existing;

        var folder = await parentFolder.CreateAsync(folderName, true);
        _fileLogger.Write("INFO", "ImapService", $"[{LogTag}] Created IMAP folder {folderName}");
        return folder;
    }

    /// <summary>
    /// IDLEs on <paramref name="inboxFolderName"/>. When new mail arrives, fetches headers,
    /// fires <see cref="MessageReceived"/> for εChat messages, and moves them to
    /// <paramref name="echatFolderName"/>. Non-εChat messages are left untouched.
    /// Also periodically re-syncs <paramref name="echatFolderName"/> so that messages
    /// moved there by another device (sent-to-self sync copies) are picked up.
    /// <paramref name="getEchatSyncState"/> / <paramref name="saveEchatSyncState"/>
    /// are optional callbacks to read/persist UID sync state across the IDLE lifetime.
    /// </summary>
    public async Task StartIdleAsync(string inboxFolderName, string echatFolderName,
        CancellationToken cancellationToken,
        Func<Task<(uint UidValidity, uint LastSyncedUid)>>? getEchatSyncState = null,
        Func<uint, uint, Task>? saveEchatSyncState = null,
        TimeSpan? echatSyncInterval = null)
    {
        var lastEchatSync = DateTime.UtcNow;
        // How often to re-check the eChat folder for messages moved by other devices.
        // Defaults to 3 minutes, but can be overridden by caller based on sync profile.
        var syncInterval = echatSyncInterval ?? TimeSpan.FromMinutes(3);

        IMailFolder? inbox = null;

        async Task<IMailFolder> OpenInboxAsync()
        {
            await EnsureConnectedAsync(cancellationToken);
            var folder = inboxFolderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
                ? _client.Inbox
                : await GetOrCreateFolderAsync(inboxFolderName);
            if (!folder.IsOpen)
                await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
            return folder;
       }

        async Task SyncEchatIfDueAsync()
        {
            if (DateTime.UtcNow - lastEchatSync < syncInterval) return;
            try
            {
                // Load UID sync state (if caller provided callbacks), run UID-based sync, save result.
                var (uidValidity, lastSyncedUid) = getEchatSyncState != null
                    ? await getEchatSyncState()
                    : (0u, 0u);
                var (newValidity, newLastUid) = await SyncEchatFolderAsync(
                    echatFolderName, uidValidity, lastSyncedUid, cancellationToken);
                if (saveEchatSyncState != null && newLastUid != lastSyncedUid)
                    await saveEchatSyncState(newValidity, newLastUid);

                lastEchatSync = DateTime.UtcNow;
                // Do NOT reopen inbox here. Setting null forces the reconnect block on the next
                // iteration to reopen AND drain — catching any messages that arrived while the
                // eChat folder was being synced (server won't fire EXISTS for those).
                inbox = null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // SyncEchatFolderAsync may have closed INBOX via SELECT before throwing.
                // Force reopen so the next iteration doesn't try to IDLE on a closed folder.
                inbox = null;
                _fileLogger.Write("WARN", "ImapService", $"[{LogTag}] Periodic eChat folder sync failed: {ex.Message}");
            }
        }

        try
        {
            inbox = await OpenInboxAsync();
            await ProcessChatMessagesAsync(inbox, echatFolderName, cancellationToken);
            // Ignore return value here — if it failed we'll retry in the IDLE loop anyway.
       }
        catch (OperationCanceledException) { return; }
        catch (System.IO.IOException ex)
        {
            _fileLogger.Write("WARN", "ImapService", $"[{LogTag}] IMAP IO error on initial open, will retry in loop: {ex.Message}");
       }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "ImapService", $"[{LogTag}] IMAP failed on initial open, will retry in loop: {ex.Message}");
       }

        while (!cancellationToken.IsCancellationRequested)
        {
            // Ensure we have a valid open inbox before each IDLE cycle
            if (inbox == null || !inbox.IsOpen || !_client.IsConnected)
            {
                try
                {
                    inbox = await OpenInboxAsync();
               }
                catch (OperationCanceledException) { break; }
                catch (System.IO.IOException ex)
                {
                    _fileLogger.Write("WARN", "ImapService", $"[{LogTag}] IMAP reconnect IO error, retrying in 15s: {ex.Message}");
                    try { await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken); } catch { break; }
                    continue;
               }
                catch (Exception ex)
                {
                    _fileLogger.Write("ERROR", "ImapService", $"[{LogTag}] IMAP reconnect failed, retrying in 15s: {ex.Message}");
                    try { await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken); } catch { break; }
                    continue;
               }

                // Drain any messages already sitting in inbox before entering IDLE.
                // After reconnect the server won't send a COUNT change for mail that was
                // already there, so IDLE would miss them until the sync-interval timeout.
                bool ok = await ProcessChatMessagesAsync(inbox, echatFolderName, cancellationToken);
                if (!ok) { inbox = null; continue; }
                await SyncEchatIfDueAsync();
           }

            // Break IDLE after syncInterval so we can re-check the eChat folder.
            using var idleDone = new CancellationTokenSource();
            using var idleTimeout = new CancellationTokenSource(syncInterval);
            using var idleOrTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                idleDone.Token, idleTimeout.Token, cancellationToken);

            void OnCountChanged(object? s, EventArgs e)
            {
                try { idleDone.Cancel(); } catch (ObjectDisposedException) { }
           }

            inbox.CountChanged += OnCountChanged;
            try
            {
                if (_client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    // Use short timeout for the IDLE command so a dead TCP connection is
                    // detected quickly (Android: 30 s; Desktop: 5 min).
                    _client.Timeout = _idleTimeout;
                    try { await _client.IdleAsync(idleOrTimeout.Token, cancellationToken); }
                    finally { _client.Timeout = _fetchTimeout; }
                }
                else
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    await _client.NoOpAsync(cancellationToken);
               }
           }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // idleDone or idleTimeout cancelled — new mail or periodic check, fall through
           }
            catch (OperationCanceledException) { break; }
            catch (System.IO.IOException ex)
            {
                _fileLogger.Write("WARN", "ImapService", $"[{LogTag}] IMAP IDLE IO error — will reconnect: {ex.Message}");
                inbox = null;
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); } catch { break; }
           }
            catch (Exception ex)
            {
                _fileLogger.Write("ERROR", "ImapService", $"[{LogTag}] IMAP IDLE error — will reconnect: {ex.Message}");
                inbox = null; // force reconnect on next iteration
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); } catch { break; }
           }
            finally
            {
                inbox?.CountChanged -= OnCountChanged;
           }

            if (inbox != null)
            {
                bool ok = await ProcessChatMessagesAsync(inbox, echatFolderName, cancellationToken);
                if (!ok)
                {
                    // Fetch error — force inbox reopen on next iteration so we skip IDLE
                    // and immediately retry the unprocessed messages.
                    inbox = null;
                }
           }

            // Periodically check eChat folder for messages moved there by other devices.
            await SyncEchatIfDueAsync();
       }
   }

    // Build the IMAP subject search query from the configured folder/subject name.
    // "[eChat]" when folderName="eChat". Use only ASCII — non-ASCII in IMAP SEARCH
    // is not universally supported and silently returns empty results on many servers.
    private static SearchQuery BuildSubjectQuery(string folderName) =>
        SearchQuery.SubjectContains($"[{folderName}]");

    /// <returns>true = processed normally; false = broke early due to fetch error (caller should skip IDLE and retry).</returns>
    private async Task<bool> ProcessChatMessagesAsync(IMailFolder inbox, string echatFolderName, CancellationToken ct)
    {
        IList<UniqueId> uids;
        var subjectToken = $"[{echatFolderName}]";

        try
        {
            uids = await inbox.SearchAsync(BuildSubjectQuery(echatFolderName), ct);
            if (uids.Count == 0)
            {
                // Fallback: some IMAP servers (e.g. Yandex) silently return 0 results for
                // SEARCH SUBJECT queries even when matching messages exist. In that case
                // scan unseen messages delivered in the last 4 hours — narrow enough to
                // avoid scanning a huge inbox (tens of thousands of unread messages) while
                // still catching any eChat email that just arrived.
                _fileLogger.Write("DEBUG", "ImapService", $"[{LogTag}] SubjectContains returned 0 results; falling back to NotSeen+DeliveredAfter(-4h) search");
                uids = await inbox.SearchAsync(
                    SearchQuery.NotSeen.And(SearchQuery.DeliveredAfter(DateTime.UtcNow.AddHours(-4))), ct);
                if (uids.Count == 0) return true;
                _fileLogger.Write("DEBUG", "ImapService", $"[{LogTag}] Fallback search found {uids.Count} unseen message(s) in inbox, will filter by subject token");
            }
       }
        catch (OperationCanceledException) { throw; }
        catch (System.IO.IOException ex)
        {
            _fileLogger.Write("WARN", "ImapService", $"[{LogTag}] IO error searching inbox for eChat messages: {ex.Message}");
            return false;
       }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "ImapService", $"[{LogTag}] Failed to search inbox for eChat messages: {ex.Message}");
            return false;
       }

        IMailFolder? echatFolder = null;
        var movedEchatUids = new List<UniqueId>();
        bool fetchError = false;

        foreach (var uid in uids)
        {
            ct.ThrowIfCancellationRequested();

            MimeMessage message;
            try
            {
                message = await inbox.GetMessageAsync(uid, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // After a timeout or transient error the inbox folder reference is stale
                // (the underlying connection may have been reset). Break so the outer
                // StartIdleAsync loop can re-open inbox cleanly on the next iteration.
                // The message will be picked up on the very next IDLE cycle.
                _fileLogger.Write("WARN", "ImapService", $"[{LogTag}] Failed to fetch inbox message {uid}: {ex.Message}. Breaking to reconnect.");
                fetchError = true;
                break;
            }

            // Client-side subject guard — IMAP SEARCH may return false positives on some
            // servers (e.g. Yandex doesn't always honour SubjectContains reliably).
            if (message.Subject?.Contains(subjectToken, StringComparison.OrdinalIgnoreCase) != true)
                continue;

            // Move to eChat folder BEFORE firing the event so we can pass the stable
            // eChat UID (inbox UIDs change on move). If the move fails, fall back to
            // the inbox UID so the message is still processed.
            long echatUid = uid.Id;
            string echatFolder2 = echatFolderName;
            UniqueId? newUid = null;
            try
            {
                echatFolder ??= await GetOrCreateFolderAsync(echatFolderName);
                newUid = await inbox.MoveToAsync(uid, echatFolder, ct);
                if (newUid.HasValue) echatUid = newUid.Value.Id;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _fileLogger.Write("ERROR", "ImapService", $"[{LogTag}] Failed to move message {uid} to {echatFolderName}: {ex.Message}");
                echatFolder2 = inbox.Name; // stayed in inbox
                try { await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); } catch { }
            }

            if (MessageReceived != null)
            {
                try { await MessageReceived(message, echatUid, echatFolder2); }
                catch (Exception ex) { _fileLogger.Write("ERROR", "ImapService", $"[{LogTag}] Error in MessageReceived handler: {ex.Message}"); }
            }

            // Track UID for bulk Seen marking after the loop
            if (newUid.HasValue)
                movedEchatUids.Add(newUid.Value);
        }

        // Mark all moved messages as Seen in the eChat folder so mail clients don't show
        // them as unread. Opening echatFolder implicitly closes inbox — that's fine because
        // the IDLE loop re-opens inbox at the top of each iteration.
        if (movedEchatUids.Count > 0)
        {
            try
            {
                var ef = echatFolder ?? await GetOrCreateFolderAsync(echatFolderName);
                if (!ef.IsOpen)
                    await ef.OpenAsync(FolderAccess.ReadWrite, ct);
                await ef.AddFlagsAsync(movedEchatUids, MessageFlags.Seen, true, ct);
                try { await ef.CloseAsync(false, ct); } catch { }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _fileLogger.Write("WARN", "ImapService", $"[{LogTag}] Failed to mark echat messages as Seen: {ex.Message}"); }
        }

        return !fetchError;
    }

    private readonly object knownMessageIdsLock = new();

    /// <summary>
    /// Syncs the eChat IMAP folder using UID-based discovery (no date anchors).
    /// Only fetches messages with UID > <paramref name="storedLastSyncedUid"/>.
    /// Fetches in descending order (newest first) for faster UI appearance, but
    /// advances <paramref name="storedLastSyncedUid"/> only once a contiguous prefix
    /// of the server UID list is fully downloaded (Variant A / two-cursor approach).
    /// </summary>
    /// <returns>
    /// Updated (UidValidity, LastSyncedUid) to be persisted by the caller.
    /// If UIDVALIDITY changed, LastSyncedUid is reset to 0 and a full re-sync occurs.
    /// </returns>
    public async Task<(uint UidValidity, uint LastSyncedUid)> SyncEchatFolderAsync(
        string echatFolderName,
        uint storedUidValidity,
        uint storedLastSyncedUid,
        CancellationToken ct)
    {
        try
        {
            var echatFolder = await GetOrCreateFolderAsync(echatFolderName);
            await echatFolder.OpenAsync(FolderAccess.ReadWrite, ct);

            var serverUidValidity = (uint)echatFolder.UidValidity;

            // If UidValidity changed, the server rebuilt its UID sequence — reset to 0 and re-sync all.
            if (storedUidValidity != 0 && serverUidValidity != storedUidValidity)
            {
                _fileLogger.Write("WARN", "ImapService",
                    $"[{LogTag}] UidValidity changed ({storedUidValidity} → {serverUidValidity}) for {echatFolderName}. Full re-sync.");
                storedLastSyncedUid = 0;
            }

            // Search all eChat messages in folder (returns only UIDs — cheap, no body download).
            // Try subject filter first. If the server returns 0 (Yandex ignores SEARCH SUBJECT
            // in subfolders), fall back to SearchQuery.All — everything in the eChat folder
            // is an eChat message anyway (placed there by our own code).
            var subjectToken = $"[{echatFolderName}]";
            var allUids = await echatFolder.SearchAsync(BuildSubjectQuery(echatFolderName), ct);
            if (allUids.Count == 0)
            {
                _fileLogger.Write("DEBUG", "ImapService",
                    $"[{LogTag}] Subject search returned 0 in eChat folder; falling back to SearchQuery.All");
                allUids = await echatFolder.SearchAsync(SearchQuery.All, ct);
            }

            // Client-side: keep only UIDs we haven't processed yet.
            var newUids = allUids.Where(u => u.Id > storedLastSyncedUid).OrderBy(u => u.Id).ToList();

            _fileLogger.Write("INFO", "ImapService",
                $"[{LogTag}] eChat sync: {allUids.Count} total UIDs in folder, {newUids.Count} new (lastSyncedUid={storedLastSyncedUid})");

            if (newUids.Count == 0)
            {
                try { await echatFolder.CloseAsync(false, ct); } catch { }
                return (serverUidValidity, storedLastSyncedUid);
            }

            // Fetch in ASC order (oldest first) so the cursor advances after each save.
            // This ensures that if the app is killed mid-batch, on restart we skip
            // already-processed UIDs (DB dedup) and continue from where we left off.
            // newUids is already sorted ASC.
            var savedUids = new HashSet<uint>();

            var syncSeenUids = new List<UniqueId>();
            var lastSyncedUid = storedLastSyncedUid;

            foreach (var uid in newUids)
            {
                ct.ThrowIfCancellationRequested();

                MimeMessage message;
                try { message = await echatFolder.GetMessageAsync(uid, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _fileLogger.Write("WARN", "ImapService", $"[{LogTag}] Failed to fetch eChat message {uid}: {ex.Message}. Stopping batch.");
                    break;
                }

                // Client-side subject guard (server may return false positives).
                if (message.Subject?.Contains(subjectToken, StringComparison.OrdinalIgnoreCase) != true)
                {
                    // Subject doesn't match — mark as "seen" so cursor can advance past it.
                    savedUids.Add(uid.Id);
                    continue;
                }

                bool fired = false;
                if (MessageReceived != null)
                {
                    try
                    {
                        await MessageReceived(message, (long)uid.Id, echatFolderName);
                        fired = true;
                    }
                    catch (Exception ex)
                    {
                        _fileLogger.Write("ERROR", "ImapService", $"[{LogTag}] Error processing eChat message {uid}: {ex.Message}");
                        // Don't mark as saved — will retry on next sync.
                        continue;
                    }
                }

                if (fired || MessageReceived == null)
                {
                    savedUids.Add(uid.Id);
                    syncSeenUids.Add(uid);
                }

                // Advance the contiguous cursor: scan newUids (ASC) from the beginning,
                // advance as far as we have consecutive saved entries.
                lastSyncedUid = ComputeNewLastSyncedUid(newUids, savedUids, storedLastSyncedUid);
           }

            // Bulk mark processed messages as Seen so mail clients don't show unread counters.
            if (syncSeenUids.Count > 0)
                try { await echatFolder.AddFlagsAsync(syncSeenUids, MessageFlags.Seen, true, ct); } catch { }

            try { await echatFolder.CloseAsync(false, ct); } catch { }

            return (serverUidValidity, lastSyncedUid);
       }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "ImapService", $"[{LogTag}] Failed to sync eChat folder: {ex.Message}");
            return (storedUidValidity, storedLastSyncedUid);
       }
   }

    /// <summary>
    /// Scans <paramref name="serverUids"/> (sorted ASC) from the start and returns
    /// the highest UID for which all preceding UIDs in the list are also in <paramref name="savedUids"/>.
    /// This ensures the cursor only advances over a gap-free prefix.
    /// </summary>
    private static uint ComputeNewLastSyncedUid(List<UniqueId> serverUids, HashSet<uint> savedUids, uint currentLastSynced)
    {
        uint result = currentLastSynced;
        foreach (var uid in serverUids)
        {
            if (!savedUids.Contains(uid.Id)) break;
            result = uid.Id;
        }
        return result;
    }

    /// <summary>
    /// Startup INBOX scan: catches eChat messages that arrived while the app was offline.
    /// Uses UID-based cursor (same pattern as SyncEchatFolderAsync) to avoid rescanning
    /// old messages on every start.
    /// Moves matched messages to the eChat folder — SyncEchatFolderAsync then processes
    /// them via its own UID cursor with the correct eChat-folder UID.
    /// Falls back to a 7-day date window on the very first run (storedLastSyncedUid == 0).
    /// </summary>
    /// <returns>Updated (UidValidity, LastSyncedUid) for the INBOX folder to be persisted by caller.</returns>
    public async Task<(uint UidValidity, uint LastSyncedUid)> SyncInboxAsync(
        string inboxFolderName, string echatFolderName,
        uint storedUidValidity, uint storedLastSyncedUid,
        CancellationToken ct)
    {
        var subjectToken = $"[{echatFolderName}]";
        try
        {
            var inbox = _client.Inbox;
            if (!inbox.IsOpen)
                await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

            var serverUidValidity = (uint)inbox.UidValidity;

            // Reset cursor if UidValidity changed.
            if (storedUidValidity != 0 && serverUidValidity != storedUidValidity)
            {
                _fileLogger.Write("WARN", "ImapService",
                    $"[{LogTag}] INBOX UidValidity changed ({storedUidValidity} → {serverUidValidity}). Resetting cursor.");
                storedLastSyncedUid = 0;
            }

            // First run: seed cursor from a 7-day window so we don't miss recent arrivals,
            // but don't go through potentially huge inbox history.
            IList<UniqueId> candidates;
            if (storedLastSyncedUid == 0)
            {
                var since = DateTime.UtcNow.AddDays(-7);
                try
                {
                    candidates = await inbox.SearchAsync(
                        BuildSubjectQuery(echatFolderName).And(SearchQuery.DeliveredAfter(since)), ct);
                }
                catch
                {
                    candidates = await inbox.SearchAsync(SearchQuery.DeliveredAfter(since), ct);
                }
                // On Yandex, subject search returns 0 — fall back to date-only, filter client-side.
                if (candidates.Count == 0)
                    candidates = await inbox.SearchAsync(SearchQuery.DeliveredAfter(since), ct);

                _fileLogger.Write("INFO", "ImapService",
                    $"[{LogTag}] SyncInbox first run: {candidates.Count} candidate(s) in last 7 days");
            }
            else
            {
                // Subsequent runs: only look at new UIDs.
                candidates = await inbox.SearchAsync(SearchQuery.All, ct);
                candidates = candidates.Where(u => u.Id > storedLastSyncedUid).ToList();
                _fileLogger.Write("INFO", "ImapService",
                    $"[{LogTag}] SyncInbox: {candidates.Count} new candidate(s) after uid={storedLastSyncedUid}");
            }

            if (candidates.Count == 0)
                return (serverUidValidity, storedLastSyncedUid);

            IMailFolder? echatFolder = null;
            var highestProcessed = storedLastSyncedUid;

            foreach (var uid in candidates.OrderBy(u => u.Id))
            {
                ct.ThrowIfCancellationRequested();

                // Fetch headers only (cheap) for subject guard.
                var summaries = await inbox.FetchAsync(new[] { uid },
                    MessageSummaryItems.Headers | MessageSummaryItems.Flags, ct);
                var summary = summaries.FirstOrDefault();
                if (summary == null) { highestProcessed = Math.Max(highestProcessed, uid.Id); continue; }

                var subject = summary.Headers?[MimeKit.HeaderId.Subject];
                bool isEchat = subject?.Contains(subjectToken, StringComparison.OrdinalIgnoreCase) == true;

                if (!isEchat)
                {
                    // Not an eChat message — advance cursor past it so we don't re-examine it.
                    highestProcessed = Math.Max(highestProcessed, uid.Id);
                    continue;
                }

                // Move to eChat folder — SyncEchatFolderAsync processes it via its UID cursor.
                try
                {
                    echatFolder ??= await GetOrCreateFolderAsync(echatFolderName);
                    await inbox.MoveToAsync(uid, echatFolder, ct);
                    _fileLogger.Write("INFO", "ImapService", $"[{LogTag}] SyncInbox: moved uid={uid} to {echatFolderName}");
                    highestProcessed = Math.Max(highestProcessed, uid.Id);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _fileLogger.Write("WARN", "ImapService", $"[{LogTag}] SyncInbox: failed to move uid={uid}: {ex.Message}");
                    // Don't advance cursor — will retry on next sync.
                }
            }

            return (serverUidValidity, highestProcessed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "ImapService", $"[{LogTag}] Failed to sync inbox: {ex.Message}");
            return (storedUidValidity, storedLastSyncedUid);
        }
    }

    public async Task<List<MimeMessage>> FetchNewMessagesAsync(string folderName)
    {
        var messages = new List<MimeMessage>();
        var folder = await GetOrCreateFolderAsync(folderName);
        await folder.OpenAsync(FolderAccess.ReadWrite);

        var uids = await folder.SearchAsync(SearchQuery.NotSeen);
        foreach (var uid in uids)
        {
            var message = await folder.GetMessageAsync(uid);
            messages.Add(message);
            await folder.AddFlagsAsync(uid, MessageFlags.Seen, true);
       }

        return messages;
   }

    public async Task MoveToFolderAsync(IMailFolder sourceFolder, UniqueId uid, string targetFolderName)
    {
        var targetFolder = await GetOrCreateFolderAsync(targetFolderName);
        await sourceFolder.MoveToAsync(uid, targetFolder);
   }

    /// <summary>
    /// Permanently deletes the given messages from the specified IMAP folder.
    /// Silently ignores UIDs that no longer exist on the server.
    /// </summary>
    public async Task DeleteMessagesAsync(string folderName, IEnumerable<long> uids, CancellationToken ct = default)
    {
        var uidList = uids.Select(u => new UniqueId((uint)u)).ToList();
        if (uidList.Count == 0) return;
        try
        {
            await EnsureConnectedAsync(ct);
            IMailFolder folder;
            if (folderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
                folder = _client.Inbox;
            else
                folder = await GetOrCreateFolderAsync(folderName);

            if (!folder.IsOpen)
                await folder.OpenAsync(FolderAccess.ReadWrite, ct);

            await folder.AddFlagsAsync(uidList, MessageFlags.Deleted, true, ct);
            await folder.ExpungeAsync(uidList, ct);
            _fileLogger.Write("INFO", "ImapService", $"[{LogTag}] Deleted {uidList.Count} message(s) from IMAP folder {folderName}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "ImapService", $"[{LogTag}] Failed to delete messages from IMAP folder {folderName}: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_client.IsConnected)
            await _client.DisconnectAsync(true);
    }

    public void Dispose() => _client?.Dispose();
}
