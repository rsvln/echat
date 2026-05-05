namespace EChat.Core.Models;

public class ChatGroup
{
    public required string GroupId { get; set; }
    public required string Name { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? AvatarHash { get; set; }
    
    // Navigation
    public ICollection<GroupMember> Members { get; set; } = new List<GroupMember>();
}

public class GroupMember
{
    public required string GroupId { get; set; }
    public required string MemberEmail { get; set; }
    public GroupRole Role { get; set; } = GroupRole.Member;
    public DateTimeOffset AddedAt { get; set; }
    public string? AddedBy { get; set; }
    public string? NameColor { get; set; }
    /// <summary>
    /// Display name as known by the member who created/added this entry.
    /// Populated from the group-create / group-member-add protocol message.
    /// Falls back to MemberEmail when null.
    /// </summary>
    public string? DisplayName { get; set; }

    // Navigation
    public ChatGroup? Group { get; set; }
}

public enum GroupRole
{
    Admin,
    Member
}