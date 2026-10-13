using System.Runtime.InteropServices;
using System.Text;

namespace WitchDrawer.Native.Windows;

/// <summary>
/// 判定「占据整块显示器的全屏程序」（游戏、全屏视频、全屏浏览器等）。
///
/// 只用窗口属性与几何做判断，不发送任何全局消息、不注入任何进程，因此不会像
/// WM_SETTINGCHANGE 广播那样干扰第三方程序（详见 DesktopIconVisibility 里的教训）。
///
/// 判定条件：他进程的顶层窗口（无 owner）、可见、未最小化、未被 DWM cloaked、
/// 不是工具窗口、不是桌面/任务栏等 Shell 表面、不是最大化的普通窗口，且窗口矩形
/// 覆盖它所在显示器的完整区域（容差 2px）。
///
/// 任务栏可见时被最大化的普通窗口只覆盖工作区，几何判定本身就不会误判；任务栏在
/// 别的显示器上（或自动隐藏）时最大化的普通窗口会盖满整块显示器，于是再用"是否处于
/// 最大化状态"区分 —— Windows 会把带边框的最大化窗口撑到显示器外沿（矩形比
/// rcMonitor 大若干像素），而真正的全屏游戏/视频不会超出显示器。2026-10-08 实机验证：
/// 副屏上最大化的 Electron 窗口 rect=-7,-921 1477x928 比 rcMonitor(0,-914,1463,0)
/// 大 7px，若不排除，副屏上的盒子会被无谓隐藏。
/// </summary>
public static class FullscreenWindowDetector
{
    private const uint GetWindowOwner = 4;
    private const uint MonitorDefaultToNearest = 2;
    private const uint DwmWindowCloaked = 14;
    private const int ExtendedStyleIndex = -20;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const int StyleIndex = -16;
    private const long StyleCaption = 0x00C00000L;
    private const long StyleThickFrame = 0x00040000L;
    private const int GeometryTolerance = 2;

    /// <summary>
    /// 窗口是否占据整块显示器；是则同时给出它所在的显示器句柄。
    /// </summary>
    public static bool TryGetFullscreenMonitor(nint windowHandle, out nint monitorHandle)
    {
        monitorHandle = nint.Zero;
        if (!IsFullscreenAppWindow(windowHandle))
        {
            return false;
        }

        monitorHandle = GetMonitorHandle(windowHandle);
        return monitorHandle != nint.Zero;
    }

    /// <summary>
    /// 窗口是否占据整块显示器（不要求它是前台窗口）。句柄失效、窗口已销毁时返回 false。
    /// </summary>
    public static bool IsFullscreenAppWindow(nint windowHandle)
    {
        if (!IsCandidateWindow(windowHandle) || !GetWindowRect(windowHandle, out var windowBounds))
        {
            return false;
        }

        var monitorHandle = GetMonitorHandle(windowHandle);
        if (monitorHandle == nint.Zero)
        {
            return false;
        }

        var monitorInfo = new NativeMonitorInfo
        {
            Size = Marshal.SizeOf<NativeMonitorInfo>(),
        };
        return GetMonitorInfoW(monitorHandle, ref monitorInfo)
            && !IsMaximizedRegularWindow(
                IsZoomed(windowHandle),
                HasWindowFrame(windowHandle),
                windowBounds,
                monitorInfo.Monitor,
                GeometryTolerance)
            && CoversMonitor(windowBounds, monitorInfo.Monitor, GeometryTolerance);
    }

    /// <summary>窗口所在显示器的句柄（就近匹配，窗口跨屏时取面积最大的那块）。</summary>
    public static nint GetMonitorHandle(nint windowHandle)
    {
        return windowHandle == nint.Zero
            ? nint.Zero
            : MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
    }

    /// <summary>窗口是否属于当前进程（桌面盒、快捷面板、设置窗口等自家窗口）。</summary>
    public static bool IsOwnProcessWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// 窗口矩形是否覆盖显示器矩形。抽成纯函数，便于对边界与容差做单元测试。
    /// </summary>
    internal static bool CoversMonitor(
        NativeScreenRect windowBounds,
        NativeScreenRect monitorBounds,
        int tolerance)
    {
        return windowBounds.Left <= monitorBounds.Left + tolerance
            && windowBounds.Top <= monitorBounds.Top + tolerance
            && windowBounds.Right >= monitorBounds.Right - tolerance
            && windowBounds.Bottom >= monitorBounds.Bottom - tolerance;
    }

    /// <summary>
    /// 这是不是一个"最大化的普通窗口"（因此不算全屏程序）。抽成纯函数便于单测：
    /// 带边框的最大化窗口，或者矩形被撑到显示器之外的，都属于此类；无边框且恰好
    /// 与显示器等大的最大化窗口（部分无边框窗口化游戏）仍按全屏处理。
    /// </summary>
    internal static bool IsMaximizedRegularWindow(
        bool isMaximized,
        bool hasWindowFrame,
        NativeScreenRect windowBounds,
        NativeScreenRect monitorBounds,
        int tolerance)
    {
        return isMaximized
            && (hasWindowFrame || OverflowsMonitor(windowBounds, monitorBounds, tolerance));
    }

    private static bool OverflowsMonitor(
        NativeScreenRect windowBounds,
        NativeScreenRect monitorBounds,
        int tolerance)
    {
        return windowBounds.Left < monitorBounds.Left - tolerance
            || windowBounds.Top < monitorBounds.Top - tolerance
            || windowBounds.Right > monitorBounds.Right + tolerance
            || windowBounds.Bottom > monitorBounds.Bottom + tolerance;
    }

    private static bool HasWindowFrame(nint windowHandle)
    {
        var style = unchecked((long)(nuint)GetWindowLongPtr(windowHandle, StyleIndex));
        return (style & (StyleCaption | StyleThickFrame)) != 0;
    }

    private static bool IsCandidateWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero
            || windowHandle == GetShellWindow()
            || GetWindow(windowHandle, GetWindowOwner) != nint.Zero
            || !IsWindowVisible(windowHandle)
            || IsIconic(windowHandle)
            || IsOwnProcessWindow(windowHandle)
            || IsToolWindow(windowHandle)
            || IsCloaked(windowHandle))
        {
            return false;
        }

        return !IsShellSurfaceClass(GetClassName(windowHandle));
    }

    /// <summary>
    /// 桌面与任务栏本身同样铺满显示器，但它们不是"全屏程序"。
    /// </summary>
    private static bool IsShellSurfaceClass(string className)
    {
        return className is "Progman"
            or "WorkerW"
            or "Shell_TrayWnd"
            or "Shell_SecondaryTrayWnd"
            or "SHELLDLL_DefView"
            or "SysListView32";
    }

    private static bool IsToolWindow(nint windowHandle)
    {
        var extendedStyle = unchecked((long)(nuint)GetWindowLongPtr(windowHandle, ExtendedStyleIndex));
        return (extendedStyle & ExtendedStyleToolWindow) != 0;
    }

    private static bool IsCloaked(nint windowHandle)
    {
        // 挂起的 UWP 应用、位于其它虚拟桌面上的窗口会被 DWM 标记为 cloaked：
        // 窗口属性仍然是"可见"，但用户实际看不到，不能当成全屏程序。
        return DwmGetWindowAttribute(windowHandle, DwmWindowCloaked, out var cloaked, sizeof(int)) == 0
            && cloaked != 0;
    }

    private static string GetClassName(nint windowHandle)
    {
        var className = new StringBuilder(64);
        return GetClassNameW(windowHandle, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    private static nint GetWindowLongPtr(nint windowHandle, int index)
    {
        return nint.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : GetWindowLong32(windowHandle, index);
    }

    /// <summary>屏幕矩形（物理像素），与 GetWindowRect / MONITORINFO 的内存布局一致。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeScreenRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeScreenRect Monitor;
        public NativeScreenRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameW(nint windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeScreenRect bounds);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    // 必须显式绑定 Unicode 版本：默认 CharSet 会绑到 GetMonitorInfoA，
    // 它对 cbSize 的校验与 W 版不同，会静默返回 false。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitorHandle, ref NativeMonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint windowHandle, uint attribute, out int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern nint GetWindowLong32(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(nint windowHandle, int index);
}
