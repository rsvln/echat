using System.Net;
using System.Net.Sockets;
using EChat.Core.Services;

namespace EChat.Core.Sync;

public class NtpTimeService
{
    private readonly FileLogger _fileLogger;
    private TimeSpan _ntpOffset = TimeSpan.Zero;
    private DateTime _lastNtpSync = DateTime.MinValue;
    private readonly TimeSpan _resyncInterval = TimeSpan.FromHours(1);
    
    public NtpTimeService(FileLogger fileLogger)
    {
        _fileLogger = fileLogger;
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
                _fileLogger.Write("WARN", "NtpTimeService", $"Significant clock skew detected: {_ntpOffset.TotalSeconds} seconds");
            }
            else
            {
                _fileLogger.Write("INFO", "NtpTimeService", $"NTP sync successful, offset: {_ntpOffset.TotalSeconds} seconds");
            }
        }
        catch (Exception ex)
        {
            _fileLogger.Write("DEBUG", "NtpTimeService", $"NTP sync failed, using system time: {ex.Message}");
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