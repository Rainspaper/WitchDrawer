using WitchDrawer.Native.Windows;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 只测几何判定这个纯函数：真实的全屏判定依赖前台窗口与真实显示器，
/// 放在实机验证，不进单元测试。
/// </summary>
public sealed class FullscreenWindowDetectorTests
{
    private const int Tolerance = 2;

    private static FullscreenWindowDetector.NativeScreenRect Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    [Fact]
    public void CoversMonitor_AcceptsAnExactMatch()
    {
        var monitor = Rect(0, 0, 1920, 1080);

        Assert.True(FullscreenWindowDetector.CoversMonitor(Rect(0, 0, 1920, 1080), monitor, Tolerance));
    }

    [Fact]
    public void CoversMonitor_AcceptsEdgesWithinTolerance()
    {
        var monitor = Rect(0, 0, 1920, 1080);

        // 无边框全屏常会因边框/DPI 取整少一两像素。
        Assert.True(FullscreenWindowDetector.CoversMonitor(Rect(1, 1, 1919, 1079), monitor, Tolerance));
    }

    [Fact]
    public void CoversMonitor_RejectsEdgesBeyondTolerance()
    {
        var monitor = Rect(0, 0, 1920, 1080);

        Assert.False(FullscreenWindowDetector.CoversMonitor(Rect(3, 0, 1920, 1080), monitor, Tolerance));
        Assert.False(FullscreenWindowDetector.CoversMonitor(Rect(0, 0, 1916, 1080), monitor, Tolerance));
    }

    [Fact]
    public void CoversMonitor_RejectsATaskbarSizedGap()
    {
        // 任务栏可见时被最大化的普通窗口只覆盖工作区（顶部让出 32px）。
        var monitor = Rect(0, 0, 1920, 1080);

        Assert.False(FullscreenWindowDetector.CoversMonitor(Rect(0, 32, 1920, 1054), monitor, Tolerance));
    }

    [Fact]
    public void CoversMonitor_AcceptsASecondaryMonitorWindow()
    {
        var secondary = Rect(-2560, -200, 0, 1240);

        Assert.True(FullscreenWindowDetector.CoversMonitor(Rect(-2560, -200, 0, 1240), secondary, Tolerance));
        // 主屏上的全屏窗口不覆盖副屏。
        Assert.False(FullscreenWindowDetector.CoversMonitor(Rect(0, 0, 1920, 1080), secondary, Tolerance));
    }

    [Fact]
    public void IsMaximizedRegularWindow_RejectsAFramedMaximizedWindow()
    {
        // 2026-10-08 实机：副屏上最大化的 Electron 窗口 rect 比 rcMonitor 大 7px。
        var monitor = Rect(0, -914, 1463, 0);

        Assert.True(FullscreenWindowDetector.IsMaximizedRegularWindow(
            isMaximized: true,
            hasWindowFrame: true,
            Rect(-7, -921, 1470, 7),
            monitor,
            Tolerance));
    }

    [Fact]
    public void IsMaximizedRegularWindow_RejectsAnUnframedWindowSpillingPastTheMonitor()
    {
        var monitor = Rect(0, 0, 1920, 1080);

        Assert.True(FullscreenWindowDetector.IsMaximizedRegularWindow(
            isMaximized: true,
            hasWindowFrame: false,
            Rect(-8, -8, 1928, 1088),
            monitor,
            Tolerance));
    }

    [Fact]
    public void IsMaximizedRegularWindow_AcceptsAnUnframedFullscreenSizedWindow()
    {
        // 无边框窗口化游戏可能自己调用最大化，但矩形恰好等于显示器。
        var monitor = Rect(0, 0, 1920, 1080);

        Assert.False(FullscreenWindowDetector.IsMaximizedRegularWindow(
            isMaximized: true,
            hasWindowFrame: false,
            Rect(0, 0, 1920, 1080),
            monitor,
            Tolerance));
    }

    [Fact]
    public void IsMaximizedRegularWindow_AcceptsNonMaximizedWindows()
    {
        var monitor = Rect(0, 0, 1920, 1080);

        Assert.False(FullscreenWindowDetector.IsMaximizedRegularWindow(
            isMaximized: false,
            hasWindowFrame: true,
            Rect(-8, -8, 1928, 1088),
            monitor,
            Tolerance));
    }
}
