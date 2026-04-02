namespace EChat.Core.Protocol;

public class GroupState
{
    public required string GroupId { get; set; }
    public required string Name { get; set; }
    public int Version { get; set; }
    public required HashSet<string> Members { get; set; }
    public required HashSet<string> Admins { get; set; }
    public string? LastOperationActor { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    
    public bool Equals(GroupState? other)
    {
        if (other is null) return false;
        return GroupId == other.GroupId
               && Name == other.Name
               && Version == other.Version
               && Members.SetEquals(other.Members)
               && Admins.SetEquals(other.Admins);
    }
}