using System.Runtime.InteropServices;
using System.Windows.Threading;
using WitchDrawer.Core.Logging;

namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 空闲时把进程工作集交还给系统。
///
/// WPF 进程的内存大头不是托管对象（实测托管堆仅 12-14 MB，而任务管理器口径
/// 是 220 MB 上下），而是按需缺页载入后长期驻留的代码页：显卡驱动的着色器
/// 编译器（nvgpucomp64.dll 约 108 MB、igc64.dll 约 85 MB）与 D3D 驱动合计
/// 超过 260 MB 映像，再加上 CLR 与 WPF 自身——实测 197 个模块、映像合计 504 MB。
/// 这些页面在应用空闲时并不需要：EmptyWorkingSet 把它们移出工作集后页面转入
/// 待机列表（内容仍在物理内存里，系统可随时回收），重新访问只需重映射。
/// 实测裁剪 222 MB → 7 MB，裁剪后还原主窗口的耗时 14 ms（未裁剪时 13 ms）。
///
/// 两个触发条件互补，任一满足即可裁剪：
///   - 系统级输入静默超过 <see cref="SystemIdleThreshold"/>：用户离开了电脑。
///     这条覆盖"鼠标恰好停在收纳盒上，用户起身走开"的情形。
///   - 本应用自身空闲超过 <see cref="SelfIdleThreshold"/>：鼠标不在本进程任何
///     窗口上，且本进程不是前台窗口。这条覆盖"主窗口开着、用户在别的应用里工作"
///     的情形——此时系统输入并不空闲，但本应用确实没在被使用。
/// 裁剪后进入 <see cref="Cooldown"/> 冷却期，避免页面被反复换入换出。
/// 收纳盒窗口静止时不重绘，因此裁剪不会造成可见的重绘延迟。
/// </summary>
internal sealed class WorkingSetTrimmer : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SystemIdleThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SelfIdleThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _timer;

    private DateTimeOffset? _selfIdleSince;
    private DateTimeOffset _lastTrimAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public WorkingSetTrimmer(IAppLogger logger)
    {
        _logger = logger;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = CheckInterval
        };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        if (!_disposed)
        {
            _timer.Start();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var systemIdle = GetSystemIdleTime();

        _selfIdleSince = IsSelfIdle()
            ? _selfIdleSince ?? DateTimeOffset.UtcNow
            : null;

        if (DateTimeOffset.UtcNow - _lastTrimAt < Cooldown)
        {
            return;
        }

        var systemIdleReached = systemIdle >= SystemIdleThreshold;
        var selfIdleReached = _selfIdleSince is DateTimeOffset since
            && DateTimeOffset.UtcNow - since >= SelfIdleThreshold;

        if (systemIdleReached || selfIdleReached)
        {
            Trim(systemIdle, systemIdleReached ? "system-idle" : "self-idle");
        }
    }

    private void Trim(TimeSpan systemIdle, string reason)
    {
        var before = Environment.WorkingSet;
        var trimmed = EmptyWorkingSet(GetCurrentProcess());
        var after = Environment.WorkingSet;
        _lastTrimAt = DateTimeOffset.UtcNow;

        if (trimmed)
        {
            _logger.Info(
                $"[perf] working set trimmed ({reason}): {before / (1024 * 1024)} MB -> {after / (1024 * 1024)} MB "
                + $"(system idle {systemIdle.TotalSeconds:F0}s).");
        }
        else
        {
            _logger.Info(
                $"[perf] working set trim skipped: EmptyWorkingSet failed with error {Marshal.GetLastWin32Error()}.");
        }
    }

    private static bool IsSelfIdle()
    {
        var processId = (uint)Environment.ProcessId;

        if (GetCursorPos(out var cursor))
        {
            var underCursor = WindowFromPoint(cursor);
            if (underCursor != nint.Zero)
            {
                _ = GetWindowThreadProcessId(underCursor, out var cursorPid);
                if (cursorPid == processId)
                {
                    return false;
                }
            }
        }

        var foreground = GetForegroundWindow();
        if (foreground != nint.Zero)
        {
            _ = GetWindowThreadProcessId(foreground, out var foregroundPid);
            if (foregroundPid == processId)
            {
                return false;
            }
        }

        return true;
    }

    private static TimeSpan GetSystemIdleTime()
    {
        var info = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            // 取不到空闲时间时按"正在使用"处理：宁可不裁剪，也不要在用户操作时换出页面。
            return TimeSpan.Zero;
        }

        var elapsed = unchecked((uint)Environment.TickCount - info.DwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(nint hProcess);
}
