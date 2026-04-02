using EChat.Core.Models;
using Microsoft.Extensions.Logging;

namespace EChat.Core.Sync;

public class SyncEngine
{
    private readonly ILogger<SyncEngine> _logger;
    private SyncSettings _settings;
    private DateTime _lastActivityTime = DateTime.MinValue;
    
    public SyncEngine(ILogger<SyncEngine> logger, SyncSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }
    
    public void UpdateSettings(SyncSettings settings)
    {
        _settings = settings;
    }
    
    public void RecordActivity()
    {
        _lastActivityTime = DateTime.UtcNow;
    }
    
    public SyncStrategy GetCurrentStrategy(int batteryLevel, bool isMetered, bool isCellular)
    {
        if (batteryLevel < 15)
        {
            _logger.LogInformation("Low battery mode activated");
            return new SyncStrategy
            {
                UseIdle = false,
                PollingInterval = TimeSpan.FromMinutes(15),
                Reason = "Low battery"
            };
        }
        
        if (IsQuietHours())
        {
            _logger.LogInformation("Quiet hours active");
            return ApplyProfile(_settings.QuietHoursProfile);
        }
        
        if (isMetered && !_settings.SyncOnMeteredConnection)
        {
            _logger.LogInformation("Metered connection, reducing sync");
            return new SyncStrategy
            {
                UseIdle = false,
                PollingInterval = TimeSpan.FromMinutes(30),
                Reason = "Metered connection"
            };
        }
        
        if (!isCellular || _settings.AllowCellularSync)
        {
            return ApplyProfile(_settings.Profile);
        }
        
        return new SyncStrategy
        {
            UseIdle = false,
            PollingInterval = TimeSpan.FromMinutes(15),
            Reason = "Cellular sync disabled"
        };
    }
    
    public TimeSpan GetAdaptiveBatchWindow(BatchTier tier)
    {
        var activityAge = DateTime.UtcNow - _lastActivityTime;
        
        var baseWindow = tier switch
        {
            BatchTier.Immediate => _settings.ImmediateBatchWindow,
            BatchTier.System => _settings.SystemBatchWindow,
            BatchTier.LowPriority => _settings.LowPriorityBatchWindow,
            _ => TimeSpan.FromSeconds(10)
        };
        
        if (activityAge < TimeSpan.FromSeconds(30))
        {
            return TimeSpan.FromTicks(baseWindow.Ticks / 2);
        }
        
        if (activityAge > TimeSpan.FromMinutes(5))
        {
            return TimeSpan.FromTicks(baseWindow.Ticks * 2);
        }
        
        return baseWindow;
    }
    
    private bool IsQuietHours()
    {
        if (_settings.QuietHours == null) return false;
        
        var now = DateTime.Now.Hour;
        var start = _settings.QuietHours.StartHour;
        var end = _settings.QuietHours.EndHour;
        
        if (start < end)
        {
            return now >= start && now < end;
        }
        else
        {
            return now >= start || now < end;
        }
    }
    
    private SyncStrategy ApplyProfile(SyncProfile profile)
    {
        return profile switch
        {
            SyncProfile.Realtime => new SyncStrategy
            {
                UseIdle = true,
                PollingInterval = TimeSpan.FromMinutes(1),
                Reason = "Realtime mode"
            },
            SyncProfile.Balanced => new SyncStrategy
            {
                UseIdle = !IsQuietHours(),
                PollingInterval = TimeSpan.FromMinutes(5),
                Reason = "Balanced mode"
            },
            SyncProfile.PowerSaver => new SyncStrategy
            {
                UseIdle = false,
                PollingInterval = TimeSpan.FromMinutes(15),
                Reason = "Power saver mode"
            },
            SyncProfile.Manual => new SyncStrategy
            {
                UseIdle = false,
                PollingInterval = TimeSpan.FromDays(1),
                Reason = "Manual sync only"
            },
            SyncProfile.Custom => new SyncStrategy
            {
                UseIdle = _settings.UseImapIdle,
                PollingInterval = _settings.PollingInterval,
                Reason = "Custom settings"
            },
            _ => new SyncStrategy
            {
                UseIdle = true,
                PollingInterval = TimeSpan.FromMinutes(5),
                Reason = "Default"
            }
        };
    }
}