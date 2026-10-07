using System.Windows;
using WitchDrawer.App.ViewModels;

namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 把所属盒子的 <see cref="DesktopBoxLayoutSettings"/> 以资源形式提供给 DataTemplate，
/// 用来替代 <c>{Binding DataContext.LayoutSettings.X, RelativeSource={RelativeSource AncestorType=ListBox}}</c>。
///
/// 为什么需要它：<c>FileList</c> / <c>IconList</c> 使用
/// <c>VirtualizingPanel.VirtualizationMode=Recycling</c>。容器被回收复用、或在尚未挂载时求值，
/// <c>FindAncestor</c> 取不到祖先便退回 <c>NaN</c>，<c>Image</c> 于是按位图的原始像素尺寸铺开
/// （实测渲染成正常值的 5–6 倍，切换标签 / 缩放盒子触发重新布局后才恢复）。
///
/// 这个 <see cref="Freezable"/> 放在 <c>Window.Resources</c> 中，其数据上下文继承自窗口
/// （资源字典的所有者），而不是取决于元素当时是否挂在可视树上，因此容器回收/复用不会让它失效。
/// </summary>
public sealed class BoxLayoutProxy : Freezable
{
    public static readonly DependencyProperty LayoutSettingsProperty =
        DependencyProperty.Register(
            nameof(LayoutSettings),
            typeof(DesktopBoxLayoutSettings),
            typeof(BoxLayoutProxy),
            new PropertyMetadata(null));

    public DesktopBoxLayoutSettings? LayoutSettings
    {
        get => (DesktopBoxLayoutSettings?)GetValue(LayoutSettingsProperty);
        set => SetValue(LayoutSettingsProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BoxLayoutProxy();
}
