using WitchDrawer.App.ViewModels;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 合并宿主的网格布局：列数按盒子宽度算（能放下几列就几列，最多不超过条目数）。
/// 盒子变窄自动换行、变宽自动铺开；既不裁图标，也不会因为内容把盒子撑宽。
/// </summary>
public sealed class MergedHostGridLayoutTests
{
    [Theory]
    // 300 宽、格宽 74：(300-28)/74 = 3.67 → 3 列
    [InlineData(300, 74, 20, 3)]
    // 条目比能放下的列数少：不留空列
    [InlineData(300, 74, 2, 2)]
    // 太窄：至少 1 列，剩下靠行数消化
    [InlineData(100, 74, 20, 1)]
    // 很宽：铺满
    [InlineData(900, 74, 50, 11)]
    // 没有条目 / 宽度无效：不参与布局
    [InlineData(300, 74, 0, 0)]
    [InlineData(double.NaN, 74, 5, 0)]
    public void CalculateMergedGridColumns_FitsColumnsToTheBoxWidth(
        double boxWidth,
        double slotWidth,
        int itemCount,
        int expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxViewModel.CalculateMergedGridColumns(boxWidth, slotWidth, itemCount));
    }

    [Fact]
    public void CalculateMergedGridColumns_NeverOverflowsTheBoxWidth()
    {
        foreach (var boxWidth in new[] { 180d, 220, 300, 480, 720 })
        {
            var columns = DesktopBoxViewModel.CalculateMergedGridColumns(boxWidth, 74, 40);
            Assert.True(columns > 0);
            Assert.True(
                (columns * 74) + DesktopBoxViewModel.MergedGridWidthChrome <= boxWidth + 0.001,
                $"{boxWidth} 宽的盒子里 {columns} 列放不下。");
        }
    }
}
