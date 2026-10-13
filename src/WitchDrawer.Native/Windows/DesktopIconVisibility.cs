using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WitchDrawer.Native.Windows;

public static class DesktopIconVisibility
{
    private const string ExplorerAdvancedRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string HideIconsValueName = "HideIcons";
    private const uint SendMessageAbortIfHung = 0x0002;
    private const uint ShellChangeAssociationChanged = 0x08000000;
    private const uint ShellNotifyFlushNoWait = 0x2000;
    private const int ShowWindowHide = 0;
    private const int ShowWindowShow = 5;
    private const uint ListViewHitTest = 0x1000 + 18; // LVM_HITTEST
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemoryCommitReserve = 0x3000;
    private const uint MemoryRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    public static bool IsHidden()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            ExplorerAdvancedRegistryPath,
            writable: false);
        return IsHiddenRegistryValue(key?.GetValue(HideIconsValueName));
    }

    public static Task SetHiddenAsync(
        bool hidden,
        CancellationToken cancellationToken = default) =>
        RunSetHiddenAsync(SetHidden, hidden, cancellationToken);

    internal static Task RunSetHiddenAsync(
        Action<bool> setHidden,
        bool hidden,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setHidden);
        return Task.Run(() => setHidden(hidden), cancellationToken);
    }

    public static void SetHidden(bool hidden)
    {
        ApplyHiddenState(
            hidden,
            WriteHiddenRegistry,
            FindDesktopListView,
            SetDesktopListViewHidden,
            NotifyDesktopIconSettingChanged);
    }

    /// <summary>
    /// 一次性恢复入口：桌面图标处于隐藏状态时恢复为显示（清理旧版「双击桌面切换」
    /// 误触发并跨重启留存的隐藏状态）。返回是否执行了恢复。
    /// </summary>
    public static bool RestoreIfHidden() => RunRestoreIfHidden(IsHidden, SetHidden);

    public static Task<bool> RestoreIfHiddenAsync(CancellationToken cancellationToken = default) =>
        Task.Run(RestoreIfHidden, cancellationToken);

    internal static bool RunRestoreIfHidden(
        Func<bool> isHidden,
        Action<bool> setHidden)
    {
        ArgumentNullException.ThrowIfNull(isHidden);
        ArgumentNullException.ThrowIfNull(setHidden);

        if (!isHidden())
        {
            return false;
        }

        setHidden(false);
        return true;
    }

    internal static void ApplyHiddenState(
        bool hidden,
        Action<bool> writeRegistry,
        Func<nint> findDesktopListView,
        Action<nint, bool> setDesktopListViewHidden,
        Action notifySettingChanged)
    {
        ArgumentNullException.ThrowIfNull(writeRegistry);
        ArgumentNullException.ThrowIfNull(findDesktopListView);
        ArgumentNullException.ThrowIfNull(setDesktopListViewHidden);
        ArgumentNullException.ThrowIfNull(notifySettingChanged);

        writeRegistry(hidden);

        var desktopListView = findDesktopListView();
        if (desktopListView != nint.Zero)
        {
            setDesktopListViewHidden(desktopListView, hidden);
        }

        notifySettingChanged();
    }

    private static void WriteHiddenRegistry(bool hidden)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            ExplorerAdvancedRegistryPath,
            writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 桌面图标设置。");
        key.SetValue(
            HideIconsValueName,
            hidden ? 1 : 0,
            RegistryValueKind.DWord);
    }

    private static void SetDesktopListViewHidden(nint desktopListView, bool hidden)
    {
        ShowWindow(desktopListView, hidden ? ShowWindowHide : ShowWindowShow);
    }

    /// <summary>
    /// 桌面图标的显隐由 <see cref="SetDesktopListViewHidden"/> 直接改可见性即时生效，
    /// 注册表值负责跨资源管理器重启保持状态；这里只补一次外壳关联变更通知，让资源管理器
    /// 刷新图标状态。
    /// 注意：不要再向 HWND_BROADCAST 广播 WM_SETTINGCHANGE（旧提交 bd7e991 的做法）。
    /// 全局广播会打到所有顶层窗口：实测火绒流量监控悬浮窗（类名 HRNETFLOWTRAY，贴在主屏
    /// 底边自动隐藏）每收到一次广播就向右平移约 19–20 px，而只发 SHChangeNotify 或只写
    /// 注册表都不会引起位移（2026-10-08 实机逐因子测量）。
    /// </summary>
    private static void NotifyDesktopIconSettingChanged()
    {
        SHChangeNotify(
            ShellChangeAssociationChanged,
            ShellNotifyFlushNoWait,
            nint.Zero,
            nint.Zero);
    }

    public static bool IsBlankDesktopPoint(int screenX, int screenY)
    {
        var clickedWindow = WindowFromPoint(new NativePoint(screenX, screenY));
        if (clickedWindow == nint.Zero)
        {
            return false;
        }

        var desktopListView = FindDesktopListView();
        if (ShouldHitTestDesktopListView(
                desktopListView != nint.Zero,
                clickedWindow == desktopListView,
                IsDesktopHostWindow(clickedWindow)))
        {
            return IsBlankListViewPoint(desktopListView, screenX, screenY);
        }

        return desktopListView == nint.Zero && IsDesktopHostWindow(clickedWindow);
    }

    /// <summary>
    /// 是否用桌面 ListView 的命中测试来区分「桌面图标」与「桌面空白」。
    /// 关键：不能把 ListView 的可见性当作前提。桌面图标被隐藏（HideIcons=1）时
    /// ListView 不可见，WindowFromPoint 只会带回 Progman/WorkerW 这类桌面宿主；
    /// 若此时退化成「命中桌面宿主即算空白」，那么双击桌面上的图标同样会被判成
    /// 「双击空白」——这正是双击切换误触发的来源，也是它曾被按启动方式一刀切
    /// 禁用的原因。LVM_HITTEST 在 ListView 隐藏时依然返回正确的条目索引
    /// （2026-10-07 实机验证：图标可见与隐藏两种状态下，图标位置 iItem=0、
    /// 空白位置 iItem=-1，itemCount 均为 28）。
    /// </summary>
    internal static bool ShouldHitTestDesktopListView(
        bool hasDesktopListView,
        bool clickedIsDesktopListView,
        bool clickedIsDesktopHost) =>
        hasDesktopListView
        && (clickedIsDesktopListView || clickedIsDesktopHost);

    internal static bool IsHiddenRegistryValue(object? value) => value switch
    {
        int intValue => intValue != 0,
        long longValue => longValue != 0,
        _ => false
    };

    private static nint FindDesktopListView()
    {
        var programManager = FindWindowW("Progman", null);
        var shellView = FindWindowExW(
            programManager,
            nint.Zero,
            "SHELLDLL_DefView",
            null);
        if (shellView == nint.Zero)
        {
            EnumWindows(
                (window, _) =>
                {
                    shellView = FindWindowExW(
                        window,
                        nint.Zero,
                        "SHELLDLL_DefView",
                        null);
                    return shellView == nint.Zero;
                },
                nint.Zero);
        }

        return shellView == nint.Zero
            ? nint.Zero
            : FindWindowExW(shellView, nint.Zero, "SysListView32", null);
    }

    private static bool IsBlankListViewPoint(nint listView, int screenX, int screenY)
    {
        var point = new NativePoint(screenX, screenY);
        if (!ScreenToClient(listView, ref point))
        {
            return false;
        }

        GetWindowThreadProcessId(listView, out var processId);
        var process = OpenProcess(ProcessVmOperation | ProcessVmWrite, false, processId);
        if (process == nint.Zero)
        {
            return false;
        }

        var size = (nuint)Marshal.SizeOf<ListViewHitTestInfo>();
        var remoteBuffer = nint.Zero;
        try
        {
            remoteBuffer = VirtualAllocEx(
                process,
                nint.Zero,
                size,
                MemoryCommitReserve,
                PageReadWrite);
            if (remoteBuffer == nint.Zero)
            {
                return false;
            }

            var hitTest = new ListViewHitTestInfo
            {
                Point = point,
                Item = -1,
                SubItem = -1,
                Group = -1
            };
            if (!WriteProcessMemory(process, remoteBuffer, ref hitTest, size, out var bytesWritten)
                || bytesWritten != size)
            {
                return false;
            }

            var sent = SendMessageTimeoutPointer(
                listView,
                ListViewHitTest,
                nint.Zero,
                remoteBuffer,
                SendMessageAbortIfHung,
                100,
                out var result);
            return sent != nint.Zero && result == new nint(-1);
        }
        finally
        {
            if (remoteBuffer != nint.Zero)
            {
                VirtualFreeEx(process, remoteBuffer, 0, MemoryRelease);
            }

            CloseHandle(process);
        }
    }

    internal static bool IsDesktopHostClass(string? className) =>
        string.Equals(className, "Progman", StringComparison.Ordinal)
        || string.Equals(className, "WorkerW", StringComparison.Ordinal)
        || string.Equals(className, "SHELLDLL_DefView", StringComparison.Ordinal);

    private static bool IsDesktopHostWindow(nint window)
    {
        var className = new StringBuilder(64);
        return GetClassNameW(window, className, className.Capacity) > 0
            && IsDesktopHostClass(className.ToString());
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ListViewHitTestInfo
    {
        public NativePoint Point;
        public uint Flags;
        public int Item;
        public int SubItem;
        public int Group;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(
        nint parent,
        nint childAfter,
        string className,
        string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeoutPointer(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(
        nint process,
        nint address,
        nuint size,
        uint allocationType,
        uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        nint process,
        nint address,
        ref ListViewHitTestInfo buffer,
        nuint size,
        out nuint bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(
        nint process,
        nint address,
        nuint size,
        uint freeType);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        nint item1,
        nint item2);
}
