namespace EChat.Core.Protocol;

public class SyncPayload
{
    public required string SyncType { get; set; }
    public required string DeviceId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required Dictionary<string, object> Data { get; set; }
}

public class ReadStateSyncData
{
    public required string ChatId { get; set; }
    public required string LastReadMessageId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public class DraftSyncData
{
    public required string ChatId { get; set; }
    public required string DraftContent { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}