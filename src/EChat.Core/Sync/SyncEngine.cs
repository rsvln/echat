using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EChat.Core.Sync;

public class SyncEngine
{
    private readonly FileLogger _fileLogger;
    private readonly IServiceScopeFactory _scopeFactory;
    private SyncSettings _settings;
    private DateTime _lastActivityTime = DateTime.UtcNow;

    private readonly List<DateTime> _wakeupTimes = new();
    private readonly object _wakeupLock = new();

    public event Func<SyncSettings, Task>? SettingsChanged;

    public SyncEngine(
        FileLogger fileLogger,
        IServiceScopeFactory scopeFactory)
    {
        _fileLogger = fileLogger;
        _scopeFactory = scopeFactory;
        _settings = new SyncSettings();
    }

    public async Task LoadSettingsAsync(string accountId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var prefix = $"acct_{accountId}_";
        var settings = await db.Settings
            .Where(s => s.Key.StartsWith(prefix))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        var newSettings = new SyncSettings();

        if (settings.TryGetValue($"{prefix}sync_profile", out var profileVal) &&
            Enum.TryParse<SyncProfile>(profileVal, out var profile))
            newSettings.Profile = profile;

        if (settings.TryGetValue($"{prefix}allow_cellular", out var cellVal) &&
            bool.TryParse(cellVal, out var allowCellular))
            newSettings.AllowCellularSync = allowCellular;

        if (settings.TryGetValue($"{prefix}sync_metered", out var meteredVal) &&
            bool.TryParse(meteredVal, out var syncMetered))
            newSettings.SyncOnMeteredConnection = syncMetered;

        if (settings.TryGetValue($"{prefix}quiet_start", out var qStart) &&
            int.TryParse(qStart, out var startHour) &&
            settings.TryGetValue($"{prefix}quiet_end", out var qEnd) &&
            int.TryParse(qEnd, out var endHour))
            newSettings.QuietHours = new TimeRange(startHour, endHour);

        if (settings.TryGetValue($"{prefix}quiet_profile", out var qProfileVal) &&
            Enum.TryParse<SyncProfile>(qProfileVal, out var qProfile))
            newSettings.QuietHoursProfile = qProfile;

        if (settings.TryGetValue($"{prefix}use_idle", out var idleVal) &&
            bool.TryParse(idleVal, out var useIdle))
            newSettings.UseImapIdle = useIdle;

        if (settings.TryGetValue($"{prefix}polling_interval", out var pollVal) &&
            int.TryParse(pollVal, out var pollMin) && pollMin > 0)
            newSettings.PollingInterval = TimeSpan.FromMinutes(pollMin);

        UpdateSettings(newSettings);
        _fileLogger.Write("INFO", "SyncEngine", $"Loaded sync settings for account {accountId}: profile={newSettings.Profile}, idle={newSettings.UseImapIdle}, poll={newSettings.PollingInterval.TotalMinutes}min");
    }

    public async Task SaveSettingsAsync(string accountId, SyncSettings settings)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var prefix = $"acct_{accountId}_";
        var keys = new Dictionary<string, string>
        {
            [$"{prefix}sync_profile"] = settings.Profile.ToString(),
            [$"{prefix}allow_cellular"] = settings.AllowCellularSync.ToString(),
            [$"{prefix}sync_metered"] = settings.SyncOnMeteredConnection.ToString(),
            [$"{prefix}use_idle"] = settings.UseImapIdle.ToString(),
            [$"{prefix}polling_interval"] = ((int)settings.PollingInterval.TotalMinutes).ToString(),
        };

        if (settings.QuietHours != null)
        {
            keys[$"{prefix}quiet_start"] = settings.QuietHours.StartHour.ToString();
            keys[$"{prefix}quiet_end"] = settings.QuietHours.EndHour.ToString();
        }

        keys[$"{prefix}quiet_profile"] = settings.QuietHoursProfile.ToString();

        foreach (var (key, value) in keys)
        {
            var existing = await db.Settings.FindAsync(key);
            if (existing == null)
            {
                db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
            }
            else
            {
                existing.Value = value;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync();
        UpdateSettings(settings);

        _fileLogger.Write("INFO", "SyncEngine", $"Saved sync settings for account {accountId}");
    }

    public void UpdateSettings(SyncSettings settings)
    {
        _settings = settings;
        SettingsChanged?.Invoke(settings);
    }

    public SyncSettings GetCurrentSettings() => _settings;

    public void RecordActivity()
    {
        _lastActivityTime = DateTime.UtcNow;
    }

    public void RecordWakeup()
    {
        var now = DateTime.UtcNow;
        lock (_wakeupLock)
        {
            _wakeupTimes.Add(now);
            // Trim entries older than 48h to bound memory usage
            var cutoff = now.AddHours(-48);
            _wakeupTimes.RemoveAll(t => t < cutoff);
        }
    }

    public int GetWakeupCount(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        lock (_wakeupLock)
            return _wakeupTimes.Count(t => t >= cutoff);
    }

    /// <summary>
    /// Applies platform-level overrides on top of whatever was loaded from DB.
    /// Called from Android-specific startup code to disable IDLE (unreliable when
    /// Android kills background TCP connections) and reduce the polling interval
    /// so messages arrive within ~1 minute instead of 5.
    /// </summary>
    public void ApplyMobileOverrides(bool useIdle = false, TimeSpan? pollingInterval = null)
    {
        _settings.UseImapIdle = useIdle;
        if (pollingInterval.HasValue)
            _settings.PollingInterval = pollingInterval.Value;
        _fileLogger.Write("INFO", "SyncEngine", $"Mobile overrides applied: idle={useIdle}, poll={_settings.PollingInterval.TotalMinutes}min");
    }

    public SyncStrategy GetCurrentStrategy(int batteryLevel, bool isMetered, bool isCellular)
    {
        return GetCurrentStrategy(batteryLevel, isMetered, isCellular, ChatPriority.Normal);
    }

    public SyncStrategy GetCurrentStrategy(int batteryLevel, bool isMetered, bool isCellular, ChatPriority chatPriority)
    {
        if (batteryLevel < 15)
        {
            _fileLogger.Write("INFO", "SyncEngine", "Low battery mode activated");
            return new SyncStrategy
            {
                UseIdle = false,
                PollingInterval = TimeSpan.FromMinutes(15),
                Reason = "Low battery"
            };
        }

        // High priority chats override quiet hours and metered restrictions
        if (chatPriority != ChatPriority.High)
        {
            if (IsQuietHours())
            {
                _fileLogger.Write("INFO", "SyncEngine", "Quiet hours active");
                return ApplyProfile(_settings.QuietHoursProfile, chatPriority);
            }

            if (isMetered && !_settings.SyncOnMeteredConnection)
            {
                _fileLogger.Write("INFO", "SyncEngine", "Metered connection, reducing sync");
                return new SyncStrategy
                {
                    UseIdle = false,
                    PollingInterval = TimeSpan.FromMinutes(30),
                    Reason = "Metered connection"
                };
            }
        }

        if (!isCellular || _settings.AllowCellularSync)
        {
            return ApplyProfile(_settings.Profile, chatPriority);
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

    private SyncStrategy ApplyProfile(SyncProfile profile, ChatPriority chatPriority = ChatPriority.Normal)
    {
        // UseImapIdle = false acts as a platform-level override (e.g. Android forces polling).
        // High-priority chats can still bypass this to get near-instant delivery.
        bool idleAllowed = _settings.UseImapIdle || chatPriority == ChatPriority.High;

        return profile switch
        {
            SyncProfile.Realtime => new SyncStrategy
            {
                UseIdle = idleAllowed,
                PollingInterval = TimeSpan.FromMinutes(1),
                Reason = "Realtime mode"
            },
            SyncProfile.Balanced => new SyncStrategy
            {
                UseIdle = idleAllowed && (!IsQuietHours() || chatPriority == ChatPriority.High),
                PollingInterval = chatPriority == ChatPriority.High
                    ? TimeSpan.FromMinutes(1)
                    : _settings.PollingInterval,
                Reason = "Balanced mode"
            },
            SyncProfile.PowerSaver => new SyncStrategy
            {
                UseIdle = idleAllowed && chatPriority == ChatPriority.High,
                PollingInterval = chatPriority switch
                {
                    ChatPriority.High => TimeSpan.FromMinutes(5),
                    ChatPriority.Normal => TimeSpan.FromMinutes(15),
                    ChatPriority.Low => TimeSpan.FromMinutes(30),
                    ChatPriority.Muted => TimeSpan.FromHours(6),
                    _ => TimeSpan.FromMinutes(15)
                },
                Reason = "Power saver mode"
            },
            SyncProfile.Manual => new SyncStrategy
            {
                UseIdle = chatPriority == ChatPriority.High,
                PollingInterval = chatPriority == ChatPriority.High
                    ? TimeSpan.FromMinutes(5)
                    : TimeSpan.FromDays(1),
                Reason = chatPriority == ChatPriority.High ? "High priority override" : "Manual sync only"
            },
            SyncProfile.Custom => new SyncStrategy
            {
                UseIdle = _settings.UseImapIdle || chatPriority == ChatPriority.High,
                PollingInterval = chatPriority == ChatPriority.High
                    ? TimeSpan.FromMinutes(1)
                    : _settings.PollingInterval,
                Reason = chatPriority == ChatPriority.High ? "High priority override" : "Custom settings"
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