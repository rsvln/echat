using EChat.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace EChat.Core.Groups;

public class GroupMergeEngine
{
    private readonly ILogger<GroupMergeEngine> _logger;
    
    public GroupMergeEngine(ILogger<GroupMergeEngine> logger)
    {
        _logger = logger;
    }
    
    public GroupState MergeConflict(GroupState local, GroupState remote)
    {
        _logger.LogWarning("Merging group conflict for {GroupId}: local v{LocalVersion} vs remote v{RemoteVersion}",
            local.GroupId, local.Version, remote.Version);
        
        var merged = new GroupState
        {
            GroupId = local.GroupId,
            Version = Math.Max(local.Version, remote.Version) + 1,
            Name = ChooseNameByTimestamp(local, remote),
            Members = MergeMembers(local, remote),
            Admins = MergeAdmins(local, remote),
            Timestamp = DateTimeOffset.UtcNow
        };
        
        _logger.LogInformation("Merged group {GroupId} to version {Version}: {MemberCount} members, {AdminCount} admins",
            merged.GroupId, merged.Version, merged.Members.Count, merged.Admins.Count);
        
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