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

    private string? _server;
    private int _port;
    private string? _email;
    private string? _password;
    private bool _useSsl;

    public event Func<MimeMessage, Task>? MessageReceived;

    public ImapService(ILogger<ImapService> logger)
    {
        _logger = logger;
        _client = new ImapClient();
   }

    public async Task ConnectAsync(string server, int port, string email, string password, bool useSsl = true)
    {
        _server = server; _port = port; _email = email; _password = password; _useSsl = useSsl;
        try
        {
            await _client.ConnectAsync(server, port, useSsl);
            await _client.AuthenticateAsync(email, password);
            _logger.LogInformation("Connected to IMAP server {Server}", server);
       }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to IMAP server");
            throw;
       }
   }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected && _client.IsAuthenticated) return;
        if (_server == null) throw new InvalidOperationException("IMAP never connected");
        _logger.LogInformation("IMAP reconnecting to {Server}", _server);
        try { await _client.DisconnectAsync(false); } catch { }
        await _client.ConnectAsync(_server, _port, _useSsl, ct);
        await _client.AuthenticateAsync(_email, _password, ct);
   }

    public async Task<IMailFolder> GetOrCreateFolderAsync(string folderName)
    {
        var personalNamespace = _client.PersonalNamespaces[0];

        try
        {
            return await _client.GetFolderAsync(personalNamespace.Path + folderName);
       }
        catch
        {
            var parentFolder = await _client.GetFolderAsync(personalNamespace.Path);
            var folder = await parentFolder.CreateAsync(folderName, true);
            _logger.LogInformation("Created IMAP folder {Folder}", folderName);
            return folder;
       }
   }

    /// <summary>
    /// IDLEs on <paramref name="inboxFolderName"/>. When new mail arrives, fetches headers,
    /// fires <see cref="MessageReceived"/> for εChat messages, and moves them to
    /// <paramref name="echatFolderName"/>. Non-εChat messages are left untouched.
    /// </summary>
    public async Task StartIdleAsync(string inboxFolderName, string echatFolderName, CancellationToken cancellationToken)
    {
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

        try
        {
            inbox = await OpenInboxAsync();
            await ProcessChatMessagesAsync(inbox, echatFolderName, cancellationToken);
       }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IMAP failed on initial open, will retry in loop");
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IMAP reconnect failed, retrying in 15s");
                    try { await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken); } catch { break; }
                    continue;
               }
           }

            using var idleDone = new CancellationTokenSource();

            void OnCountChanged(object? s, EventArgs e)
            {
                try { idleDone.Cancel(); } catch (ObjectDisposedException) { }
           }

            inbox.CountChanged += OnCountChanged;
            try
            {
                if (_client.Capabilities.HasFlag(ImapCapabilities.Idle))
                    await _client.IdleAsync(idleDone.Token, cancellationToken);
                else
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    await _client.NoOpAsync(cancellationToken);
               }
           }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // idleDone cancelled — new mail arrived, fall through to process
           }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IMAP IDLE error — will reconnect");
                inbox = null; // force reconnect on next iteration
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); } catch { break; }
           }
            finally
            {
                inbox?.CountChanged -= OnCountChanged;
           }

            if (inbox != null)
            {
                await ProcessChatMessagesAsync(inbox, echatFolderName, cancellationToken);
           }
       }
   }

    // Use only ASCII subject prefixes — non-ASCII (e.g. Greek ε) in IMAP SEARCH
    // is not universally supported and silently returns empty results on many servers.
    private static readonly SearchQuery ChatSubjectQuery =
        SearchQuery.SubjectContains("[eChat]")
            .Or(SearchQuery.SubjectContains("[eChat Batch]"));

    private async Task ProcessChatMessagesAsync(IMailFolder inbox, string echatFolderName, CancellationToken ct)
    {
        IList<UniqueId> uids;

        try
        {
            uids = await inbox.SearchAsync(ChatSubjectQuery, ct);
            if (uids.Count == 0) return;
       }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search inbox for eChat messages");
            return;
       }

        IMailFolder? echatFolder = null;

        foreach (var uid in uids)
        {
            MimeMessage message;
            try
            {
                message = await inbox.GetMessageAsync(uid, ct);
           }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch message {Uid}", uid);
                continue;
           }

            if (MessageReceived != null)
            {
                try { await MessageReceived(message); }
                catch (Exception ex) { _logger.LogError(ex, "Error in MessageReceived handler"); }
           }

            // Move to eChat folder so it doesn't clutter the regular inbox
            try
            {
                echatFolder ??= await GetOrCreateFolderAsync(echatFolderName);
                await inbox.MoveToAsync(uid, echatFolder, ct);
           }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to move message {Uid} to {Folder}", uid, echatFolderName);
                try { await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); } catch { }
           }
       }
   }

    /// <summary>
    /// On startup, sync the eChat IMAP folder against the DB.
    /// Fires <see cref="MessageReceived"/> for any message whose Chat-Message-ID
    /// is not already in <paramref name="knownMessageIds"/>.
    /// Never touches the Seen flag — deduplication is DB-driven, not flag-driven.
    /// </summary>
    public async Task SyncEchatFolderAsync(string echatFolderName, HashSet<string> knownMessageIds, CancellationToken ct)
    {
        try
        {
            var echatFolder = await GetOrCreateFolderAsync(echatFolderName);
            await echatFolder.OpenAsync(FolderAccess.ReadWrite, ct);

            var since = DateTimeOffset.UtcNow.AddDays(-30);
            var uids = await echatFolder.SearchAsync(
                ChatSubjectQuery.And(SearchQuery.DeliveredAfter(since.UtcDateTime)), ct);
            if (uids.Count == 0)
            {
                try { await echatFolder.CloseAsync(false, ct); } catch { }
                return;
           }

            _logger.LogInformation("eChat sync: {Count} candidates in last 30d", uids.Count);

            foreach (var uid in uids)
            {
                ct.ThrowIfCancellationRequested();

                var summary = (await echatFolder.FetchAsync(new[] { uid },
                    MessageSummaryItems.Headers | MessageSummaryItems.Flags, ct))
                    .FirstOrDefault();

                var msgId = summary?.Headers?["Chat-Message-ID"]
                            ?? summary?.Headers?["Message-ID"];

                if (msgId != null && knownMessageIds.Contains(msgId))
                {
                    if (summary?.Flags?.HasFlag(MessageFlags.Seen) == false)
                        try { await echatFolder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); } catch { }
                    continue;
               }

                MimeMessage message;
                try { message = await echatFolder.GetMessageAsync(uid, ct); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to fetch eChat message {Uid}", uid); continue; }

                if (MessageReceived != null)
                {
                    try { await MessageReceived(message); }
                    catch (Exception ex) { _logger.LogError(ex, "Error processing eChat message {Uid}", uid); continue; }
               }

                try { await echatFolder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); } catch { }
           }

            try { await echatFolder.CloseAsync(false, ct); } catch { }
       }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync eChat folder");
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

    public async Task DisconnectAsync()
    {
        if (_client.IsConnected)
            await _client.DisconnectAsync(true);
   }

    public void Dispose() => _client?.Dispose();
}
