using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 条目/图标尺寸的取值来源必须是「不依赖可视树祖先」的绑定。
///
/// 背景：这些绑定曾写成
///   {Binding DataContext.LayoutSettings.XXX, RelativeSource={RelativeSource AncestorType={x:Type ListBox}}}
/// 而 FileList 开了 VirtualizationPanel.VirtualizationMode=Recycling。容器被回收复用、
/// 或在尚未挂载时求值，FindAncestor 取不到祖先就退回 NaN，Image 便按位图的原始像素尺寸
/// 铺开——实测渲染成正常值的 5-6 倍（130-175px vs 26-28px），切换标签 / 缩放盒子触发
/// 重新布局后才恢复。改为引用窗口级 BoxLayoutProxy 资源后不再依赖祖先可达性。
/// </summary>
public sealed class DesktopBoxIconSizeBindingTests
{
    [Fact]
    public void LayoutSettingsBindings_MustNotUseListOrItemsAncestor()
    {
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());

        // 只覆盖 ListBox / ItemsControl 两类祖先：它们出现在开了虚拟化的列表模板里，
        // 容器回收复用时会取不到祖先。AncestorType=Window 的绑定（抽屉弹窗那几处）
        // 不在本次修复范围，另行评估——Popup 的可视树与 Window 分离，若确认同样失效
        // 再一并接进 BoxLayoutProxy。
        var offenders = document
            .Descendants()
            .Attributes()
            .Where(attribute =>
                attribute.Value.Contains("LayoutSettings.", StringComparison.Ordinal)
                && (attribute.Value.Contains(
                        "AncestorType={x:Type ListBox}",
                        StringComparison.Ordinal)
                    || attribute.Value.Contains(
                        "AncestorType={x:Type ItemsControl}",
                        StringComparison.Ordinal)))
            .Select(attribute =>
                $"{attribute.Parent!.Name.LocalName}.{attribute.Name.LocalName} = {attribute.Value}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void LayoutSettingsBindings_ReadFromTheWindowLevelProxy()
    {
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());

        var proxyReferences = document
            .Descendants()
            .Attributes()
            .Count(attribute => attribute.Value.Contains(
                "Source={StaticResource BoxLayoutProxy}",
                StringComparison.Ordinal));

        Assert.True(
            proxyReferences > 0,
            "尺寸类绑定应当引用窗口级 BoxLayoutProxy 资源。");
    }

    private static string GetDesktopBoxWindowXamlPath() =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "WitchDrawer.App",
                "Views",
                "DesktopBoxWindow.xaml"));
}
