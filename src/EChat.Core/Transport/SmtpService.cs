using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using EChat.Core.Services;
using MailKit.Net.Smtp;
using MimeKit;

namespace EChat.Core.Transport;

public enum SmtpSendResult
{
    Sent,        // delivered
    RateLimited, // 4xx rate-limit (429/452/421) — retry later
    Permanent,   // 5xx permanent failure — don't retry
    TransientError // network / protocol error — safe to retry later
}

public class SmtpService : IDisposable
{
    private readonly FileLogger _fileLogger;
    private readonly SmtpClient _client;
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

    public SmtpService(FileLogger fileLogger)
    {
        _fileLogger = fileLogger;
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
            _fileLogger.Write("INFO", "SmtpService", $"Connected to SMTP server {server}");
        }
        catch (Exception ex)
        {
            _fileLogger.Write("ERROR", "SmtpService", $"Failed to connect to SMTP server: {ex.Message}");
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
        _fileLogger.Write("INFO", "SmtpService", $"Reconnected to SMTP server {_server}");
    }

    public async Task<SmtpSendResult> SendAsync(MimeMessage message)
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

    private async Task<SmtpSendResult> SendInternalAsync(MimeMessage message)
    {
        var backoff = TimeSpan.FromSeconds(2);

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                if (!_client.IsConnected)
                    await ReconnectAsync();

                await _client.SendAsync(message);
                _fileLogger.Write("INFO", "SmtpService", $"Message sent: {message.MessageId}");
                return SmtpSendResult.Sent;
            }
            catch (SmtpProtocolException ex)
            {
                // Connection dropped after DATA — message may already be accepted.
                // Retrying would duplicate it. Reconnect for future sends, report as transient.
                _fileLogger.Write("WARN", "SmtpService", $"SMTP protocol error — possible duplicate risk, not retrying: {ex.Message}");
                try { await ReconnectAsync(); } catch { }
                return SmtpSendResult.TransientError;
            }
            catch (SmtpCommandException ex)
            {
                var code = (int)ex.StatusCode;
                _fileLogger.Write("WARN", "SmtpService", $"SMTP command error {code} on attempt {attempt + 1}: {ex.Message}");

                // 5xx = permanent failure (bad address, policy rejection, etc.) — don't retry
                if (code >= 500)
                    return SmtpSendResult.Permanent;

                // 421 = service temporarily unavailable (server overloaded / shutting down)
                // 429 = too many requests (rate limit)
                // 451 = requested action aborted / rate-limit exceeded (mail.ru, Yandex, etc.)
                // 452 = insufficient system storage / sending limit reached
                if (code == 421 || code == 429 || code == 451 || code == 452)
                {
                    _fileLogger.Write("WARN", "SmtpService", $"SMTP rate-limit {code} — will retry automatically after cooldown");
                    return SmtpSendResult.RateLimited;
                }

                // Other 4xx — transient, retry with backoff
                if (attempt < MaxRetries - 1)
                {
                    await Task.Delay(backoff);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
                }
            }
            catch (Exception ex)
            {
                _fileLogger.Write("ERROR", "SmtpService", $"Unexpected error on attempt {attempt + 1}: {ex.Message}");
                if (attempt < MaxRetries - 1)
                {
                    await Task.Delay(backoff);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
                }
            }
        }

        return SmtpSendResult.TransientError;
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
