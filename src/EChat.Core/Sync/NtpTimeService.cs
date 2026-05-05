using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EChat.Core.Services;

namespace EChat.Core.Sync;

public class NtpTimeService
{
    private readonly FileLogger _fileLogger;
    private long _offsetTicks = 0; // TimeSpan.Ticks, written via Interlocked.Exchange
    private readonly TimeSpan _resyncInterval = TimeSpan.FromHours(1);
    private readonly ConcurrentBag<string> _httpFallbackHosts = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public NtpTimeService(FileLogger fileLogger)
    {
        _fileLogger = fileLogger;
        NtpClock.SetService(this);
        // Fire-and-forget background sync: immediate first sync, then every hour.
        _ = Task.Run(RunBackgroundSyncAsync);
    }

    /// <summary>
    /// Called by transport services when an account connects.
    /// Registers the mail domain as an HTTP fallback time source.
    /// E.g. "imap.mail.ru" → adds "mail.ru" to the fallback pool.
    /// </summary>
    public void AddFallbackHost(string imapServer)
    {
        var domain = ExtractMailDomain(imapServer);
        if (!_httpFallbackHosts.Contains(domain))
            _httpFallbackHosts.Add(domain);
    }

    private static string ExtractMailDomain(string imapServer)
    {
        var parts = imapServer.Split('.');
        if (parts.Length > 2)
        {
            var prefix = parts[0].ToLowerInvariant().TrimEnd("0123456789".ToCharArray());
            if (prefix is "imap" or "smtp" or "pop" or "mail")
                return string.Join(".", parts.Skip(1));
        }
        return imapServer;
    }

    private async Task RunBackgroundSyncAsync()
    {
        await SyncAsync();
        using var timer = new PeriodicTimer(_resyncInterval);
        while (await timer.WaitForNextTickAsync())
            await SyncAsync();
    }

    private async Task SyncAsync()
    {
        // Try NTP first
        if (await TrySyncNtpAsync("pool.ntp.org")) return;

        // NTP failed — try HTTP HEAD against known mail domains
        foreach (var host in _httpFallbackHosts)
        {
            if (await TrySyncHttpAsync(host)) return;
        }

        _fileLogger.Write("DEBUG", "NtpTimeService", "All time sync sources failed, using system clock");
    }

    private async Task<bool> TrySyncNtpAsync(string server)
    {
        try
        {
            var ntpTime = await GetNetworkTimeAsync(server);
            ApplyOffset(ntpTime - DateTime.UtcNow, $"NTP ({server})");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TrySyncHttpAsync(string host)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var request = new HttpRequestMessage(HttpMethod.Head, $"https://{host}/");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var serverDate = response.Headers.Date;
            if (serverDate == null) return false;
            ApplyOffset(serverDate.Value.UtcDateTime - DateTime.UtcNow, $"HTTP ({host})");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyOffset(TimeSpan offset, string source)
    {
        Interlocked.Exchange(ref _offsetTicks, offset.Ticks);
        var sec = offset.TotalSeconds;
        if (Math.Abs(sec) > 60)
            _fileLogger.Write("WARN", "NtpTimeService", $"Clock skew {sec:F1}s via {source}");
        else
            _fileLogger.Write("INFO", "NtpTimeService", $"Time sync ok via {source}, offset: {sec:F3}s");
    }

    public async Task<DateTimeOffset> GetAccurateTimeAsync()
    {
        return await Task.FromResult(UtcNow);
    }

    public async Task SyncWithNtpAsync(string server = "pool.ntp.org")
    {
        await TrySyncNtpAsync(server);
    }
    
    private static async Task<DateTime> GetNetworkTimeAsync(string ntpServer)
    {
        var ntpData = new byte[48];
        ntpData[0] = 0x1B; // LI = 0, VN = 3, Mode = 3

        var addresses = await Dns.GetHostAddressesAsync(ntpServer);
        var ipEndPoint = new IPEndPoint(addresses[0], 123);

        // Use CancellationTokenSource for the real async timeout —
        // ReceiveTimeout only affects synchronous operations and is silently
        // ignored by await ReceiveAsync, which would otherwise block forever.
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        await socket.ConnectAsync(ipEndPoint, cts.Token);
        await socket.SendAsync(ntpData.AsMemory(), SocketFlags.None, cts.Token);
        int received = await socket.ReceiveAsync(ntpData.AsMemory(), SocketFlags.None, cts.Token);

        if (received < 48)
            throw new InvalidDataException($"NTP response too short: {received} bytes");

        var intPart  = BitConverter.ToUInt32(ntpData, 40);
        var fractPart = BitConverter.ToUInt32(ntpData, 44);

        if (BitConverter.IsLittleEndian)
        {
            intPart   = SwapEndianness(intPart);
            fractPart = SwapEndianness(fractPart);
        }

        if (intPart == 0)
            throw new InvalidDataException("NTP server returned a zero timestamp");

        var milliseconds = (intPart * 1000L) + ((fractPart * 1000L) / 0x100000000L);
        var networkDateTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMilliseconds(milliseconds);

        // Sanity check: reject NTP responses that differ from the system clock by more
        // than 10 years — this catches the "intPart=0 → year 1900" failure mode where
        // the socket receive returned garbage or the wrong packet.
        if (Math.Abs((networkDateTime - DateTime.UtcNow).TotalDays) > 3650)
            throw new InvalidDataException(
                $"NTP timestamp unreasonably far from system clock: {networkDateTime:u}");

        return networkDateTime;
    }
    
    private static uint SwapEndianness(uint x)
    {
        return ((x & 0x000000ff) << 24) +
               ((x & 0x0000ff00) << 8) +
               ((x & 0x00ff0000) >> 8) +
               ((x & 0xff000000) >> 24);
    }
    
    public TimeSpan GetCurrentOffset() => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
}