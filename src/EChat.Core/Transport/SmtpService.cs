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

    private string? _server;
    private int _port;
    private string? _email;
    private string? _password;
    private bool _useSsl;

    public SmtpService(ILogger<SmtpService> logger)
    {
        _logger = logger;
        _client = new SmtpClient();
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
                _logger.LogWarning(ex, "SMTP protocol error on attempt {Attempt}, reconnecting", attempt + 1);
                try { await ReconnectAsync(); }
                catch (Exception rex)
                {
                    _logger.LogWarning(rex, "SMTP reconnect failed, giving up");
                    return false;
                }
            }
            catch (SmtpCommandException ex)
            {
                _logger.LogWarning(ex, "SMTP command error {StatusCode} on attempt {Attempt}: {Message}",
                    (int)ex.StatusCode, attempt + 1, ex.Message);
                // 5xx = permanent rejection — no point retrying
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

    public void Dispose() => _client?.Dispose();
}
