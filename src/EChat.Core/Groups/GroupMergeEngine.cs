using EChat.Core.Protocol;
using EChat.Core.Services;

namespace EChat.Core.Groups;

public class GroupMergeEngine
{
    private readonly FileLogger _fileLogger;
    
    public GroupMergeEngine(FileLogger fileLogger)
    {
        _fileLogger = fileLogger;
    }
    
    public GroupState MergeConflict(GroupState local, GroupState remote)
    {
        _fileLogger.Write("WARN", "GroupMergeEngine", $"Merging group conflict for {local.GroupId}: local v{local.Version} vs remote v{remote.Version}");
        
        var merged = new GroupState
        {
            GroupId = local.GroupId,
            Version = Math.Max(local.Version, remote.Version) + 1,
            Name = ChooseNameByTimestamp(local, remote),
            Members = MergeMembers(local, remote),
            Admins = MergeAdmins(local, remote),
            Timestamp = DateTimeOffset.UtcNow
        };
        
        _fileLogger.Write("INFO", "GroupMergeEngine", $"Merged group {merged.GroupId} to version {merged.Version}: {merged.Members.Count} members, {merged.Admins.Count} admins");
        
        return merged;
    }
    
    private string ChooseNameByTimestamp(GroupState local, GroupState remote)
    {
        return local.Timestamp > remote.Timestamp ? local.Name : remote.Name;
    }
    
    private HashSet<string> MergeMembers(GroupState local, GroupState remote)
    {
        return local.Members.Union(remote.Members).ToHashSet();
    }
    
    private HashSet<string> MergeAdmins(GroupState local, GroupState remote)
    {
        var localAdminChanges = local.LastOperationActor != null && 
                                local.Admins.Contains(local.LastOperationActor);
        var remoteAdminChanges = remote.LastOperationActor != null && 
                                 remote.Admins.Contains(remote.LastOperationActor);
        
        if (localAdminChanges && remoteAdminChanges)
        {
            return local.Admins.Union(remote.Admins).ToHashSet();
        }
        
        if (localAdminChanges)
        {
            return local.Admins;
        }
        
        if (remoteAdminChanges)
        {
            return remote.Admins;
        }
        
        return local.Admins.Union(remote.Admins).ToHashSet();
    }
}