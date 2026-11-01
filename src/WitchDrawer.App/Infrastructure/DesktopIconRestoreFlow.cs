namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 桌面图标隐藏状态的启动恢复流程。
/// 「双击桌面切换」产生的隐藏属于临时切换（下次启动恢复显示）；
/// 设置页开关产生的隐藏是持久意图（跨重启保留），以标记区分两类来源。
/// </summary>
internal static class DesktopIconRestoreFlow
{
    /// <summary>
    /// 当「隐藏来自双击临时切换」且图标确实处于隐藏状态时，恢复显示并清除标记。
    /// 返回是否执行了恢复。
    /// </summary>
    internal static async Task<bool> RestoreTransientHideAsync(
        Func<Task<bool>> isTransientFlaggedAsync,
        Func<bool> isHidden,
        Func<Task> restoreAsync,
        Func<Task> clearFlagAsync)
    {
        ArgumentNullException.ThrowIfNull(isTransientFlaggedAsync);
        ArgumentNullException.ThrowIfNull(isHidden);
        ArgumentNullException.ThrowIfNull(restoreAsync);
        ArgumentNullException.ThrowIfNull(clearFlagAsync);

        if (!await isTransientFlaggedAsync() || !isHidden())
        {
            return false;
        }

        await restoreAsync();
        await clearFlagAsync();
        return true;
    }
}
