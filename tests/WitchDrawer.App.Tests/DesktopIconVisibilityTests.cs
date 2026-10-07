using WitchDrawer.Native.Windows;

namespace WitchDrawer.App.Tests;

public sealed class DesktopIconVisibilityTests
{
    [Fact]
    public void ApplyHiddenState_UpdatesDesktopBeforeSendingNotifications()
    {
        var calls = new List<string>();
        var desktopListView = new nint(42);

        DesktopIconVisibility.ApplyHiddenState(
            hidden: true,
            hidden => calls.Add($"registry:{hidden}"),
            () =>
            {
                calls.Add("find");
                return desktopListView;
            },
            (window, hidden) => calls.Add($"window:{window}:{hidden}"),
            () => calls.Add("notify"));

        Assert.Equal(
            ["registry:True", "find", "window:42:True", "notify"],
            calls);
    }

    [Fact]
    public void ApplyHiddenState_StillNotifiesWhenDesktopListViewIsUnavailable()
    {
        var calls = new List<string>();

        DesktopIconVisibility.ApplyHiddenState(
            hidden: false,
            hidden => calls.Add($"registry:{hidden}"),
            () =>
            {
                calls.Add("find");
                return nint.Zero;
            },
            (_, _) => calls.Add("window"),
            () => calls.Add("notify"));

        Assert.Equal(["registry:False", "find", "notify"], calls);
    }

    [Fact]
    public async Task RunSetHiddenAsync_DoesNotBlockCallerWhileNativeUpdateIsPending()
    {
        using var nativeUpdateStarted = new ManualResetEventSlim();
        using var releaseNativeUpdate = new ManualResetEventSlim();

        var operation = DesktopIconVisibility.RunSetHiddenAsync(
            hidden =>
            {
                Assert.True(hidden);
                nativeUpdateStarted.Set();
                Assert.True(releaseNativeUpdate.Wait(TimeSpan.FromSeconds(5)));
            },
            hidden: true);

        Assert.True(nativeUpdateStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(operation.IsCompleted);

        releaseNativeUpdate.Set();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RunRestoreIfHidden_RestoresOnlyWhenCurrentlyHidden()
    {
        var setValues = new List<bool>();

        var restored = DesktopIconVisibility.RunRestoreIfHidden(
            isHidden: () => true,
            setHidden: value => setValues.Add(value));

        Assert.True(restored);
        Assert.Equal([false], setValues);

        setValues.Clear();
        restored = DesktopIconVisibility.RunRestoreIfHidden(
            isHidden: () => false,
            setHidden: value => setValues.Add(value));

        Assert.False(restored);
        Assert.Empty(setValues);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    [InlineData("1", false)]
    public void IsHiddenRegistryValue_RecognizesOnlyNonZeroRegistryNumbers(
        object? value,
        bool expected)
    {
        Assert.Equal(expected, DesktopIconVisibility.IsHiddenRegistryValue(value));
    }

    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("SHELLDLL_DefView", true)]
    [InlineData("SysListView32", false)]
    [InlineData("WitchDrawer", false)]
    [InlineData(null, false)]
    public void IsDesktopHostClass_RecognizesOnlyDesktopHosts(string? className, bool expected)
    {
        Assert.Equal(expected, DesktopIconVisibility.IsDesktopHostClass(className));
    }

    [Theory]
    // 图标被隐藏（HideIcons=1）时 ListView 不可见，WindowFromPoint 只会带回
    // Progman/WorkerW 这类桌面宿主。此时仍必须走 ListView 命中测试——退化成
    // 「命中桌面宿主即空白」就会把「双击桌面图标」判成「双击桌面空白」，
    // 从而把全部图标切走。
    [InlineData(true, false, true, true)]
    // 命中 ListView 本身同样走精确判定。
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    // 命中的既不是桌面视图也不是桌面宿主（例如叠在桌面上的普通窗口）：不触发。
    [InlineData(true, false, false, false)]
    // 找不到桌面 ListView：无法精确判定，交给调用方的兜底分支。
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    public void ShouldHitTestDesktopListView_KeepsPreciseHitTestWhenListViewIsInvisible(
        bool hasDesktopListView,
        bool clickedIsDesktopListView,
        bool clickedIsDesktopHost,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopIconVisibility.ShouldHitTestDesktopListView(
                hasDesktopListView,
                clickedIsDesktopListView,
                clickedIsDesktopHost));
    }
}
