using WitchDrawer.App.Infrastructure;

namespace WitchDrawer.App.Tests;

public sealed class DesktopToggleVisibilityStateTests
{
    private static readonly Guid BoxA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BoxB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BoxC = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void BeginHide_ReturnsVisibleBoxesAndEntersHiddenState()
    {
        var state = new DesktopToggleVisibilityState();

        var toHide = state.BeginHide([BoxA, BoxB]);

        Assert.True(state.IsHidden);
        Assert.Equal([BoxA, BoxB], toHide);
    }

    [Fact]
    public void BeginHide_IsIdempotentWhileAlreadyHidden()
    {
        var state = new DesktopToggleVisibilityState();
        state.BeginHide([BoxA]);

        // 隐藏期间又收到一次「隐藏」：不能把新出现的可见盒子卷进来，
        // 否则恢复时会显示一个当时根本没隐藏的窗口。
        var second = state.BeginHide([BoxA, BoxB]);

        Assert.Empty(second);
        Assert.Equal([BoxA], state.HiddenBoxIds.OrderBy(id => id));
    }

    [Fact]
    public void EndHide_RestoresOnlyTheBoxesHiddenByTheToggle()
    {
        var state = new DesktopToggleVisibilityState();
        state.BeginHide([BoxA, BoxB]);

        var toShow = state.EndHide();

        Assert.False(state.IsHidden);
        Assert.Equal([BoxA, BoxB], toShow);
        Assert.Empty(state.HiddenBoxIds);
    }

    [Fact]
    public void EndHide_IsIdempotentWhenNotHidden()
    {
        var state = new DesktopToggleVisibilityState();

        Assert.Empty(state.EndHide());
        Assert.False(state.IsHidden);
    }

    [Fact]
    public void BeginHide_DistinctsRepeatedBoxIds()
    {
        var state = new DesktopToggleVisibilityState();

        Assert.Equal([BoxA], state.BeginHide([BoxA, BoxA]));
    }

    [Fact]
    public void TrackIfHidden_OnlyTracksWhileHidden()
    {
        var state = new DesktopToggleVisibilityState();

        Assert.False(state.TrackIfHidden(BoxC));

        state.BeginHide([BoxA]);
        Assert.True(state.TrackIfHidden(BoxC));

        Assert.Equal([BoxA, BoxC], state.EndHide().OrderBy(id => id));
    }

    [Fact]
    public void Forget_DropsSingleBoxAndLeavesHiddenStateWhenEmpty()
    {
        var state = new DesktopToggleVisibilityState();
        state.BeginHide([BoxA, BoxB]);

        state.Forget(BoxA);
        Assert.True(state.IsHidden);

        state.Forget(BoxB);
        Assert.False(state.IsHidden);
        Assert.Empty(state.HiddenBoxIds);
    }

    [Fact]
    public void Forget_KeepsHiddenStateForBoxThatWasNeverTracked()
    {
        var state = new DesktopToggleVisibilityState();
        state.BeginHide([BoxA]);

        state.Forget(BoxC);

        Assert.True(state.IsHidden);
        Assert.Equal([BoxA], state.HiddenBoxIds.OrderBy(id => id));
    }

    [Fact]
    public void BeginHide_NullInputThrows()
    {
        var state = new DesktopToggleVisibilityState();

        Assert.Throws<ArgumentNullException>(() => state.BeginHide(null!));
    }
}
