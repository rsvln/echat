namespace EChat.Core.Models;

public class Chat
{
    public required string ChatId { get; set; }
    public required ChatType Type { get; set; }
    public required string Name { get; set; }
    public string? AccountId { get; set; }
    public string? ContactEmail { get; set; }
    public string? GroupId { get; set; }
    public string? LastMessageId { get; set; }
    public int UnreadCount { get; set; }
    public bool Muted { get; set; }
    public bool Archived { get; set; }
    public bool Deleted { get; set; }
    /// <summary>
    /// The shared ChatGroup.Version at the moment this chat was tombstoned (Deleted=true).
    /// Used to allow group-create resurrection only when the incoming version is strictly
    /// greater than the version that triggered the deletion.  Null = never tombstoned.
    /// </summary>
    public int? TombstoneVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }

    /// <summary>
    /// One-time invite token to include in the next outgoing message to this chat.
    /// Set when Bob starts a chat using Alice's invite code; cleared after the first send.
    /// Null once the key exchange is complete.
    /// </summary>
    public string? PendingOutgoingInviteToken { get; set; }

    // Navigation
    public ChatGroup? Group { get; set; }
    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

public enum ChatType
{
    OneToOne,
    Group
}

