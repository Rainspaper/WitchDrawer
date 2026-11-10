using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 静默自启（--silent）不调用 Window.Show，WPF 就不会创建 HWND，OnSourceInitialized
/// 不触发，全局热键也就没注册。MainWindow.EnsureHotKeysReady 用 EnsureHandle 补这一步。
/// 这些用例锁住该机制依赖的两个 WPF 事实，避免以后被"顺手简化"掉。
/// </summary>
public sealed class BackgroundStartupHotKeyTests
{
    private const int TestHotKeyId = 0x5744;
    private const uint TestModifiers = 0x000F; // Win | Ctrl | Alt | Shift
    private const uint TestVirtualKey = 0x87;  // F24：几乎不可能与真实软件冲突

    [Fact]
    public void EnsureHandle_CreatesInvisibleWindow_AndRaisesSourceInitialized()
    {
        RunOnStaThread(() =>
        {
            var window = CreateMainWindowLikeWindow();
            var sourceInitializedCount = 0;
            window.SourceInitialized += (_, _) => sourceInitializedCount++;

            Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);

            var handle = new WindowInteropHelper(window).EnsureHandle();

            Assert.NotEqual(nint.Zero, handle);
            Assert.Equal(1, sourceInitializedCount);
            Assert.NotNull(HwndSource.FromHwnd(handle));
            // 关键：HWND 存在，但窗口不可见——后台启动不会闪一下主窗口。
            Assert.False(window.IsVisible);
            Assert.False(IsWindowVisible(handle));
        });
    }

    [Fact]
    public void HotKeyRegisteredOnEnsuredHandle_SurvivesHide_AndIsUsableWhileHidden()
    {
        RunOnStaThread(() =>
        {
            var window = CreateMainWindowLikeWindow();
            var handle = new WindowInteropHelper(window).EnsureHandle();

            Assert.True(RegisterHotKey(handle, TestHotKeyId, TestModifiers, TestVirtualKey));

            // 静默启动紧接着就是 MinimizeToTray() -> Hide()：HWND 必须留着，热键才不掉。
            window.Hide();
            Assert.False(window.IsVisible);
            Assert.True(IsWindow(handle));
            Assert.False(IsWindowVisible(handle));
            Assert.Equal(handle, new WindowInteropHelper(window).Handle);

            // 隐藏期间解注册再注册必须仍然成功：说明句柄有效、热键通道可用。
            Assert.True(UnregisterHotKey(handle, TestHotKeyId));
            Assert.True(RegisterHotKey(handle, TestHotKeyId, TestModifiers, TestVirtualKey));
            Assert.True(UnregisterHotKey(handle, TestHotKeyId));

            // 之后用户手动打开主窗口：复用同一个 HWND，不会重建。
            window.Show();
            Assert.Equal(handle, new WindowInteropHelper(window).Handle);
            window.Hide();
        });
    }

    /// <summary>复刻 MainWindow.xaml 里影响 HWND 创建的那几项设置。</summary>
    private static Window CreateMainWindowLikeWindow()
    {
        return new Window
        {
            Title = "WitchDrawer",
            Width = 1000,
            Height = 620,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
        };
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (failure is not null)
        {
            throw new InvalidOperationException("STA 线程上的 WPF 断言失败。", failure);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}