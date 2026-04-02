namespace EChat.Core.Models;

public class Chat
{
    public required string ChatId { get; set; }
    public required ChatType Type { get; set; }
    public required string Name { get; set; }
    public string? AccountId { get; set; }
    public string? LastMessageId { get; set; }
    public int UnreadCount { get; set; }
    public bool Muted { get; set; }
    public bool Archived { get; set; }
    public ChatPriority Priority { get; set; } = ChatPriority.Normal;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    
    // Navigation
    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

public enum ChatType
{
    OneToOne,
    Group
}

public enum ChatPriority
{
    High,
    Normal,
    Low,
    Muted
}