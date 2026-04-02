namespace EChat.Core.Sync;

public class SyncStrategy
{
    public bool UseIdle { get; set; }
    public TimeSpan PollingInterval { get; set; }
    public string Reason { get; set; } = string.Empty;
}