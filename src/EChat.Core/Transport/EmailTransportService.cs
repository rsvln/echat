using EChat.Core.Crypto;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    public bool IsConnected { get; private set; }

    private CancellationTokenSource? _idleCts;
    private Task? _idleTask;
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
        PgpService pgpService)
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

        _batchQueue = new BatchQueue(
            SendBatchedAsync,
            SendSingleAsync,
            TimeSpan.FromSeconds(10)
        );

        _imapService.MessageReceived += OnMessageReceived;
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

        // Start IDLE for the new account.
        // First, collect message IDs already in DB so the eChat sync can skip them.
        _idleCts = new CancellationTokenSource();
        var cts = _idleCts;
        _idleTask = StartIdleWithSyncAsync(account, cts.Token);
        _ = _idleTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogError(t.Exception?.GetBaseException(), "IMAP IDLE stopped for {Email}", account.Email);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task StartIdleWithSyncAsync(Account account, CancellationToken ct)
    {
        // Load existing message IDs from DB so eChat sync skips them
        HashSet<string> knownIds;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            // DateTimeOffset is not translatable by SQLite EF provider — load all IDs, bounded
            var ids = await db.Messages
                .Where(m => m.MessageId != null)
                .Select(m => m.MessageId!)
                .Take(5000)
                .ToListAsync();
            knownIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load known message IDs; eChat sync may reprocess messages");
            knownIds = new HashSet<string>();
        }

        // Sync eChat folder before starting IDLE (opening eChat deselects INBOX,
        // so this must happen first)
        await _imapService.SyncEchatFolderAsync(EchatFolder, knownIds, ct);

        // Now run the IDLE loop on INBOX
        await _imapService.StartIdleAsync(InboxFolder, EchatFolder, ct);
    }

    public async Task SendMessageAsync(OutgoingMessage message)
    {
        // Look up recipient's public key so we can encrypt
        if (message.RecipientPublicKey == null && message.Recipients.Count == 1)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                var contact = await db.Contacts.FindAsync(message.Recipients[0]);
                if (contact?.PublicKey != null)
                    message.RecipientPublicKey = contact.PublicKey;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to look up recipient public key");
            }
        }

        await _batchQueue.Enqueue(message);
    }

    private async Task SendSingleAsync(OutgoingMessage message)
    {
        // Use async build (may encrypt)
        var email = await _builder.BuildSingleAsync(message);
        await _smtpService.SendAsync(email);
    }

    private async Task SendBatchedAsync(List<OutgoingMessage> messages)
    {
        var tier = messages.First().Tier;
        var email = _builder.BuildBatch(messages, tier);

        var success = await _smtpService.SendAsync(email);
        if (!success)
        {
            _logger.LogWarning("Batch send failed, retrying individually");
            foreach (var msg in messages)
                await SendSingleAsync(msg);
        }
    }

    private async Task OnMessageReceived(MimeKit.MimeMessage email)
    {
        try
        {
            var autocrypt = email.Headers["Autocrypt"];
            if (!string.IsNullOrEmpty(autocrypt))
                await StoreAutocryptKeyAsync(email.From.Mailboxes.FirstOrDefault()?.Address, autocrypt);

            var messages = _parser.Parse(email);

            // Decrypt PGP-inline encrypted messages
            if (_accountConfig.PrivateKey != null && _accountConfig.KeyPassword != null)
            {
                foreach (var msg in messages)
                {
                    if (msg.Headers.Encryption == "pgp-inline" && !string.IsNullOrEmpty(msg.Content))
                    {
                        try
                        {
                            msg.Content = await _pgpService.DecryptAsync(
                                msg.Content, _accountConfig.PrivateKey, _accountConfig.KeyPassword);
                            msg.IsEncrypted = false; // mark as successfully decrypted
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to decrypt message {Id}", msg.Headers.MessageId);
                        }
                    }
                }
            }

            var newMessages = messages.Where(m => !_deduplicator.IsDuplicate(m)).ToList();
            foreach (var m in newMessages)
            if (newMessages.Any() && MessagesReceived != null)
            {
                await MessagesReceived(newMessages);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing received message");
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

            await db.SaveChangesAsync();
            _logger.LogInformation("Stored public key for {Email}", senderEmail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store Autocrypt key for {Email}", senderEmail);
        }
    }
}
