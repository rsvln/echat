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
        _imapService = new ImapService(imapLogger);
        _imapService.MessageReceived += OnMessageReceivedAsync;
    }

    public void Start(HashSet<string> knownMessageIds, DateTimeOffset since, TimeSpan? syncInterval = null)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _imapService.ConnectAsync(
                    _account.ImapServer, _account.ImapPort,
                    _account.Email, _account.Password, _account.ImapUseSsl);
                await _imapService.SyncEchatFolderAsync(EchatFolder, knownMessageIds, since.UtcDateTime, ct);
                await _imapService.StartIdleAsync(InboxFolder, EchatFolder, ct, knownMessageIds, syncInterval);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _fileLogger.Write("ERROR", "AccountImapWorker", $"IMAP worker failed for {_account.Email}: {ex.Message}"); }
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
                            _fileLogger.Write("DEBUG", "AccountImapWorker", $"Group key found but decryption failed for {msg.Headers.MessageId}, trying personal key");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _fileLogger.Write("WARN", "AccountImapWorker", $"Failed to look up group key for {msg.Headers.GroupId}: {ex.Message}");
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
                    _fileLogger.Write("WARN", "AccountImapWorker", $"Failed to decrypt message {msg.Headers.MessageId}: {ex.Message}");
                }
            }
        }

        foreach (var msg in messages)
            _fileLogger.Write("DEBUG", "AccountImapWorker",
                $"Parsed msg {msg.Headers.MessageId}: encrypted={msg.IsEncrypted}, attachments={msg.Attachments?.Count ?? 0}, contentLen={msg.Content?.Length ?? 0}");

        var newMessages = messages.Where(m => !_deduplicator.IsDuplicate(_account.AccountId, m)).ToList();
        _fileLogger.Write("INFO", "AccountImapWorker", $"Dedup for {_account.Email}: {messages.Count} total, {newMessages.Count} new, {messages.Count - newMessages.Count} duplicates");
        if (newMessages.Any() && MessagesReceived != null)
            await MessagesReceived(_account.AccountId, newMessages);
    }
}
