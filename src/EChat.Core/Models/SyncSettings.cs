namespace EChat.Core.Models;

public class SyncSettings
{
    public SyncProfile Profile { get; set; } = SyncProfile.Balanced;
    public bool UseImapIdle { get; set; } = true;
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ImmediateBatchWindow { get; set; } = TimeSpan.Zero;
    public TimeSpan SystemBatchWindow { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan LowPriorityBatchWindow { get; set; } = TimeSpan.FromSeconds(60);
    public bool AllowCellularSync { get; set; } = true;
    public bool SyncOnMeteredConnection { get; set; } = false;
    public TimeRange? QuietHours { get; set; }
    public SyncProfile QuietHoursProfile { get; set; } = SyncProfile.PowerSaver;
}

public enum SyncProfile
{
    Realtime,
    Balanced,
    PowerSaver,
    Manual,
    Custom
}

public record TimeRange(int StartHour, int EndHour);