using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Smtp;
using MimeKit;
using Microsoft.Extensions.Logging;

namespace EChat.Core.Transport;

public class SmtpService : IDisposable
{
    private readonly ILogger<SmtpService> _logger;
    private readonly SmtpClient _client;
    private TimeSpan _currentBackoff = TimeSpan.FromSeconds(1);
    private const int MaxRetries = 3;

    // MailKit SmtpClient is not thread-safe — serialize all sends
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

    private string? _server;
    private int _port;
    private string? _email;
    private string? _password;
    private bool _useSsl;

    // Accept certificates where only revocation status is unknown (common on Android/mobile).
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
        return (sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;
    }

    public SmtpService(ILogger<SmtpService> logger)
    {
        _logger = logger;
        _client = new SmtpClient
        {
            ServerCertificateValidationCallback = AllowRevocationUnknown
        };
    }

    public async Task ConnectAsync(string server, int port, string email, string password, bool useSsl = true)
    {
        _server = server;
        _port = port;
        _email = email;
        _password = password;
        _useSsl = useSsl;

        try
        {
            await _client.ConnectAsync(server, port, useSsl);
            await _client.AuthenticateAsync(email, password);
            _logger.LogInformation("Connected to SMTP server {Server}", server);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to SMTP server");
            throw;
        }
    }

    private async Task ReconnectAsync()
    {
        if (_server == null) throw new InvalidOperationException("SMTP never connected");

        if (_client.IsConnected)
        {
            try { await _client.DisconnectAsync(false); } catch { }
        }

        await _client.ConnectAsync(_server, _port, _useSsl);
        await _client.AuthenticateAsync(_email, _password);
        _logger.LogInformation("Reconnected to SMTP server {Server}", _server);
    }

    public async Task<bool> SendAsync(MimeMessage message)
    {
        await _sendLock.WaitAsync();
        try
        {
            return await SendInternalAsync(message);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<bool> SendInternalAsync(MimeMessage message)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                if (!_client.IsConnected)
                    await ReconnectAsync();

                await _client.SendAsync(message);
                _currentBackoff = TimeSpan.FromSeconds(1);
                _logger.LogInformation("Message sent: {MessageId}", message.MessageId);
                return true;
            }
            catch (SmtpProtocolException ex)
            {
                // SmtpProtocolException typically means the connection dropped AFTER the DATA
                // command was sent — the message may already have been accepted by the server.
                // Retrying would send a duplicate. Reconnect for future sends but don't retry.
                _logger.LogWarning(ex, "SMTP protocol error on attempt {Attempt} — not retrying to avoid duplicate send", attempt + 1);
                try { await ReconnectAsync(); } catch { }
                return false;
            }
            catch (SmtpCommandException ex)
            {
                _logger.LogWarning(ex, "SMTP command error {StatusCode} on attempt {Attempt}: {Message}",
                    (int)ex.StatusCode, attempt + 1, ex.Message);
                if ((int)ex.StatusCode >= 500)
                    return false;
                if (attempt < MaxRetries - 1)
                {
                    await Task.Delay(_currentBackoff);
                    _currentBackoff = TimeSpan.FromSeconds(Math.Min(_currentBackoff.TotalSeconds * 2, 60));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending message on attempt {Attempt}", attempt + 1);
                if (attempt < MaxRetries - 1)
                {
                    await Task.Delay(_currentBackoff);
                    _currentBackoff = TimeSpan.FromSeconds(Math.Min(_currentBackoff.TotalSeconds * 2, 60));
                }
            }
        }

        return false;
    }

    public async Task DisconnectAsync()
    {
        if (_client.IsConnected)
            await _client.DisconnectAsync(true);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _sendLock?.Dispose();
    }
}
