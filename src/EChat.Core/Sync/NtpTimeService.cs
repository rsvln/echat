using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace EChat.Core.Sync;

public class NtpTimeService
{
    private readonly ILogger<NtpTimeService> _logger;
    private TimeSpan _ntpOffset = TimeSpan.Zero;
    private DateTime _lastNtpSync = DateTime.MinValue;
    private readonly TimeSpan _resyncInterval = TimeSpan.FromHours(1);
    
    public NtpTimeService(ILogger<NtpTimeService> logger)
    {
        _logger = logger;
    }
    
    public async Task<DateTimeOffset> GetAccurateTimeAsync()
    {
        if (DateTime.UtcNow - _lastNtpSync > _resyncInterval)
        {
            await SyncWithNtpAsync();
        }
        
        return DateTimeOffset.UtcNow + _ntpOffset;
    }
    
    public async Task SyncWithNtpAsync(string server = "pool.ntp.org")
    {
        try
        {
            var ntpTime = await GetNetworkTimeAsync(server);
            _ntpOffset = ntpTime - DateTime.UtcNow;
            _lastNtpSync = DateTime.UtcNow;
            
            if (Math.Abs(_ntpOffset.TotalSeconds) > 60)
            {
                _logger.LogWarning("Significant clock skew detected: {Offset} seconds", _ntpOffset.TotalSeconds);
            }
            else
            {
                _logger.LogInformation("NTP sync successful, offset: {Offset} seconds", _ntpOffset.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NTP sync failed, using system time");
        }
    }
    
    private async Task<DateTime> GetNetworkTimeAsync(string ntpServer)
    {
        var ntpData = new byte[48];
        ntpData[0] = 0x1B; // LI = 0, VN = 3, Mode = 3
        
        var addresses = await Dns.GetHostAddressesAsync(ntpServer);
        var ipEndPoint = new IPEndPoint(addresses[0], 123);
        
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = 5000;
        
        await socket.ConnectAsync(ipEndPoint);
        await socket.SendAsync(ntpData, SocketFlags.None);
        await socket.ReceiveAsync(ntpData, SocketFlags.None);
        
        var intPart = BitConverter.ToUInt32(ntpData, 40);
        var fractPart = BitConverter.ToUInt32(ntpData, 44);
        
        if (BitConverter.IsLittleEndian)
        {
            intPart = SwapEndianness(intPart);
            fractPart = SwapEndianness(fractPart);
        }
        
        var milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);
        var networkDateTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMilliseconds((long)milliseconds);
        
        return networkDateTime;
    }
    
    private static uint SwapEndianness(uint x)
    {
        return ((x & 0x000000ff) << 24) +
               ((x & 0x0000ff00) << 8) +
               ((x & 0x00ff0000) >> 8) +
               ((x & 0xff000000) >> 24);
    }
    
    public TimeSpan GetCurrentOffset() => _ntpOffset;
}