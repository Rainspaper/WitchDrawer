using System.Globalization;
using System.Windows.Data;

namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 标签卡宽度 = 标签栏可用宽度 ÷ 标签数（夹在上下限之间），见 <see cref="TabChipMetrics"/>。
/// 多值绑定输入依次为：标签栏实际宽度、标签数量。
/// </summary>
public sealed class TabChipWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var stripWidth = values.Length > 0 && values[0] is double width ? width : 0;
        var tabCount = values.Length > 1 && values[1] is int count ? count : 0;
        return TabChipMetrics.CalculateChipWidth(stripWidth, tabCount);
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
