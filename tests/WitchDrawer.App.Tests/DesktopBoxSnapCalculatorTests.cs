using System.Windows;
using WitchDrawer.App.Infrastructure;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 锁定收纳盒拖动吸附/对齐的判定规则（纯函数 <see cref="DesktopBoxSnapCalculator"/>）：
/// 对边对齐优先于紧贴、紧贴分零间距与留缝两档、紧贴只对"相邻"的盒子生效、
/// 工作区边缘也能吸、虚线只为真正命中的目标绘制。
/// </summary>
public sealed class DesktopBoxSnapCalculatorTests
{
    private const double Threshold = 10;
    private const double Gap = 8;
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);

    private static DesktopBoxSnapResult Compute(
        Rect dragged,
        params Rect[] others) =>
        DesktopBoxSnapCalculator.Compute(
            dragged.Left,
            dragged.Top,
            dragged.Width,
            dragged.Height,
            others,
            WorkArea,
            Threshold,
            Gap);

    [Fact]
    public void FarFromEverything_DoesNotSnapOrDrawGuides()
    {
        var others = new[] { new Rect(900, 600, 200, 150) };
        var dragged = new Rect(200, 200, 200, 150);

        var result = Compute(dragged, others);

        Assert.False(result.HasSnap);
        Assert.Empty(result.VerticalGuides);
        Assert.Empty(result.HorizontalGuides);
    }

    [Fact]
    public void NearLeftEdge_AlignsLeftEdges()
    {
        var others = new[] { new Rect(300, 100, 200, 150) };
        var dragged = new Rect(296, 400, 200, 150);

        var result = Compute(dragged, others);

        Assert.Equal(300, result.SnappedLeft!.Value, 3);
        Assert.Single(result.VerticalGuides);
        Assert.Equal(300, result.VerticalGuides[0].Position, 3);
    }

    [Fact]
    public void NearRightEdges_AlignsRightEdges()
    {
        var others = new[] { new Rect(300, 100, 200, 150) };
        // 右缘 703 距对方右缘 500 太远，改为贴近后让右缘对齐：left = 500-200+4
        var dragged = new Rect(304, 400, 200, 150);

        var result = Compute(dragged, others);

        Assert.Equal(300, result.SnappedLeft!.Value, 3);
    }

    [Fact]
    public void SideBySideNeighbour_SnapsFlushWithoutGap()
    {
        var others = new[] { new Rect(100, 100, 200, 150) };
        // 垂直方向重叠（同一行），水平方向想贴在右边：当前 left 距 300 有 2px，
        // 而距"留缝"位置 308 有 6px —— 零间距紧贴是更近的候选。
        var dragged = new Rect(302, 120, 200, 150);

        var result = Compute(dragged, others);

        Assert.Equal(300, result.SnappedLeft!.Value, 3);
    }

    [Fact]
    public void SideBySideNeighbour_CanKeepTheVisualGap()
    {
        var others = new[] { new Rect(100, 100, 200, 150) };
        // 想留 8px 缝：left = 300 + 8 = 308
        var dragged = new Rect(307, 120, 200, 150);

        var result = Compute(dragged, others);

        Assert.Equal(308, result.SnappedLeft!.Value, 3);
    }

    [Fact]
    public void StackedNeighbour_SnapsFlushBelowWithoutGap()
    {
        var others = new[] { new Rect(100, 100, 200, 150) };
        // 水平方向重叠，垂直方向想贴在下面：top = 250
        var dragged = new Rect(120, 254, 200, 150);

        var result = Compute(dragged, others);

        Assert.Equal(250, result.SnappedTop!.Value, 3);
    }

    [Fact]
    public void StackedNeighbour_CanKeepTheVisualGap()
    {
        var others = new[] { new Rect(100, 100, 200, 150) };
        // top = 250 + 8 = 258
        var dragged = new Rect(120, 257, 200, 150);

        var result = Compute(dragged, others);

        Assert.Equal(258, result.SnappedTop!.Value, 3);
    }

    [Fact]
    public void FarApartBoxes_DoNotSnapSideBySide()
    {
        // 上一个盒子在屏幕顶部，被拖盒子在很下方且水平位置正好落在"紧贴右边"的距离内：
        // 因为两盒垂直方向相距很远，紧贴候选必须被排除（否则会莫名跳一下 + 画出贯穿屏的虚线）。
        var others = new[] { new Rect(100, 100, 200, 150) };
        var dragged = new Rect(306, 800, 200, 150);

        var result = Compute(dragged, others);

        Assert.Null(result.SnappedLeft);
        Assert.Empty(result.VerticalGuides);
    }

    [Fact]
    public void NearWorkAreaLeftEdge_SnapsToTheWorkArea()
    {
        // 没有任何其它盒子：仍然要能贴住工作区左边缘。
        var dragged = new Rect(5, 400, 200, 150);

        var result = Compute(dragged);

        Assert.Equal(0, result.SnappedLeft!.Value, 3);
        Assert.Single(result.VerticalGuides);
        Assert.Equal(0, result.VerticalGuides[0].Position, 3);
    }

    [Fact]
    public void NearWorkAreaRightAndBottom_ClampsToTheEdges()
    {
        var dragged = new Rect(1920 - 200 - 4, 1040 - 150 - 6, 200, 150);

        var result = Compute(dragged);

        Assert.Equal(1720, result.SnappedLeft!.Value, 3);
        Assert.Equal(890, result.SnappedTop!.Value, 3);
    }

    [Fact]
    public void EdgeAlignmentWinsOverFlushAtTheSameDistance()
    {
        // 构造两个盒子：一个的左缘正好在 306（对边对齐距离 0），另一个的右缘 306 也在紧贴位置。
        // 优先级 1（对边对齐）必须赢。
        var alignTarget = new Rect(306, 100, 200, 150);
        var flushTarget = new Rect(106, 100, 200, 150);
        var dragged = new Rect(306, 120, 200, 150);

        var result = Compute(dragged, alignTarget, flushTarget);

        Assert.Equal(306, result.SnappedLeft!.Value, 3);
        Assert.Single(result.VerticalGuides);
    }

    [Fact]
    public void CentersAlignWhenEdgeCandidatesAreOutOfRange()
    {
        var others = new[] { new Rect(400, 100, 300, 150) };
        // 被拖盒子宽 200，居中位置 left = 400 + (300-200)/2 = 450
        var dragged = new Rect(453, 600, 200, 150);

        var result = Compute(dragged, others);

        Assert.Equal(450, result.SnappedLeft!.Value, 3);
    }

    [Fact]
    public void GuidesOnlyCoverTheBoxesThatActuallyMatch()
    {
        var matching = new Rect(300, 100, 200, 150);
        var unrelated = new Rect(900, 700, 120, 90);
        var dragged = new Rect(297, 400, 200, 150);

        var result = Compute(dragged, matching, unrelated);

        var guide = Assert.Single(result.VerticalGuides);
        Assert.Equal(300, guide.Position, 3);
        // 虚线的纵向范围只覆盖被拖盒子与命中的那个盒子，不应延伸到无关盒子。
        Assert.Equal(100, guide.Start, 3);
        Assert.Equal(550, guide.End, 3);
    }

    [Theory]
    // 左侧越界：无论越界多少，左缘贴到工作区左边缘
    [InlineData(-6.0, 400.0, 0.0, null)]
    [InlineData(-120.0, 400.0, 0.0, null)]
    // 右侧越界：右缘贴到工作区右边缘（left = 1920 - 200）
    [InlineData(1730.0, 400.0, 1720.0, null)]
    // 上方越界
    [InlineData(400.0, -5.0, null, 0.0)]
    // 下方越界（top = 1040 - 150）
    [InlineData(400.0, 899.0, null, 890.0)]
    public void BoxOutsideWorkArea_IsPushedBackToTheScreenEdge(
        double left,
        double top,
        double? expectedLeft,
        double? expectedTop)
    {
        var result = Compute(new Rect(left, top, 200, 150));

        Assert.Equal(expectedLeft, result.SnappedLeft);
        Assert.Equal(expectedTop, result.SnappedTop);
    }

    [Fact]
    public void BoxOutsideWorkArea_StillPulledInWhenFarBeyondTheThreshold()
    {
        // 越界很远（超过吸附阈值 10）也必须被拉回来：这是硬约束。
        var dragged = new Rect(-500, 400, 200, 150);

        var result = Compute(dragged);

        Assert.Equal(0, result.SnappedLeft!.Value, 3);
        // 归边时也画一条贴着屏幕边缘的虚线
        var guide = Assert.Single(result.VerticalGuides);
        Assert.Equal(0, guide.Position, 3);
    }

    [Fact]
    public void BoxInsideWorkArea_StaysUnsappedWhenFarFromEverything()
    {
        var dragged = new Rect(600, 400, 200, 150);

        var result = Compute(dragged);

        Assert.Null(result.SnappedLeft);
        Assert.Null(result.SnappedTop);
        Assert.Empty(result.VerticalGuides);
        Assert.Empty(result.HorizontalGuides);
    }

    [Theory]
    [InlineData(0, 100, 100, 200, 0)]
    [InlineData(100, 300, 300, 400, 0)]
    [InlineData(0, 100, 200, 300, 100)]
    public void GapAlongAxis_ReportsOnlyRealGaps(
        double startA,
        double endA,
        double startB,
        double endB,
        double expected)
    {
        Assert.Equal(
            expected,
            DesktopBoxSnapCalculator.GapAlongAxis(startA, endA, startB, endB),
            3);
    }
}
