using System;
using System.Collections.Generic;
using System.Windows;

namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 一条对齐虚线（物理像素）。竖线：<see cref="Position"/> 是 x，Start/End 是 y；
/// 横线：<see cref="Position"/> 是 y，Start/End 是 x。
/// </summary>
internal readonly record struct SnapGuideLine(double Position, double Start, double End);

/// <summary>
/// 拖动吸附结果。某个轴的 Snapped* 为 null 表示该轴没有命中任何候选。
/// </summary>
internal readonly record struct DesktopBoxSnapResult(
    double? SnappedLeft,
    double? SnappedTop,
    IReadOnlyList<SnapGuideLine> VerticalGuides,
    IReadOnlyList<SnapGuideLine> HorizontalGuides)
{
    public static DesktopBoxSnapResult Empty { get; } =
        new(null, null, Array.Empty<SnapGuideLine>(), Array.Empty<SnapGuideLine>());

    public bool HasSnap => SnappedLeft.HasValue || SnappedTop.HasValue;
}

/// <summary>
/// 收纳盒拖动吸附/对齐的纯计算：输入被拖盒子与其它盒子的可见边界（物理像素）、
/// 所在工作区、阈值与间距，输出吸附后的位置和要画的虚线。
/// <para>
/// 对齐候选优先级（同距离时数值小的先赢）：
/// 1 = 对边对齐（左/左、上/上，含工作区边缘）；2 = 反向对边（右/右、下/下，含工作区边缘）；
/// 3 = 零间距紧贴；4 = 留缝紧贴（间距 <c>gap</c>）；5 = 中心线；
/// 6 = 工作区中心线。
/// </para>
/// <para>
/// 紧贴类候选（3/4）只有在两个盒子沿另一轴「相邻」时才参与，避免屏幕上相隔很远的两个盒子
/// 在横拖时莫名被吸住、并画出一条贯穿屏幕的虚线。
/// </para>
/// </summary>
internal static class DesktopBoxSnapCalculator
{
    internal const double DefaultThresholdDip = 10.0;

    /// <summary>上下/左右并排时默认留出的间距（DIP）。</summary>
    internal const double DefaultGapDip = 8.0;

    private const int PriorityEdgeAlign = 1;
    private const int PriorityEdgeAlignOpposite = 2;
    private const int PriorityFlush = 3;
    private const int PriorityGap = 4;
    private const int PriorityCenter = 5;
    private const int PriorityWorkAreaCenter = 6;
    private const double GuideMergeTolerance = 0.5;

    public static DesktopBoxSnapResult Compute(
        double left,
        double top,
        double width,
        double height,
        IReadOnlyList<Rect> others,
        Rect workArea,
        double threshold,
        double gap)
    {
        if (!(width > 0) || !(height > 0) || !(threshold > 0))
        {
            return DesktopBoxSnapResult.Empty;
        }

        gap = Math.Max(0, gap);
        // 紧贴类候选的"相邻"容差：容器间隙本身 + 一个吸附阈值。
        var adjacency = gap + threshold;
        var right = left + width;
        var bottom = top + height;

        var horizontalCandidates = new List<AxisCandidate>();
        var verticalCandidates = new List<AxisCandidate>();

        foreach (var other in others)
        {
            if (other.IsEmpty || !(other.Width > 0) || !(other.Height > 0))
            {
                continue;
            }

            var otherLeft = other.Left;
            var otherTop = other.Top;
            var otherRight = other.Right;
            var otherBottom = other.Bottom;

            // 并排（水平方向紧贴）要求两者在垂直方向上相邻；上下叠放同理。
            var sideBySide = GapAlongAxis(top, bottom, otherTop, otherBottom) <= adjacency;
            var stacked = GapAlongAxis(left, right, otherLeft, otherRight) <= adjacency;

            // X 轴候选
            horizontalCandidates.Add(new AxisCandidate(
                otherLeft, PriorityEdgeAlign, otherLeft, otherTop, otherBottom));
            horizontalCandidates.Add(new AxisCandidate(
                otherRight - width, PriorityEdgeAlignOpposite, otherRight, otherTop, otherBottom));
            if (sideBySide)
            {
                horizontalCandidates.Add(new AxisCandidate(
                    otherRight, PriorityFlush, otherRight, otherTop, otherBottom));
                horizontalCandidates.Add(new AxisCandidate(
                    otherLeft - width, PriorityFlush, otherLeft, otherTop, otherBottom));
                if (gap > 0)
                {
                    horizontalCandidates.Add(new AxisCandidate(
                        otherRight + gap, PriorityGap, otherRight + (gap / 2), otherTop, otherBottom));
                    horizontalCandidates.Add(new AxisCandidate(
                        otherLeft - width - gap, PriorityGap, otherLeft - (gap / 2), otherTop, otherBottom));
                }
            }

            horizontalCandidates.Add(new AxisCandidate(
                otherLeft + ((other.Width - width) / 2),
                PriorityCenter,
                otherLeft + (other.Width / 2),
                otherTop,
                otherBottom));

            // Y 轴候选
            verticalCandidates.Add(new AxisCandidate(
                otherTop, PriorityEdgeAlign, otherTop, otherLeft, otherRight));
            verticalCandidates.Add(new AxisCandidate(
                otherBottom - height, PriorityEdgeAlignOpposite, otherBottom, otherLeft, otherRight));
            if (stacked)
            {
                verticalCandidates.Add(new AxisCandidate(
                    otherBottom, PriorityFlush, otherBottom, otherLeft, otherRight));
                verticalCandidates.Add(new AxisCandidate(
                    otherTop - height, PriorityFlush, otherTop, otherLeft, otherRight));
                if (gap > 0)
                {
                    verticalCandidates.Add(new AxisCandidate(
                        otherBottom + gap, PriorityGap, otherBottom + (gap / 2), otherLeft, otherRight));
                    verticalCandidates.Add(new AxisCandidate(
                        otherTop - height - gap, PriorityGap, otherTop - (gap / 2), otherLeft, otherRight));
                }
            }

            verticalCandidates.Add(new AxisCandidate(
                otherTop + ((other.Height - height) / 2),
                PriorityCenter,
                otherTop + (other.Height / 2),
                otherLeft,
                otherRight));
        }

        if (!workArea.IsEmpty && workArea.Width > 0 && workArea.Height > 0)
        {
            horizontalCandidates.Add(new AxisCandidate(
                workArea.Left, PriorityEdgeAlign, workArea.Left, workArea.Top, workArea.Bottom));
            horizontalCandidates.Add(new AxisCandidate(
                workArea.Right - width, PriorityEdgeAlignOpposite, workArea.Right, workArea.Top, workArea.Bottom));
            horizontalCandidates.Add(new AxisCandidate(
                workArea.Left + ((workArea.Width - width) / 2),
                PriorityWorkAreaCenter,
                workArea.Left + (workArea.Width / 2),
                workArea.Top,
                workArea.Bottom));

            verticalCandidates.Add(new AxisCandidate(
                workArea.Top, PriorityEdgeAlign, workArea.Top, workArea.Left, workArea.Right));
            verticalCandidates.Add(new AxisCandidate(
                workArea.Bottom - height, PriorityEdgeAlignOpposite, workArea.Bottom, workArea.Left, workArea.Right));
            verticalCandidates.Add(new AxisCandidate(
                workArea.Top + ((workArea.Height - height) / 2),
                PriorityWorkAreaCenter,
                workArea.Top + (workArea.Height / 2),
                workArea.Left,
                workArea.Right));
        }

        var snappedLeft = SelectBestTarget(horizontalCandidates, left, threshold);
        var snappedTop = SelectBestTarget(verticalCandidates, top, threshold);

        var verticalGuides = BuildGuides(
            horizontalCandidates,
            snappedLeft,
            top,
            top + height);
        var horizontalGuides = BuildGuides(
            verticalCandidates,
            snappedTop,
            left,
            left + width);

        return new DesktopBoxSnapResult(snappedLeft, snappedTop, verticalGuides, horizontalGuides);
    }

    /// <summary>两个区间之间的间隙；重叠时返回 0。</summary>
    internal static double GapAlongAxis(double startA, double endA, double startB, double endB)
    {
        return Math.Max(0, Math.Max(startB - endA, startA - endB));
    }

    private static double? SelectBestTarget(
        IReadOnlyList<AxisCandidate> candidates,
        double current,
        double threshold)
    {
        double? bestTarget = null;
        var bestPriority = int.MaxValue;
        var bestDistance = double.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(candidate.Target - current);
            if (distance > threshold)
            {
                continue;
            }

            var closer = distance < bestDistance - 0.0001;
            var tiesWithBetterPriority = Math.Abs(distance - bestDistance) <= 0.0001
                && candidate.Priority < bestPriority;
            if (!bestTarget.HasValue || closer || tiesWithBetterPriority)
            {
                bestTarget = candidate.Target;
                bestPriority = candidate.Priority;
                bestDistance = distance;
            }
        }

        return bestTarget;
    }

    /// <summary>
    /// 只为「真正命中的那个目标位置」画虚线：命中后同一位置上的所有候选合并成一条，
    /// 长度覆盖被拖盒子与相关盒子的并集。没有命中就一条都不画。
    /// </summary>
    private static List<SnapGuideLine> BuildGuides(
        IReadOnlyList<AxisCandidate> candidates,
        double? target,
        double currentStart,
        double currentEnd)
    {
        var guides = new List<SnapGuideLine>();
        if (target is not double chosen)
        {
            return guides;
        }

        // 只画"赢下这次吸附的那个优先级"的虚线：两个等宽盒子对齐时，
        // 左缘/右缘/中心线会同时满足，全都画出来就是三条重影线——只留最高优先级的那条。
        var bestPriority = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (Math.Abs(candidate.Target - chosen) <= GuideMergeTolerance
                && candidate.Priority < bestPriority)
            {
                bestPriority = candidate.Priority;
            }
        }

        foreach (var candidate in candidates)
        {
            if (Math.Abs(candidate.Target - chosen) > GuideMergeTolerance
                || candidate.Priority != bestPriority)
            {
                continue;
            }

            AddGuide(
                guides,
                candidate.GuidePosition,
                Math.Min(currentStart, candidate.OtherStart),
                Math.Max(currentEnd, candidate.OtherEnd));
        }

        return guides;
    }

    private static void AddGuide(List<SnapGuideLine> guides, double position, double start, double end)
    {
        for (var index = 0; index < guides.Count; index++)
        {
            var existing = guides[index];
            if (Math.Abs(existing.Position - position) > GuideMergeTolerance)
            {
                continue;
            }

            guides[index] = existing with
            {
                Start = Math.Min(existing.Start, start),
                End = Math.Max(existing.End, end)
            };
            return;
        }

        guides.Add(new SnapGuideLine(position, Math.Min(start, end), Math.Max(start, end)));
    }

    private readonly record struct AxisCandidate(
        double Target,
        int Priority,
        double GuidePosition,
        double OtherStart,
        double OtherEnd);
}
