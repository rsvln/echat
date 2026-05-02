using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Protocol;
using EChat.Core.Services;
using EChat.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace EChat.Core.Groups;

public class GroupStateManager
{
    private readonly FileLogger _fileLogger;
    private readonly ChatDbContext _dbContext;
    private readonly GroupMergeEngine _mergeEngine;
    
    public GroupStateManager(
        FileLogger fileLogger,
        ChatDbContext dbContext,
        GroupMergeEngine mergeEngine)
    {
        _fileLogger = fileLogger;
        _dbContext = dbContext;
        _mergeEngine = mergeEngine;
    }
    
    public async Task<GroupState> GetGroupStateAsync(string groupId)
    {
        var group = await _dbContext.Groups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.GroupId == groupId);
        
        if (group == null)
        {
            throw new InvalidOperationException($"Group {groupId} not found");
        }
        
        return new GroupState
        {
            GroupId = group.GroupId,
            Name = group.Name,
            Version = group.Version,
            Members = group.Members.Select(m => m.MemberEmail).ToHashSet(),
            Admins = group.Members.Where(m => m.Role == GroupRole.Admin)
                                  .Select(m => m.MemberEmail).ToHashSet(),
            Timestamp = NtpClock.UtcNow
        };
    }
    
    public async Task<GroupState> ApplyRemoteStateAsync(GroupState remoteState, string actor)
    {
        var localGroup = await _dbContext.Groups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.GroupId == remoteState.GroupId);
        
        if (localGroup == null)
        {
            return await CreateGroupAsync(remoteState, actor);
        }
        
        var localState = new GroupState
        {
            GroupId = localGroup.GroupId,
            Name = localGroup.Name,
            Version = localGroup.Version,
            Members = localGroup.Members.Select(m => m.MemberEmail).ToHashSet(),
            Admins = localGroup.Members.Where(m => m.Role == GroupRole.Admin)
                                       .Select(m => m.MemberEmail).ToHashSet(),
            LastOperationActor = actor,
            Timestamp = NtpClock.UtcNow
        };
        
        if (localState.Version == remoteState.Version && !localState.Equals(remoteState))
        {
            _fileLogger.Write("WARN", "GroupStateManager", $"Group version conflict detected for {remoteState.GroupId}");
            var mergedState = _mergeEngine.MergeConflict(localState, remoteState);
            return await UpdateGroupAsync(mergedState, actor);
        }
        
        if (remoteState.Version > localState.Version)
        {
            return await UpdateGroupAsync(remoteState, actor);
        }
        
        return localState;
    }
    
    private async Task<GroupState> CreateGroupAsync(GroupState state, string creator)
    {
        var group = new ChatGroup
        {
            GroupId = state.GroupId,
            Name = state.Name,
            Version = state.Version,
            CreatedAt = DateTimeOffset.UtcNow
        };
        
        foreach (var memberEmail in state.Members)
        {
            group.Members.Add(new GroupMember
            {
                GroupId = state.GroupId,
                MemberEmail = memberEmail,
                Role = state.Admins.Contains(memberEmail) ? GroupRole.Admin : GroupRole.Member,
                AddedAt = DateTimeOffset.UtcNow,
                AddedBy = creator,
                NameColor = GroupPalette.PickColor(memberEmail)
            });
        }
        
        _dbContext.Groups.Add(group);
        await _dbContext.SaveChangesAsync();
        
        _fileLogger.Write("INFO", "GroupStateManager", $"Created group {state.GroupId} with {state.Members.Count} members");
        
        return state;
    }
    
    private async Task<GroupState> UpdateGroupAsync(GroupState state, string actor)
    {
        var group = await _dbContext.Groups
            .Include(g => g.Members)
            .FirstAsync(g => g.GroupId == state.GroupId);
        
        group.Name = state.Name;
        group.Version = state.Version;
        
        var existingMembers = group.Members.ToDictionary(m => m.MemberEmail);
        
        foreach (var memberEmail in state.Members)
        {
            if (!existingMembers.ContainsKey(memberEmail))
            {
                group.Members.Add(new GroupMember
                {
                    GroupId = state.GroupId,
                    MemberEmail = memberEmail,
                    Role = state.Admins.Contains(memberEmail) ? GroupRole.Admin : GroupRole.Member,
                    AddedAt = DateTimeOffset.UtcNow,
                    AddedBy = actor,
                    NameColor = GroupPalette.PickColor(memberEmail)
                });
            }
            else
            {
                var member = existingMembers[memberEmail];
                member.Role = state.Admins.Contains(memberEmail) ? GroupRole.Admin : GroupRole.Member;
            }
        }
        
        var toRemove = group.Members.Where(m => !state.Members.Contains(m.MemberEmail)).ToList();
        foreach (var member in toRemove)
        {
            group.Members.Remove(member);
        }
        
        await _dbContext.SaveChangesAsync();
        
        _fileLogger.Write("INFO", "GroupStateManager", $"Updated group {state.GroupId} to version {state.Version}");
        
        return state;
    }
}