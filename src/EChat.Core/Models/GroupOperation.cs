namespace EChat.Core.Models;

public class GroupOperation
{
    public int Id { get; set; }
    public required string GroupId { get; set; }
    public int Version { get; set; }
    public required GroupOperationType Operation { get; set; }
    public required string Actor { get; set; }
    public string? Target { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public bool Applied { get; set; }
    
    // Navigation
    public ChatGroup? Group { get; set; }
}

public enum GroupOperationType
{
    MemberAdd,
    MemberRemove,
    PromoteAdmin,
    DemoteAdmin,
    NameChange,
    AvatarChange
}