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

    [Theory]
    // 列表模式：宽度完全听用户的，不用网格内容兜底（回归：列表模式曾被强制撑到上限、拖不动）。
    [InlineData(true, false, 900, 0)]
    // 用户拖拽过宽度：内容再宽也不再撑宽盒子。
    [InlineData(false, true, 900, 0)]
    // 网格模式 + 用户没设过宽度：内容兜底（保证图标不被裁）。
    [InlineData(false, false, 900, 900 + DesktopBoxViewModel.MergedGridWidthChrome)]
    // 还没量到内容宽度：不兜底。
    [InlineData(false, false, 0, 0)]
    public void ResolveContentWidthFloor_SkipsListModeAndUserChosenWidths(
        bool isMappingListMode,
        bool hasCustomWidth,
        double gridCanvasWidth,
        double expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxViewModel.ResolveContentWidthFloor(
                isMappingListMode,
                hasCustomWidth,
                gridCanvasWidth));
    }
}
