namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 「全屏程序在前台」驱动的收纳盒压制状态机。
///
/// 与「双击桌面」的整体隐藏相互独立、可以叠加：某个盒子可能同时被两种原因隐藏，
/// 只要还有一种原因成立就不该显示出来，因此这里只记录由全屏压制造成的那些盒子。
/// </summary>
internal sealed class FullscreenSuppressionState
{
    private readonly HashSet<Guid> _suppressedBoxIds = [];

    /// <summary>当前被全屏压制隐藏的盒子集合（只读，供诊断与测试观察）。</summary>
    public IReadOnlyCollection<Guid> SuppressedBoxIds => _suppressedBoxIds;

    /// <summary>盒子当前是否处于全屏压制状态。</summary>
    public bool IsSuppressed(Guid boxId) => _suppressedBoxIds.Contains(boxId);

    /// <summary>
    /// 开始压制某个盒子；返回它是否刚刚进入压制（已在压制中时返回 false，调用方
    /// 据此避免重复隐藏与重复写日志）。
    /// </summary>
    public bool BeginSuppress(Guid boxId) => _suppressedBoxIds.Add(boxId);

    /// <summary>解除某个盒子的压制；返回它此前是否处于压制状态。</summary>
    public bool EndSuppress(Guid boxId) => _suppressedBoxIds.Remove(boxId);

    /// <summary>
    /// 用户显式要求显示某个盒子（托盘菜单、主窗口召回等）：把它从压制集合里摘出来，
    /// 用户意图优先于全屏压制。
    /// </summary>
    public void Forget(Guid boxId) => _suppressedBoxIds.Remove(boxId);
}
