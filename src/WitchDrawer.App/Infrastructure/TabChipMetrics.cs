namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 标签卡在「宿主盒子宽度」内的取宽方案：等分自适应。
/// <list type="number">
/// <item>取标签栏可用宽度，按标签数等分（每个标签再扣掉自身左右外边距）。</item>
/// <item>结果夹在 <see cref="MinimumChipWidth"/> 与 <see cref="MaximumChipWidth"/> 之间：
/// 标签少时不让单个标签拉满整行，标签多时缩到最小宽度后交给横向滚动。</item>
/// </list>
/// 这样常规情况下所有标签正好排满一行、宽度整齐一致，不需要滚动；
/// 只有窄盒子塞很多标签时才会滚动，且滚动前已经尽量压缩过。
/// </summary>
internal static class TabChipMetrics
{
    /// <summary>标签条左右外边距之和，与 XAML 里 ItemsControl 的 Margin="8,0,8,0" 对应。</summary>
    internal const double StripHorizontalInsets = 16;

    /// <summary>每个标签左右外边距之和，与样式中 Margin="1,0" 对应。</summary>
    internal const double ChipHorizontalMargin = 2;

    /// <summary>再窄文字就读不出来了：到此为止不再压缩，剩下的靠横向滚动。</summary>
    internal const double MinimumChipWidth = 56;

    /// <summary>上限：标签很少时也不让单个标签拉满整行，保持"一排小标签"的观感。</summary>
    internal const double MaximumChipWidth = 120;

    internal static double CalculateChipWidth(double stripWidth, int tabCount)
    {
        if (tabCount <= 0 || double.IsNaN(stripWidth) || stripWidth <= 0)
        {
            // 首次布局尚未拿到实际宽度时先按最小宽度渲染，测量完成后绑定会自动刷新。
            return MinimumChipWidth;
        }

        var usableWidth = stripWidth - StripHorizontalInsets;
        var perTab = (usableWidth / tabCount) - ChipHorizontalMargin;
        return Math.Clamp(perTab, MinimumChipWidth, MaximumChipWidth);
    }
}
