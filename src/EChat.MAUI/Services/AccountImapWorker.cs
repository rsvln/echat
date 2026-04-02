using EChat.Core.Crypto;
using EChat.Core.Models;
using EChat.Core.Protocol;
using EChat.Core.Transport;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EChat.Maui.Services;

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
    private readonly ILogger<AccountImapWorker> _logger;
    private readonly Account _account;

    private CancellationTokenSource? _cts;

    private const string InboxFolder = "INBOX";
    private const string EchatFolder = "eChat";

    public event Func<string, List<ParsedMessage>, Task>? MessagesReceived;

    public AccountImapWorker(
        Account account,
        ILogger<AccountImapWorker> logger,
        ILogger<ImapService> imapLogger,
        ChatMessageParser parser,
        PgpService pgpService,
        MessageDeduplicator deduplicator)
    {
        _account = account;
        _logger = logger;
        _parser = parser;
        _pgpService = pgpService;
        _deduplicator = deduplicator;
        _imapService = new ImapService(imapLogger);
        _imapService.MessageReceived += OnMessageReceivedAsync;
    }

    public void Start(HashSet<string> knownMessageIds)
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
                await _imapService.SyncEchatFolderAsync(EchatFolder, knownMessageIds, ct);
                await _imapService.StartIdleAsync(InboxFolder, EchatFolder, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "IMAP worker failed for {Email}", _account.Email); }
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task OnMessageReceivedAsync(MimeMessage email)
    {
        var messages = _parser.Parse(email);

        if (_account.PrivateKey != null)
        {
            foreach (var msg in messages)
            {
                if (msg.Headers.Encryption == "pgp-inline" && !string.IsNullOrEmpty(msg.Content))
                {
                    try
                    {
                        msg.Content = await _pgpService.DecryptAsync(
                            msg.Content, _account.PrivateKey, _account.Password);
                        msg.IsEncrypted = false;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to decrypt message {Id}", msg.Headers.MessageId);
                    }
                }
            }
        }

        var newMessages = messages.Where(m => !_deduplicator.IsDuplicate(m)).ToList();
        if (newMessages.Any() && MessagesReceived != null)
            await MessagesReceived(_account.AccountId, newMessages);
    }
}
