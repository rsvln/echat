namespace EChat.Core.Models;

public record BatchKey
{
    public required HashSet<string> Recipients { get; init; }
    public string? GroupId { get; init; }
    public BatchTier Tier { get; init; }
    
    public virtual bool Equals(BatchKey? other)
    {
        if (other is null) return false;
        return Recipients.SetEquals(other.Recipients) 
               && GroupId == other.GroupId 
               && Tier == other.Tier;
    }
    
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var recipient in Recipients.OrderBy(r => r))
            hash.Add(recipient);
        hash.Add(GroupId);
        hash.Add(Tier);
        return hash.ToHashCode();
    }
}

public enum BatchTier
{
    Immediate,
    System,
    LowPriority
}