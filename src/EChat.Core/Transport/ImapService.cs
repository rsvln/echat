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
        _fileLogger.Write("INFO", "ImapService", $"IDLE timeout set to {timeout.TotalSeconds}s");
    }

    public async Task ConnectAsync(string server, int port, string email, string password, bool useSsl = true)
    {
        _server = server; _port = port; _email = email; _password = password; _useSsl = useSsl;
        try
        {
            await _client.ConnectAsync(server, port, useSsl);
            await _client.AuthenticateAsync(email, password);
            _fileLogger.Write("INFO", "ImapService", $"Connected to IMAP server {server}");
       }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "ImapService", $"Failed to connect to IMAP server: {ex.Message}");
            throw;
       }
   }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected && _client.IsAuthenticated) return;
        if (_server == null) throw new InvalidOperationException("IMAP never connected");
        _fileLogger.Write("INFO", "ImapService", $"IMAP reconnecting to {_server}");
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
        _fileLogger.Write("INFO", "ImapService", $"Created IMAP folder {folderName}");
        return folder;
    }

    /// <summary>
    /// IDLEs on <paramref name="inboxFolderName"/>. When new mail arrives, fetches headers,
    /// fires <see cref="MessageReceived"/> for εChat messages, and moves them to
    /// <paramref name="echatFolderName"/>. Non-εChat messages are left untouched.
    /// Also periodically re-syncs <paramref name="echatFolderName"/> so that messages
    /// moved there by another device (sent-to-self sync copies) are picked up.
    /// </summary>
    public async Task StartIdleAsync(string inboxFolderName, string echatFolderName,
        CancellationToken cancellationToken, HashSet<string>? knownEchatIds = null,
        TimeSpan? echatSyncInterval = null)
    {
        knownEchatIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                // SyncEchatFolderAsync opens its own folder; INBOX will be closed by MailKit SELECT.
                var since = lastEchatSync.AddMinutes(-1); // 1-min overlap to avoid gaps
                await SyncEchatFolderAsync(echatFolderName, knownEchatIds!, since, cancellationToken);
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
                _fileLogger.Write("WARN", "ImapService", $"Periodic eChat folder sync failed: {ex.Message}");
            }
        }

        try
        {
            inbox = await OpenInboxAsync();
            await ProcessChatMessagesAsync(inbox, echatFolderName, cancellationToken, knownEchatIds);
            // Ignore return value here — if it failed we'll retry in the IDLE loop anyway.
       }
        catch (OperationCanceledException) { return; }
        catch (System.IO.IOException ex)
        {
            _fileLogger.Write("WARN", "ImapService", $"IMAP IO error on initial open, will retry in loop: {ex.Message}");
       }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "ImapService", $"IMAP failed on initial open, will retry in loop: {ex.Message}");
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
                    _fileLogger.Write("WARN", "ImapService", $"IMAP reconnect IO error, retrying in 15s: {ex.Message}");
                    try { await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken); } catch { break; }
                    continue;
               }
                catch (Exception ex)
                {
                    _fileLogger.Write("ERROR", "ImapService", $"IMAP reconnect failed, retrying in 15s: {ex.Message}");
                    try { await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken); } catch { break; }
                    continue;
               }

                // Drain any messages already sitting in inbox before entering IDLE.
                // After reconnect the server won't send a COUNT change for mail that was
                // already there, so IDLE would miss them until the sync-interval timeout.
                bool ok = await ProcessChatMessagesAsync(inbox, echatFolderName, cancellationToken, knownEchatIds);
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
                _fileLogger.Write("WARN", "ImapService", $"IMAP IDLE IO error — will reconnect: {ex.Message}");
                inbox = null;
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); } catch { break; }
           }
            catch (Exception ex)
            {
                _fileLogger.Write("ERROR", "ImapService", $"IMAP IDLE error — will reconnect: {ex.Message}");
                inbox = null; // force reconnect on next iteration
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); } catch { break; }
           }
            finally
            {
                inbox?.CountChanged -= OnCountChanged;
           }

            if (inbox != null)
            {
                bool ok = await ProcessChatMessagesAsync(inbox, echatFolderName, cancellationToken, knownEchatIds);
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
    private async Task<bool> ProcessChatMessagesAsync(IMailFolder inbox, string echatFolderName, CancellationToken ct, HashSet<string>? knownMessageIds = null)
    {
        IList<UniqueId> uids;
        var subjectToken = $"[{echatFolderName}]";

        try
        {
            uids = await inbox.SearchAsync(BuildSubjectQuery(echatFolderName), ct);
            if (uids.Count == 0) return true;
       }
        catch (OperationCanceledException) { throw; }
        catch (System.IO.IOException ex)
        {
            _fileLogger.Write("WARN", "ImapService", $"IO error searching inbox for eChat messages: {ex.Message}");
            return false;
       }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "ImapService", $"Failed to search inbox for eChat messages: {ex.Message}");
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
                _fileLogger.Write("WARN", "ImapService", $"Failed to fetch inbox message {uid}: {ex.Message}. Breaking to reconnect.");
                fetchError = true;
                break;
            }

            // Client-side subject guard — IMAP SEARCH may return false positives on some
            // servers (e.g. Yandex doesn't always honour SubjectContains reliably).
            if (message.Subject?.Contains(subjectToken, StringComparison.OrdinalIgnoreCase) != true)
                continue;

            // Skip if already processed in this session
            var chatMsgId = message.Headers["Chat-Message-ID"];
            if (chatMsgId != null && knownMessageIds != null && knownMessageIds.Contains(chatMsgId))
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
                _fileLogger.Write("ERROR", "ImapService", $"Failed to move message {uid} to {echatFolderName}: {ex.Message}");
                echatFolder2 = inbox.Name; // stayed in inbox
                try { await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); } catch { }
            }

            if (MessageReceived != null)
            {
                try { await MessageReceived(message, echatUid, echatFolder2); }
                catch (Exception ex) { _fileLogger.Write("ERROR", "ImapService", $"Error in MessageReceived handler: {ex.Message}"); }
            }

            // Track processed message IDs so periodic eChat sync doesn't re-process them
            if (chatMsgId != null && knownMessageIds != null)
            {
                lock (knownMessageIdsLock)
                    knownMessageIds.Add(chatMsgId);
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
            catch (Exception ex) { _fileLogger.Write("WARN", "ImapService", $"Failed to mark echat messages as Seen: {ex.Message}"); }
        }

        return !fetchError;
    }

    private readonly object knownMessageIdsLock = new();

    /// <summary>
    /// On startup, sync the eChat IMAP folder against the DB.
    /// Fires <see cref="MessageReceived"/> for any message whose Chat-Message-ID
    /// is not already in <paramref name="knownMessageIds"/>.
    /// Never touches the Seen flag — deduplication is DB-driven, not flag-driven.
    /// </summary>
    public async Task SyncEchatFolderAsync(string echatFolderName, HashSet<string> knownMessageIds, DateTime since, CancellationToken ct)
    {
        try
        {
            var echatFolder = await GetOrCreateFolderAsync(echatFolderName);
            await echatFolder.OpenAsync(FolderAccess.ReadWrite, ct);

            var subjectToken = $"[{echatFolderName}]";
            var uids = await echatFolder.SearchAsync(
                BuildSubjectQuery(echatFolderName).And(SearchQuery.DeliveredAfter(since)), ct);
            if (uids.Count == 0)
            {
                try { await echatFolder.CloseAsync(false, ct); } catch { }
                return;
           }

            _fileLogger.Write("INFO", "ImapService", $"eChat sync: {uids.Count} candidates in last 30d");

            var syncSeenUids = new List<UniqueId>();

            foreach (var uid in uids)
            {
                ct.ThrowIfCancellationRequested();

                MimeMessage message;
                try { message = await echatFolder.GetMessageAsync(uid, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // After a timeout the echatFolder reference is stale — break so the caller
                    // can re-open it on the next sync cycle rather than spinning with errors.
                    _fileLogger.Write("WARN", "ImapService", $"Failed to fetch eChat message {uid}: {ex.Message}. Breaking to reconnect.");
                    break;
                }

                // Client-side subject guard — double-check after fetch in case the IMAP
                // server returned messages that don't actually contain the subject token.
                if (message.Subject?.Contains(subjectToken, StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                // Chat-Message-ID is a custom header — only available in the full message,
                // not in IMAP summary/envelope. Check it here against DB-derived knownIds.
                var chatMsgId = message.Headers["Chat-Message-ID"];
                if (chatMsgId != null && knownMessageIds.Contains(chatMsgId))
                {
                    syncSeenUids.Add(uid); // already processed — still mark Seen
                    continue;
                }

                if (MessageReceived != null)
                {
                    try { await MessageReceived(message, (long)uid.Id, echatFolderName); }
                    catch (Exception ex) { _fileLogger.Write("ERROR", "ImapService", $"Error processing eChat message {uid}: {ex.Message}"); continue; }
                }

                // Track in-session so polling doesn't re-process the same message twice
                if (chatMsgId != null) knownMessageIds.Add(chatMsgId);
                syncSeenUids.Add(uid);
           }

            // Bulk mark as Seen so mail clients don't show unread counters for echat messages
            if (syncSeenUids.Count > 0)
                try { await echatFolder.AddFlagsAsync(syncSeenUids, MessageFlags.Seen, true, ct); } catch { }

            try { await echatFolder.CloseAsync(false, ct); } catch { }
       }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "ImapService", $"Failed to sync eChat folder: {ex.Message}");
       }
   }

    public async Task SyncInboxAsync(string inboxFolderName, HashSet<string> knownMessageIds, CancellationToken ct)
    {
        try
        {
            var inbox = _client.Inbox;
            if (!inbox.IsOpen)
                await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

            var uids = await inbox.SearchAsync(SearchQuery.NotSeen, ct);
            if (uids.Count == 0) return;

            foreach (var uid in uids)
            {
                ct.ThrowIfCancellationRequested();

                var summary = (await inbox.FetchAsync(new[] { uid },
                    MessageSummaryItems.Headers | MessageSummaryItems.Flags, ct))
                    .FirstOrDefault();

                var msgId = summary?.Headers?["Chat-Message-ID"]
                            ?? summary?.Headers?["Message-ID"];

                if (msgId != null && knownMessageIds.Contains(msgId))
                {
                    try { await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); } catch { }
                    continue;
                }

                MimeMessage message;
                try { message = await inbox.GetMessageAsync(uid, ct); }
                catch (Exception ex) { _fileLogger.Write("ERROR", "ImapService", $"Failed to fetch inbox message {uid}: {ex.Message}"); continue; }

                if (MessageReceived != null)
                {
                    try { await MessageReceived(message, (long)uid.Id, inboxFolderName); }
                    catch (Exception ex) { _fileLogger.Write("ERROR", "ImapService", $"Error processing inbox message {uid}: {ex.Message}"); }
                }

                try { await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); } catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "ImapService", $"Failed to sync inbox: {ex.Message}");
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
            _fileLogger.Write("INFO", "ImapService", $"Deleted {uidList.Count} message(s) from IMAP folder {folderName}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _fileLogger.Write("WARN", "ImapService", $"Failed to delete messages from IMAP folder {folderName}: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_client.IsConnected)
            await _client.DisconnectAsync(true);
    }

    public void Dispose() => _client?.Dispose();
}
