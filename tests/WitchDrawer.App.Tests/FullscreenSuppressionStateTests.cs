using WitchDrawer.App.Infrastructure;

namespace WitchDrawer.App.Tests;

public sealed class FullscreenSuppressionStateTests
{
    private static readonly Guid BoxA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BoxB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void BeginSuppress_ReportsOnlyTheFirstTransition()
    {
        var state = new FullscreenSuppressionState();

        Assert.True(state.BeginSuppress(BoxA));
        Assert.False(state.BeginSuppress(BoxA));
        Assert.True(state.IsSuppressed(BoxA));
        Assert.Equal([BoxA], state.SuppressedBoxIds);
    }

    [Fact]
    public void EndSuppress_ReportsWhetherTheBoxWasSuppressed()
    {
        var state = new FullscreenSuppressionState();
        state.BeginSuppress(BoxA);

        Assert.True(state.EndSuppress(BoxA));
        Assert.False(state.EndSuppress(BoxA));
        Assert.False(state.IsSuppressed(BoxA));
        Assert.Empty(state.SuppressedBoxIds);
    }

    [Fact]
    public void EndSuppress_KeepsOtherBoxesSuppressed()
    {
        var state = new FullscreenSuppressionState();
        state.BeginSuppress(BoxA);
        state.BeginSuppress(BoxB);

        state.EndSuppress(BoxA);

        Assert.False(state.IsSuppressed(BoxA));
        Assert.True(state.IsSuppressed(BoxB));
    }

    [Fact]
    public void Forget_RemovesSuppressionSoAnExplicitShowWins()
    {
        // 用户显式召回某个盒子时，全屏压制记录必须让位，否则盒子会立刻被再次藏起来。
        var state = new FullscreenSuppressionState();
        state.BeginSuppress(BoxA);

        state.Forget(BoxA);

        Assert.False(state.IsSuppressed(BoxA));
        Assert.Empty(state.SuppressedBoxIds);
    }

    [Fact]
    public void Forget_IsIdempotentForUnknownBoxes()
    {
        var state = new FullscreenSuppressionState();

        state.Forget(BoxA);

        Assert.Empty(state.SuppressedBoxIds);
    }
}
