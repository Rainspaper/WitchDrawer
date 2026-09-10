using WitchDrawer.App.ViewModels;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 合并宿主的"统一外宽"：网格与列表共用同一个宽度，切换模式/标签时盒子不变宽变窄。
/// 宽度 = max(用户设定的映射盒宽度, 网格内容需要的宽度)，再夹在映射盒宽度上下限之间。
/// </summary>
public sealed class MergedHostWidthTests
{
    [Theory]
    // 用户设定了宽度、内容更窄 → 用用户设定的宽度。
    [InlineData(260, 0, 260)]
    // 某个标签的网格内容比设定宽度还宽 → 撑到内容宽度，保证图标不被裁。
    [InlineData(220, 320, 320)]
    // 两者都很小 → 保住下限。
    [InlineData(100, 60, DesktopBoxViewModel.MinimumMappingListWidth)]
    // 内容极大 → 止步于上限。
    [InlineData(220, 5000, DesktopBoxViewModel.MaximumMappingListWidth)]
    public void CalculateHostBoxWidth_TakesTheWiderOfSettingAndContent(
        double storedWidth,
        double contentFloorWidth,
        double expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxViewModel.CalculateHostBoxWidth(storedWidth, contentFloorWidth));
    }

    [Fact]
    public void CalculateHostBoxWidth_OnlyGrowsWithTheContentFloor()
    {
        // 只增不减：内容更宽时变宽，内容变窄时保持原宽度（避免切换标签时忽宽忽窄）。
        var wide = DesktopBoxViewModel.CalculateHostBoxWidth(220, 400);
        var narrow = DesktopBoxViewModel.CalculateHostBoxWidth(wide, 180);

        Assert.Equal(wide, narrow);
    }
}
