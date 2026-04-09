namespace EChat.Core.Protocol;

public class ChatHeaders
{
    public string Version { get; set; } = "2.0-batching";
    public required string MessageId { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
    public string? InReplyTo { get; set; }
    public int? Sequence { get; set; }
    
    // Batch
    public bool IsBatch { get; set; }
    public int? BatchCount { get; set; }
    public string? BatchTier { get; set; }
    public int? BatchItemIndex { get; set; }
    
    // Group
    public string? GroupId { get; set; }
    public int? GroupVersion { get; set; }
    public string? GroupName { get; set; }
    public List<string>? GroupMembers { get; set; }
    public List<string>? GroupAdmins { get; set; }
    public string? GroupOperation { get; set; }
    public string? GroupOperationActor { get; set; }
    public string? GroupOperationTarget { get; set; }
    
    // Crypto
    public string? Encryption { get; set; }
    public string? KeyFingerprint { get; set; }
    
    // System
    public string? Disposition { get; set; }
    public string? DispositionId { get; set; }
    public string? Reaction { get; set; }
    public string? ReactionTo { get; set; }
    public string? EditOf { get; set; }
    public int? EditVersion { get; set; }
    public string? DeleteOf { get; set; }
    public List<string>? ReadOf { get; set; }  // Chat-Read-Of: comma-separated MessageIds

    // Sync
    public string? SyncType { get; set; }
    public string? SyncDeviceId { get; set; }

    // System messages
    public string? SystemType { get; set; }

    // Message type override (e.g. "reaction")
    public string? MessageType { get; set; }
}