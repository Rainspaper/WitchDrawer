namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 「双击桌面空白」驱动的收纳盒整体隐藏状态机。
///
/// 只记录本次被整体隐藏的盒子：用户自己用 X 关掉的盒子、以及合并进宿主盒的
/// 成员盒，都不会在恢复时被错误拉起——恢复只针对进入隐藏那一刻确实可见的窗口。
/// </summary>
internal sealed class DesktopToggleVisibilityState
{
    private readonly HashSet<Guid> _hiddenBoxIds = [];

    /// <summary>是否处于「双击桌面」造成的整体隐藏状态。</summary>
    public bool IsHidden { get; private set; }

    /// <summary>当前被整体隐藏的盒子集合（只读，供诊断与测试观察）。</summary>
    public IReadOnlyCollection<Guid> HiddenBoxIds => _hiddenBoxIds;

    /// <summary>
    /// 进入整体隐藏。调用方传入此刻可见的盒子集合，返回需要真正隐藏的那些；
    /// 已经处于隐藏状态时返回空数组（幂等）。
    /// </summary>
    public Guid[] BeginHide(IEnumerable<Guid> visibleBoxIds)
    {
        ArgumentNullException.ThrowIfNull(visibleBoxIds);

        if (IsHidden)
        {
            return [];
        }

        var toHide = visibleBoxIds.Distinct().ToArray();
        IsHidden = true;
        _hiddenBoxIds.Clear();
        foreach (var boxId in toHide)
        {
            _hiddenBoxIds.Add(boxId);
        }

        return toHide;
    }

    /// <summary>
    /// 退出整体隐藏，返回需要恢复显示的盒子；未处于隐藏状态时返回空数组（幂等）。
    /// </summary>
    public Guid[] EndHide()
    {
        if (!IsHidden)
        {
            return [];
        }

        IsHidden = false;
        var toShow = _hiddenBoxIds.ToArray();
        _hiddenBoxIds.Clear();
        return toShow;
    }

    /// <summary>
    /// 整体隐藏期间新建了盒子窗口：把它并入隐藏集合。
    /// 返回窗口是否应当保持隐藏（调用方据此决定要不要立刻把新窗口藏起来）。
    /// </summary>
    public bool TrackIfHidden(Guid boxId)
    {
        if (!IsHidden)
        {
            return false;
        }

        _hiddenBoxIds.Add(boxId);
        return true;
    }

    /// <summary>
    /// 用户显式要求显示某个盒子（托盘菜单、主窗口召回等）：把它从整体隐藏集合里
    /// 摘出来；集合空了就自动退出隐藏状态。
    /// </summary>
    public void Forget(Guid boxId)
    {
        if (_hiddenBoxIds.Remove(boxId) && _hiddenBoxIds.Count == 0)
        {
            IsHidden = false;
        }
    }
}
