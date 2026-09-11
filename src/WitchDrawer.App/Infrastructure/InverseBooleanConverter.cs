using System.Globalization;
using System.Windows.Data;

namespace WitchDrawer.App.Infrastructure;

/// <summary>布尔取反（bool → !bool），用于把"是宿主自身标签"翻转为"可拆出"。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is not bool flag || !flag;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
