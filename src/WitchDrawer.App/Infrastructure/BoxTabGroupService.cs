using System.Collections.Concurrent;
using System.Text.Json;
using WitchDrawer.Core.Logging;
using WitchDrawer.Core.Services;

namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 管理多个收纳盒合并为多标签总盒子的聚合关系。
/// 以宿主盒（host）为基础，其他成员盒作为其标签页聚合显示。
/// 持久化走 <see cref="DrawerService"/> 设置键，不修改数据库 schema：
/// <list type="bullet">
/// <item><c>BoxTabHost:{hostId:N}</c> = JSON 数组 [memberId, ...]，顺序即标签顺序。</item>
/// <item><c>BoxTabMember:{memberId:N}</c> = hostId 字符串，用于反查归属。</item>
/// </list>
/// 盒删除时这些设置键会残留（与现有按盒维度的设置键行为一致），
/// 查询时对无效 Guid 静默过滤，不会产生悬空引用。
/// </summary>
public sealed class BoxTabGroupService(
    DrawerService drawerService,
    IAppLogger logger)
{
    private const string HostSettingKeyPrefix = "BoxTabHost:";
    private const string MemberSettingKeyPrefix = "BoxTabMember:";

    private readonly ConcurrentDictionary<Guid, List<Guid>> _hostCache = new();
    private readonly ConcurrentDictionary<Guid, Guid> _memberToHostCache = new();

    /// <summary>
    /// 读取宿主盒下的成员盒 Id 列表（按标签顺序）。空列表表示未作为宿主合并任何盒子。
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetMembersAsync(
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        if (_hostCache.TryGetValue(hostId, out var cachedMembers))
        {
            return cachedMembers;
        }

        var value = await drawerService.GetSettingAsync(
            HostSettingKeyPrefix + hostId.ToString("N"),
            cancellationToken);
        var members = ParseMembers(value);
        _hostCache[hostId] = members;
        return members;
    }

    /// <summary>
    /// 读取成员盒所属的宿主盒 Id；未合并到任何组返回 null。
    /// </summary>
    public async Task<Guid?> GetHostAsync(
        Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (_memberToHostCache.TryGetValue(memberId, out var cachedHost))
        {
            return cachedHost;
        }

        var value = await drawerService.GetSettingAsync(
            MemberSettingKeyPrefix + memberId.ToString("N"),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Guid.TryParse(value, out var hostId) || hostId == Guid.Empty)
        {
            logger.Info(
                $"Ignored invalid tab-host reference for member {memberId:N}: {value}.");
            return null;
        }

        _memberToHostCache[memberId] = hostId;
        return hostId;
    }

    public Task<bool> IsHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        return GetMembersAsync(hostId, cancellationToken)
            .ContinueWith(task => task.Result.Count > 0, cancellationToken);
    }

    public async Task<bool> IsMemberAsync(
        Guid memberId,
        CancellationToken cancellationToken = default)
    {
        return await GetHostAsync(memberId, cancellationToken) is not null;
    }

    /// <summary>
    /// 把成员盒合并进宿主盒。若成员原本属于其他组，先从原组移除。
    /// 不允许把自己合并进自己。
    /// </summary>
    public async Task MergeAsync(
        Guid memberId,
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        if (memberId == hostId)
        {
            throw new ArgumentException(
                "A box cannot be merged into itself.",
                nameof(memberId));
        }

        var currentHost = await GetHostAsync(memberId, cancellationToken);
        if (currentHost == hostId)
        {
            return;
        }

        if (currentHost is Guid previousHost)
        {
            await RemoveMemberFromHostAsync(memberId, previousHost, cancellationToken);
        }

        var members = (await GetMembersAsync(hostId, cancellationToken)).ToList();
        if (!members.Contains(memberId))
        {
            members.Add(memberId);
        }

        await PersistMembersAsync(hostId, members, cancellationToken);
        await drawerService.SetSettingAsync(
            MemberSettingKeyPrefix + memberId.ToString("N"),
            hostId.ToString("N"),
            cancellationToken);
        _memberToHostCache[memberId] = hostId;
        logger.Info($"Merged box {memberId:N} into tab host {hostId:N}.");
    }

    /// <summary>
    /// 从其所在组中移除一个成员盒（解散该标签）。返回是否原本处于某组中。
    /// </summary>
    public async Task<bool> RemoveMemberAsync(
        Guid memberId,
        CancellationToken cancellationToken = default)
    {
        var hostId = await GetHostAsync(memberId, cancellationToken);
        if (hostId is not Guid host)
        {
            return false;
        }

        await RemoveMemberFromHostAsync(memberId, host, cancellationToken);
        await drawerService.DeleteSettingAsync(
            MemberSettingKeyPrefix + memberId.ToString("N"),
            cancellationToken);
        _memberToHostCache.TryRemove(memberId, out _);
        logger.Info($"Removed box {memberId:N} from tab host {host:N}.");
        return true;
    }

    /// <summary>
    /// 解散宿主盒的整组：清空所有成员归属与宿主键。成员盒本身不受影响。
    /// </summary>
    public async Task DissolveAsync(
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        var members = await GetMembersAsync(hostId, cancellationToken);
        foreach (var memberId in members)
        {
            await drawerService.DeleteSettingAsync(
                MemberSettingKeyPrefix + memberId.ToString("N"),
                cancellationToken);
            _memberToHostCache.TryRemove(memberId, out _);
        }

        await drawerService.DeleteSettingAsync(
            HostSettingKeyPrefix + hostId.ToString("N"),
            cancellationToken);
        _hostCache.TryRemove(hostId, out _);
        logger.Info(
            $"Dissolved tab group for host {hostId:N} ({members.Count} members).");
    }

    private async Task RemoveMemberFromHostAsync(
        Guid memberId,
        Guid hostId,
        CancellationToken cancellationToken)
    {
        var members = (await GetMembersAsync(hostId, cancellationToken)).ToList();
        if (members.RemoveAll(id => id == memberId) == 0)
        {
            return;
        }

        if (members.Count == 0)
        {
            await drawerService.DeleteSettingAsync(
                HostSettingKeyPrefix + hostId.ToString("N"),
                cancellationToken);
            _hostCache.TryRemove(hostId, out _);
        }
        else
        {
            await PersistMembersAsync(hostId, members, cancellationToken);
        }
    }

    private async Task PersistMembersAsync(
        Guid hostId,
        IReadOnlyList<Guid> members,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(members);
        await drawerService.SetSettingAsync(
            HostSettingKeyPrefix + hostId.ToString("N"),
            json,
            cancellationToken);
        _hostCache[hostId] = members.ToList();
    }

    private static List<Guid> ParseMembers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(value);
            if (list is null)
            {
                return [];
            }

            var guids = new List<Guid>(list.Count);
            foreach (var id in list)
            {
                if (Guid.TryParse(id, out var guid) && guid != Guid.Empty)
                {
                    guids.Add(guid);
                }
            }

            return guids;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
