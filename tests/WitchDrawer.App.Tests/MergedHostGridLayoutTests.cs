using WitchDrawer.App.ViewModels;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 合并宿主的网格布局：列数按盒子宽度算（能放下几列就几列，最多不超过条目数）。
/// 盒子变窄自动换行、变宽自动铺开；既不裁图标，也不会因为内容把盒子撑宽。
/// </summary>
public sealed class MergedHostGridLayoutTests
{
    [Theory]
    // 300 宽、格宽 74：(300-30)/74 = 3.65 → 3 列（30 = 边框/留白，见 MergedGridWidthChrome）
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

    [Theory]
    // 统一外宽 = 边框留白 + 整数列 × 图标格宽：对齐后网格里没有余数空带，
    // 右下角尺寸手柄也就落在图标块的右下角（而不是飘在盒子角上）。
    [InlineData(300, 37, 289)]  // (300-30)/37 = 7.29 → 7 列
    [InlineData(286, 55, 250)]  // 4x4：(286-30)/55 = 4.65 → 4 列
    [InlineData(180, 37, 178)]  // 6x6 下限 180 → 4 列，比下限略窄
    [InlineData(180, 55, 140)]  // 3x3 图标更宽：(180-30)/55 = 2.7 → 2 列
    [InlineData(220, 37, 215)]  // 默认宽度本来就没余数：不会被来回挪
    [InlineData(720, 74, 696)]  // 上限：(720-30)/74 = 9.3 → 9 列
    [InlineData(90, 74, 104)]   // 窄到放不下一列：按一列走（调用方已先夹到下限）
    public void SnapHostWidthToGridColumns_FitsWholeIconColumns(
        double boxWidth,
        double slotWidth,
        double expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxViewModel.SnapHostWidthToGridColumns(boxWidth, slotWidth),
            precision: 6);
    }

    [Theory]
    [InlineData(74)]
    [InlineData(55)]
    [InlineData(44)]
    [InlineData(37)]
    public void SnapHostWidthToGridColumns_LeavesNoDeadBand(double slotWidth)
    {
        foreach (var boxWidth in new[] { 180d, 220, 286, 300, 480, 720 })
        {
            var snapped = DesktopBoxViewModel.SnapHostWidthToGridColumns(boxWidth, slotWidth);
            var usableWidth = snapped - DesktopBoxViewModel.MergedGridWidthChrome;
            Assert.Equal(0d, usableWidth % slotWidth, precision: 6);

            var columns = (int)Math.Round(usableWidth / slotWidth);
            // 整数列正好铺满可用宽度：图标块右侧不再留空带。
            Assert.Equal(
                snapped,
                DesktopBoxViewModel.MergedGridWidthChrome + (columns * slotWidth),
                precision: 6);
            // 条目够多时列数就是铺满的列数；再多一列就塞不进盒子。
            Assert.Equal(columns, DesktopBoxViewModel.CalculateMergedGridColumns(snapped, slotWidth, 40));
            Assert.True(
                DesktopBoxViewModel.CalculateMergedGridColumns(snapped + slotWidth, slotWidth, 40) > columns);
        }
    }
}
