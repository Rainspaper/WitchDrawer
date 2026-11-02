using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WitchDrawer.App.Infrastructure;
using WitchDrawer.App.ViewModels;
using WitchDrawer.Native.Windows;

namespace WitchDrawer.App.Views;

public partial class QuickPanelWindow : Window
{
    private bool _forceClose;
    private IDisposable? _outsideInputMonitor;

    public QuickPanelWindow(QuickPanelViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        DpiChanged += OnDpiChanged;
        Deactivated += OnDeactivated;
        IsVisibleChanged += OnIsVisibleChanged;
        AppThemeManager.ThemeChanged += OnThemeChanged;
    }

    public QuickPanelViewModel ViewModel => (QuickPanelViewModel)DataContext;

    public async Task ToggleAsync()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            StopOutsideInputMonitor();
            return;
        }

        await ViewModel.LoadAsync();
        Show();
        WindowMotion.PopIn(this, 0.97, 130);
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        StartOutsideInputMonitor();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    /// <summary>
    /// 点击面板外部即退出。面板是 Topmost 且已激活，点别的窗口会先触发
    /// Window.Deactivated；但点桌面/任务栏这类"不激活任何窗口"的区域不会，
    /// 所以这里复用 TransientMenuWindow 的低层鼠标钩（零轮询、按钮按下才回调）。
    /// </summary>
    private void StartOutsideInputMonitor()
    {
        _outsideInputMonitor?.Dispose();
        _outsideInputMonitor = null;

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        try
        {
            _outsideInputMonitor = TransientMenuWindow.DismissOnOutsideInput(
                handle,
                () => Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() =>
                    {
                        if (IsVisible)
                        {
                            Hide();
                        }
                    })));
        }
        catch (Exception exception)
        {
            // 钩子装不上不影响面板本身：退化为"失焦即隐藏"（见 OnDeactivated）。
            System.Diagnostics.Debug.WriteLine($"Quick panel outside-input hook failed: {exception.Message}");
        }
    }

    private void StopOutsideInputMonitor()
    {
        _outsideInputMonitor?.Dispose();
        _outsideInputMonitor = null;
    }

    /// <summary>
    /// 面板一隐藏就释放低层钩子：钩子是全局的，留着会在面板不可见时继续吃
    /// 每次鼠标按下（无谓开销），也会在下次显示前重复挂钩。
    /// </summary>
    private void OnIsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            StopOutsideInputMonitor();
        }
    }

    /// <summary>失焦即退出（点其它窗口时走这条更轻的路径）。</summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        StopOutsideInputMonitor();
        if (IsVisible)
        {
            Hide();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        IsVisibleChanged -= OnIsVisibleChanged;
        Deactivated -= OnDeactivated;
        StopOutsideInputMonitor();
        DpiChanged -= OnDpiChanged;
        AppThemeManager.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateIconDisplayMetrics(VisualTreeHelper.GetDpi(this));
        AppThemeManager.ApplyToWindow(this);
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        UpdateIconDisplayMetrics(e.NewDpi);
    }

    private void UpdateIconDisplayMetrics(DpiScale dpi)
    {
        ViewModel.UpdateIconDisplayMetrics(dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        AppThemeManager.ApplyToWindow(this);
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void OnItemsMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            var item = ItemsControl.ContainerFromElement((ItemsControl)sender, source) as FrameworkElement;
            if (item?.DataContext is DrawerItemViewModel drawerItem)
            {
                await ViewModel.OpenItemCommand.ExecuteAsync(drawerItem);
                Hide();
            }
        }
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            var item = QuickItems.SelectedItem as DrawerItemViewModel;
            if (item is null && QuickItems.Items.Count > 0)
            {
                item = QuickItems.Items[0] as DrawerItemViewModel;
            }
            if (item is not null)
            {
                await ViewModel.OpenItemCommand.ExecuteAsync(item);
                Hide();
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Down)
        {
            if (QuickItems.Items.Count > 0)
            {
                int nextIndex = QuickItems.SelectedIndex + 1;
                if (nextIndex >= QuickItems.Items.Count)
                {
                    nextIndex = 0;
                }
                QuickItems.SelectedIndex = nextIndex;
                QuickItems.ScrollIntoView(QuickItems.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (QuickItems.Items.Count > 0)
            {
                int prevIndex = QuickItems.SelectedIndex - 1;
                if (prevIndex < 0)
                {
                    prevIndex = QuickItems.Items.Count - 1;
                }
                QuickItems.SelectedIndex = prevIndex;
                QuickItems.ScrollIntoView(QuickItems.SelectedItem);
            }
            e.Handled = true;
        }
    }
}
