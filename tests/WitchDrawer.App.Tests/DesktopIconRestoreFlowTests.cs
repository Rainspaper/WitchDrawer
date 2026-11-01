using WitchDrawer.App.Infrastructure;

namespace WitchDrawer.App.Tests;

public sealed class DesktopIconRestoreFlowTests
{
    [Fact]
    public async Task RestoreTransientHide_FlaggedAndHidden_RestoresThenClearsFlag()
    {
        var calls = new List<string>();

        var restored = await DesktopIconRestoreFlow.RestoreTransientHideAsync(
            isTransientFlaggedAsync: () => Task.FromResult(true),
            isHidden: () => true,
            restoreAsync: () =>
            {
                calls.Add("restore");
                return Task.CompletedTask;
            },
            clearFlagAsync: () =>
            {
                calls.Add("clear");
                return Task.CompletedTask;
            });

        Assert.True(restored);
        Assert.Equal(["restore", "clear"], calls);
    }

    [Fact]
    public async Task RestoreTransientHide_FlaggedButVisible_DoesNothing()
    {
        var calls = new List<string>();

        var restored = await DesktopIconRestoreFlow.RestoreTransientHideAsync(
            isTransientFlaggedAsync: () => Task.FromResult(true),
            isHidden: () => false,
            restoreAsync: () =>
            {
                calls.Add("restore");
                return Task.CompletedTask;
            },
            clearFlagAsync: () =>
            {
                calls.Add("clear");
                return Task.CompletedTask;
            });

        Assert.False(restored);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task RestoreTransientHide_NotFlagged_DoesNothingEvenWhenHidden()
    {
        // 设置页手动隐藏（无标记）必须跨重启保留：不能被自动恢复打扰。
        var calls = new List<string>();

        var restored = await DesktopIconRestoreFlow.RestoreTransientHideAsync(
            isTransientFlaggedAsync: () => Task.FromResult(false),
            isHidden: () => true,
            restoreAsync: () =>
            {
                calls.Add("restore");
                return Task.CompletedTask;
            },
            clearFlagAsync: () =>
            {
                calls.Add("clear");
                return Task.CompletedTask;
            });

        Assert.False(restored);
        Assert.Empty(calls);
    }
}
