using WitchDrawer.App.Infrastructure;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 标签卡取宽：等分标签栏可用宽度，并夹在上下限之间。
/// 期望行为：常规几个标签正好平分一行；标签极多时停止压缩（交给横向滚动）；
/// 标签极少时也不让单个标签拉满整行。
/// </summary>
public sealed class TabChipMetricsTests
{
    [Theory]
    // 宿主盒 223 宽、3 个标签：(223-16)/3-2 = 67 → 等分后正好排满一行，不需要滚动。
    [InlineData(223, 3, 67, 6)]
    // 5 个标签：(223-16)/5-2 = 39.4 → 继续缩小以放下整排标签，而不是截断。
    [InlineData(223, 5, 39.4, 6)]
    // 盒子被拖窄到 100：(100-16)/3-2 = 26 → 标签跟着缩，三个都还在。
    [InlineData(100, 3, 26, 6)]
    // 单个标签：不拉满整行，止步于上限。
    [InlineData(223, 1, 120, 6)]
    // 宽盒子 2 个标签：(600-16)/2-2 = 290 → 同上限。
    [InlineData(600, 2, 120, 6)]
    public void CalculateChipWidth_SplitsTheStripAndClampsToBounds(
        double stripWidth,
        int tabCount,
        double expected,
        int precision)
    {
        Assert.Equal(
            expected,
            TabChipMetrics.CalculateChipWidth(stripWidth, tabCount),
            precision);
    }

    [Fact]
    public void CalculateChipWidth_KeepsEveryTabInsideTheStripWhenTheBoxShrinks()
    {
        // 回归：盒子缩小后标签被截掉。只要还没有触到硬下限，
        // 整排标签（含各自外边距）必须放得进标签栏可用宽度里。
        foreach (var stripWidth in new[] { 60d, 80, 100, 140, 200, 223, 340, 600 })
        {
            for (var tabCount = 1; tabCount <= 6; tabCount++)
            {
                var chipWidth = TabChipMetrics.CalculateChipWidth(stripWidth, tabCount);
                if (chipWidth <= TabChipMetrics.MinimumChipWidth)
                {
                    continue; // 已到硬下限，此时才允许溢出（横向滚动兜底）
                }

                var required = (chipWidth + TabChipMetrics.ChipHorizontalMargin) * tabCount;
                var usable = stripWidth - TabChipMetrics.StripHorizontalInsets;
                Assert.True(
                    required <= usable + 0.001,
                    $"{tabCount} 个标签在 {stripWidth} 宽的标签栏里需要 {required}，超过了可用宽度 {usable}。");
            }
        }
    }

    [Fact]
    public void CalculateChipWidth_IsUsableBeforeTheFirstLayoutPass()
    {
        // 首帧还没拿到实际宽度时先按最小宽度渲染，避免出现 0 宽或 NaN。
        Assert.Equal(TabChipMetrics.MinimumChipWidth, TabChipMetrics.CalculateChipWidth(0, 3));
        Assert.Equal(TabChipMetrics.MinimumChipWidth, TabChipMetrics.CalculateChipWidth(double.NaN, 3));
        Assert.Equal(TabChipMetrics.MinimumChipWidth, TabChipMetrics.CalculateChipWidth(223, 0));
    }

    [Fact]
    public void CalculateChipWidth_NeverGrowsWithMoreTabs()
    {
        var previous = double.PositiveInfinity;
        for (var count = 1; count <= 12; count++)
        {
            var width = TabChipMetrics.CalculateChipWidth(223, count);
            Assert.True(
                width <= previous,
                $"标签数 {count} 时宽度 {width} 不应大于标签数 {count - 1} 时的 {previous}。");
            previous = width;
            Assert.InRange(
                width,
                TabChipMetrics.MinimumChipWidth,
                TabChipMetrics.MaximumChipWidth);
        }
    }
}
