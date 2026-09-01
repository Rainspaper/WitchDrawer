using System.Runtime.InteropServices;
using System.Text;

namespace WitchDrawer.Native.Windows;

/// <summary>
/// Reports system-wide foreground window changes without polling.
/// </summary>
public sealed class ForegroundWindowMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;

    private readonly WinEventCallback _callback;
    private nint _hook;

    public ForegroundWindowMonitor()
    {
        _callback = OnWinEvent;
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            _callback,
            0,
            0,
            WinEventOutOfContext);
    }

    public event Action<nint>? ForegroundWindowChanged;

    public bool IsActive => _hook != nint.Zero;

    private const uint GetWindowOwner = 4;

    public static nint GetCurrentForegroundWindow()
    {
        return GetForegroundWindow();
    }

    public static bool IsDesktopWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return false;
        }

        var className = new StringBuilder(64);
        return GetClassNameW(windowHandle, className, className.Capacity) > 0
            && IsDesktopWindowClass(className.ToString());
    }

    public static bool IsDesktopWindowClass(string? className)
    {
        return string.Equals(className, "Progman", StringComparison.Ordinal)
            || string.Equals(className, "WorkerW", StringComparison.Ordinal);
    }

    /// <summary>
    /// 判断一个前台窗口是否属于 Shell 桌面属主链（例如回收站确认弹窗）。
    /// 这类窗口与桌面盒共用 Progman 属主链，压盒子时可能连带拖动弹窗层级。
    /// </summary>
    public static bool IsWindowOwnedByShell(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return false;
        }

        var shellWindow = GetShellWindow();
        if (shellWindow == nint.Zero)
        {
            return false;
        }

        return GetWindow(windowHandle, GetWindowOwner) == shellWindow;
    }

    public void Dispose()
    {
        if (_hook == nint.Zero)
        {
            return;
        }

        UnhookWinEvent(_hook);
        _hook = nint.Zero;
        GC.SuppressFinalize(this);
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        try
        {
            ForegroundWindowChanged?.Invoke(windowHandle);
        }
        catch
        {
            // Exceptions must never escape a native callback boundary.
        }
    }

    private delegate void WinEventCallback(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(
        nint windowHandle,
        StringBuilder className,
        int maximumCount);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);
}
