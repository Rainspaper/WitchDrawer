namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 标签卡在「宿主盒子宽度」内的取宽方案：等分自适应。
/// <list type="number">
/// <item>取标签栏可用宽度，按标签数等分（每个标签再扣掉自身左右外边距）。</item>
/// <item>结果夹在 <see cref="MinimumChipWidth"/> 与 <see cref="MaximumChipWidth"/> 之间：
/// 标签少时不让单个标签拉满整行；盒子变窄时标签跟着一起缩，保证整排标签始终放得下、
/// 不会出现"盒子缩小后标签被截掉"。</item>
/// </list>
/// 下限只是防御性的硬底（避免宽度退化成 0/负数）；到这个宽度文字已截成一两个字符，
/// 靠 ToolTip 显示完整名字。只有盒子窄到这个极限时才会溢出为横向滚动。
/// </summary>
internal static class TabChipMetrics
{
    /// <summary>标签条左右外边距之和，与 XAML 里 ItemsControl 的 Margin="8,0,8,0" 对应。</summary>
    internal const double StripHorizontalInsets = 16;

    /// <summary>每个标签左右外边距之和，与样式中 Margin="1,0" 对应。</summary>
    internal const double ChipHorizontalMargin = 2;

    /// <summary>
    /// 硬下限：只用于避免宽度退化为 0/负数。到这个宽度标签只剩一两个字符可读
    /// （完整名字看 ToolTip），换来的是整排标签永远放得下。
    /// </summary>
    internal const double MinimumChipWidth = 16;

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
