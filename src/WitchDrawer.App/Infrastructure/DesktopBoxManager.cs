using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using WitchDrawer.App.Messages;
using WitchDrawer.App.ViewModels;
using WitchDrawer.App.Views;
using WitchDrawer.Core.Abstractions;
using WitchDrawer.Core.Logging;
using WitchDrawer.Core.Models;
using WitchDrawer.Core.Services;
using WitchDrawer.Native.Windows;

namespace WitchDrawer.App.Infrastructure;

public sealed class DesktopBoxManager
{
    private const string BoxPositionSettingPrefix = "BoxPosition:";
    private const string PhysicalPositionPrefix = "px:";
    private const string LayoutBackupSettingPrefix = "LayoutBackup:";
    private const int LayoutBackupVersion = 2;
    private const int LegacyLayoutBackupVersion = 1;
    private const int LayoutBackupSlotCount = 3;
    private const int MaxLayoutBackupPositions = 4096;
    private const char PositionSeparator = ',';

    private readonly DrawerService _drawerService;
    private readonly TodoService _todoService;
    private readonly IFileLauncher _launcher;
    private readonly IAppLogger _logger;
    private readonly BoxVisualStyleStore _boxVisualStyleStore;
    private readonly BoxPositionLockStateStore _boxPositionLockStateStore;
    private readonly BoxTabGroupService _tabGroupService;
    private readonly Dictionary<Guid, DesktopBoxWindow> _windows = [];
    private readonly ForegroundWindowMonitor _foregroundWindowMonitor;
    private readonly GlobalMouseButtonMonitor _mouseButtonMonitor;
    private readonly DesktopDoubleClickDetector _desktopDoubleClickDetector = new();
    private readonly Channel<DesktopMouseButtonEvent> _desktopMouseButtonEvents =
        Channel.CreateUnbounded<DesktopMouseButtonEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private readonly Task _desktopMouseButtonProcessor;
    private readonly Func<bool> _isDesktopDoubleClickEnabled;
    private readonly HashSet<Guid> _overlapResolutionBoxIds = [];
    private readonly MappingFolderWatcher _mappingFolderWatcher;
    private bool _closing;
    private bool _desktopIsForeground;
    private CancellationTokenSource? _foregroundChangeCts;
    private CancellationTokenSource? _desktopContextMenuRestoreCts;
    private long _showDesktopShortcutObservedUntilTick;
    private GuideLineWindow? _verticalGuide;
    private GuideLineWindow? _horizontalGuide;
    private bool _isAdjustingPosition;
    private DesktopBoxWindow? _mergePreviewHost;
    private readonly DesktopToggleVisibilityState _desktopToggleVisibility = new();

    public DesktopBoxManager(
        DrawerService drawerService,
        TodoService todoService,
        IFileLauncher launcher,
        IAppLogger logger,
        BoxVisualStyleStore boxVisualStyleStore,
        BoxPositionLockStateStore boxPositionLockStateStore,
        BoxTabGroupService tabGroupService,
        Func<bool> isDesktopDoubleClickEnabled)
    {
        _drawerService = drawerService;
        _todoService = todoService;
        _launcher = launcher;
        _logger = logger;
        _boxVisualStyleStore = boxVisualStyleStore;
        _boxPositionLockStateStore = boxPositionLockStateStore;
        _tabGroupService = tabGroupService;
        _isDesktopDoubleClickEnabled = isDesktopDoubleClickEnabled;
        // 映射文件夹变化 -> 同步新项 -> 刷新对应盒子（实时自动识别）。
        _mappingFolderWatcher = new MappingFolderWatcher(
            drawerService,
            boxId => RefreshItemsAsync(boxId),
            _logger);
        _foregroundWindowMonitor = new ForegroundWindowMonitor();
        _foregroundWindowMonitor.ForegroundWindowChanged += OnForegroundWindowChanged;
        _desktopIsForeground = ForegroundWindowMonitor.IsDesktopWindow(
            ForegroundWindowMonitor.GetCurrentForegroundWindow());
        if (!_foregroundWindowMonitor.IsActive)
        {
            _logger.Info("Foreground window monitoring is unavailable; Show Desktop layering may be limited.");
        }

        // 盒子带 WS_EX_NOACTIVATE，点击不激活窗口，桌面点击不会产生 Deactivated
        // 事件，选中框无法自动清除。全局鼠标钩子补上"外部点击"信号。
        _mouseButtonMonitor = new GlobalMouseButtonMonitor();
        _mouseButtonMonitor.MouseButtonDown += OnGlobalMouseButtonDown;
        _mouseButtonMonitor.MouseButtonPressed += OnGlobalMouseButtonPressed;
        // One background consumer preserves click order without blocking the WPF dispatcher.
        _desktopMouseButtonProcessor = Task.Run(ProcessDesktopMouseButtonEventsAsync);
        if (!_mouseButtonMonitor.IsActive)
        {
            _logger.Info("Global mouse monitoring is unavailable; outside clicks will not clear box selection.");
        }

        WeakReferenceMessenger.Default.Register<DesktopBoxManager, BoxLayoutPresetChangedMessage>(
            this,
            static (recipient, message) => recipient.ApplyBoxLayoutPreset(message));
        WeakReferenceMessenger.Default.Register<DesktopBoxManager, BoxPositionLockStateChangedMessage>(
            this,
            static (recipient, message) => recipient.ApplyBoxPositionLockState(message));
        WeakReferenceMessenger.Default.Register<DesktopBoxManager, BoxTitleVisibilityChangedMessage>(
            this,
            static (recipient, message) => recipient.ApplyTitleVisibility(message));
        WeakReferenceMessenger.Default.Register<DesktopBoxManager, BoxFileNameVisibilityChangedMessage>(
            this,
            static (recipient, message) => recipient.ApplyFileNameVisibility(message));
        WeakReferenceMessenger.Default.Register<DesktopBoxManager, DrawerSortModeChangedMessage>(
            this,
            static (recipient, message) => recipient.ApplyDrawerSortMode(message));
        WeakReferenceMessenger.Default.Register<DesktopBoxManager, BoxSizeModeChangedMessage>(
            this,
            static (recipient, message) => recipient.ApplyBoxSizeMode(message));
        WeakReferenceMessenger.Default.Register<DesktopBoxManager, BoxTabSplitRequestedMessage>(
            this,
            static (recipient, message) => recipient.ApplyTabSplit(message));
    }

    /// <summary>
    /// 标签栏"从组合中移出"请求：把任意标签（成员或宿主自身）拆出为独立盒子。
    /// 走 <see cref="DetachAsync"/>：成员→单拆，宿主→组内成员继任宿主。
    /// </summary>
    private void ApplyTabSplit(BoxTabSplitRequestedMessage message)
    {
        FireAndForget.Run(
            DetachAsync(message.MemberId),
            _logger,
            $"Failed to detach box {message.MemberId:N} from its tab host.");
    }

    public event EventHandler<BoxItemsChangedEventArgs>? ItemsChanged;

    public event EventHandler? DesktopBackgroundDoubleClicked;

    public event EventHandler? ShowDesktopActivated;

    private int _refreshVersion;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public async Task RefreshAsync(bool animateNewWindows = true)
    {
        if (_closing)
        {
            return;
        }

        var version = Interlocked.Increment(ref _refreshVersion);
        await _refreshGate.WaitAsync();
        try
        {
            if (_closing || version != Volatile.Read(ref _refreshVersion))
            {
                return;
            }

            var boxes = await _drawerService.GetBoxesAsync();
            if (_closing || version != Volatile.Read(ref _refreshVersion))
            {
                return;
            }

            var boxIds = boxes.Select(box => box.Id).ToHashSet();

            // 每次刷新重新标记哪些窗口参与重叠消解：只有本次新放置（无存档位置）
            // 或落位时被工作区钳制过（分辨率/显示器变化）的窗口才可被挪动。
            _overlapResolutionBoxIds.Clear();

            foreach (var removedId in _windows.Keys.Where(id => !boxIds.Contains(id)).ToArray())
            {
                var win = _windows[removedId];
                win.LocationChanged -= OnWindowLocationChanged;
                win.SizeChanged -= OnWindowSizeChanged;
                win.PreviewMouseLeftButtonUp -= OnWindowMouseUp;
                win.ForceClose();
                _windows.Remove(removedId);
                if (_mergePreviewHost is DesktopBoxWindow previewHost
                    && previewHost.ViewModel.BoxId == removedId)
                {
                    _mergePreviewHost = null;
                }
            }

            for (var index = 0; index < boxes.Count; index++)
            {
                if (_closing || version != Volatile.Read(ref _refreshVersion))
                {
                    return;
                }

                var box = boxes[index];

                // 成员盒已合并进宿主盒作为标签页，不创建独立桌面窗口。
                // 宿主盒自身正常创建窗口，随后在其窗口内承载各成员 VM。
                if (await _tabGroupService.GetHostAsync(box.Id) is not null)
                {
                    continue;
                }

                var visualStyle = await _boxVisualStyleStore.LoadAsync(box);
                var isPositionLocked =
                    await _boxPositionLockStateStore.LoadAsync(box.Id);
                if (!_windows.TryGetValue(box.Id, out var window))
                {
                    var layoutSettings = new DesktopBoxLayoutSettings(
                        box.Type == WitchDrawer.Core.Models.BoxType.Drawer);
                    var savedPreset = await _drawerService.GetSettingAsync(
                        BoxViewModel.GetLayoutPresetSettingKey(box.Id));
                    layoutSettings.ApplyPresetWithoutCallback(savedPreset);

                    var viewModel = new DesktopBoxViewModel(
                        box,
                        _drawerService,
                        _todoService,
                        _launcher,
                        _logger,
                        visualStyle,
                        layoutSettings);
                    await viewModel.LoadTitleVisibilityAsync();
                    await viewModel.LoadFileNameVisibilityAsync();
                    await viewModel.LoadMappingViewModeAsync();
                    await viewModel.LoadMappingListWidthAsync();
                    await viewModel.LoadMappingListHeightAsync();
                    await viewModel.LoadTodoBoxSizeAsync();
                    await viewModel.LoadDrawerStyleAsync();
                    // The persisted drawer height is snapped against the active row height.
                    // Load the file-name row first so a saved 4x4 cover stays 4x4 after restart.
                    await viewModel.LoadDrawerCoverSizeAsync();
                    await viewModel.LoadRollUpStateAsync();
                    await viewModel.LoadSortModeAsync();
                    await viewModel.LoadSizeModeAsync();
                    viewModel.ItemsChanged += (_, _) => ItemsChanged?.Invoke(
                        this,
                        new BoxItemsChangedEventArgs(viewModel.BoxId));

                    window = new DesktopBoxWindow(viewModel);
                    window.SuppressPopInOnLoad = !animateNewWindows;
                    var requiresOverlapResolution =
                        await PlaceWindowAsync(window, box.Id, index);

                    _windows.Add(box.Id, window);

                    window.LocationChanged += OnWindowLocationChanged;
                    window.SizeChanged += OnWindowSizeChanged;
                    window.PreviewMouseLeftButtonUp += OnWindowMouseUp;
                    window.SetPositionChangedCallback(async (id) =>
                    {
                        _isAdjustingPosition = true;
                        try
                        {
                            PerformSnappingAndAlignment(window, applySnap: true);
                        }
                        finally
                        {
                            _isAdjustingPosition = false;
                        }
                        HideGuides();
                        await SavePositionAsync(id);
                    });

                    // 先创建句柄：OnSourceInitialized 里完成挂桌面 + 沉底，
                    // 窗口首次可见时就已经在桌面层，不会先浮在最上层闪一帧再被压回。
                    new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                    window.Show();
                    if (_desktopToggleVisibility.TrackIfHidden(box.Id))
                    {
                        // 整体隐藏期间刷新重建出来的窗口也要跟着藏起来。
                        window.Hide();
                    }

                    window.SetPositionLocked(isPositionLocked);
                    window.SetDesktopForeground(_desktopIsForeground);
                    window.QueueSendToBottom();
                    await window.ViewModel.LoadAsync();
                    // 首次布局的测量约束来自初始 HWND 尺寸，内容稳定后强制重测一次，
                    // 否则窗口会一直停在错误的初始宽度（折叠抽屉盒封面两侧突出）。
                    window.ResyncSizeToContent();
                    window.UpdateLayout();
                    if (ClampWindowToCurrentWorkArea(window))
                    {
                        requiresOverlapResolution = true;
                    }

                    // Startup SizeChanged events are based on provisional template sizes.
                    // Only future user/content-driven resizes may clamp the live window.
                    window.EnableVisibleBoundsClamping();
                    if (requiresOverlapResolution)
                    {
                        _overlapResolutionBoxIds.Add(box.Id);
                    }

                    // 装载多标签成员：若该盒是 Tab 宿主，创建各成员 VM 并挂到宿主窗口。
                    await InitializeTabMembersAsync(window, box);
                }
                else
                {
                    window.ViewModel.UpdateBox(box, visualStyle);
                    await window.ViewModel.LoadDrawerStyleAsync();
                    window.SetPositionLocked(isPositionLocked);

                    // 宿主窗口已存在时仍需同步成员 VM 列表（盒子增删后）。
                    if (window.ViewModel.IsTabHost || await _tabGroupService.IsHostAsync(box.Id))
                    {
                        await InitializeTabMembersAsync(window, box);
                    }
                }

                window.SetDesktopForeground(_desktopIsForeground);
                window.QueueSendToBottom();
            }

            // 让映射文件夹监视器与当前持久化的映射文件夹保持一致。
            foreach (var box in boxes)
            {
                await _mappingFolderWatcher.SynchronizeAsync(box.Id);
            }

            ResolveWindowOverlaps();
            if (DesktopToolWindow.RepairShellLastActivePopup())
            {
                _logger.Info("Reset Progman last-active-popup after attaching desktop boxes.");
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Reloads item lists for existing desktop windows without recreating them.
    /// </summary>
    public async Task RefreshItemsAsync(Guid? affectedBoxId = null)
    {
        if (_closing)
        {
            return;
        }

        await _refreshGate.WaitAsync();
        try
        {
            if (_closing)
            {
                return;
            }

            if (affectedBoxId is Guid boxId)
            {
                if (_windows.TryGetValue(boxId, out var affectedWindow)
                    && affectedWindow.IsVisible)
                {
                    await affectedWindow.ViewModel.LoadAsync();
                    return;
                }

                // 该盒可能是合并宿主里的标签成员（成员盒没有独立窗口，_windows 查不到）：
                // 找到承载它的宿主窗口，刷新对应成员 VM，否则新增内容不会出现在标签页里。
                var hostId = await _tabGroupService.GetHostAsync(boxId);
                if (hostId is Guid host
                    && _windows.TryGetValue(host, out var hostWindow)
                    && hostWindow.IsVisible)
                {
                    var memberVm = hostWindow.ViewModel.TabVms
                        .FirstOrDefault(vm => vm.BoxId == boxId);
                    if (memberVm is not null)
                    {
                        // LoadAsync 内部对映射盒先同步映射文件夹再重载 items——
                        // 成员盒的新增引用因此显示到标签页。
                        await memberVm.LoadAsync();
                        _logger.Info(
                            $"Refreshed tab member {boxId:N} inside host {host:N} "
                            + "after mapping folder change.");
                    }
                }

                return;
            }

            foreach (var window in _windows.Values.Where(window => window.IsVisible).ToArray())
            {
                await window.ViewModel.LoadAsync();
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task SaveAllPositionsAsync()
    {
        var positions = _windows
            .Select(pair => new KeyValuePair<string, string>(
                BoxPositionSettingPrefix + pair.Key.ToString("N"),
                CaptureStoredPosition(pair.Value)))
            .ToArray();

        foreach (var (key, value) in positions)
        {
            await _drawerService.SetSettingAsync(key, value);
        }

        _logger.Info($"Recorded complete desktop layout for {positions.Length} boxes.");
    }

    public async Task<int> RecordLayoutBackupAsync(int slot)
    {
        var key = GetLayoutBackupSettingKey(slot);
        var positions = _windows
            .Select(pair => CaptureLayoutBackupPosition(pair.Key, pair.Value))
            .ToArray();
        var value = SerializeLayoutBackup(positions);

        await _drawerService.SetSettingAsync(key, value);
        _logger.Info($"Recorded layout backup slot {slot} with {positions.Length} boxes.");
        return positions.Length;
    }

    public async Task<bool> HasLayoutBackupAsync(int slot)
    {
        var key = GetLayoutBackupSettingKey(slot);
        var value = await _drawerService.GetSettingAsync(key);
        var hasBackup = TryParseLayoutBackup(value, out _);
        if (!hasBackup && !string.IsNullOrWhiteSpace(value))
        {
            _logger.Info($"Layout backup slot {slot} contains invalid data and is treated as empty.");
        }

        return hasBackup;
    }

    public async Task<bool> DeleteLayoutBackupAsync(int slot)
    {
        var key = GetLayoutBackupSettingKey(slot);
        var deleted = await _drawerService.DeleteSettingAsync(key);
        _logger.Info(
            deleted
                ? $"Deleted layout backup slot {slot}."
                : $"Layout backup slot {slot} was already empty when deletion was requested.");
        return deleted;
    }

    public async Task<LayoutBackupRestoreResult> RestoreLayoutBackupAsync(int slot)
    {
        var key = GetLayoutBackupSettingKey(slot);
        await _refreshGate.WaitAsync();
        try
        {
            if (_closing)
            {
                return new LayoutBackupRestoreResult(false, 0, 0);
            }

            var value = await _drawerService.GetSettingAsync(key);
            if (!TryParseLayoutBackup(value, out var positions))
            {
                _logger.Info($"Layout backup slot {slot} is empty or invalid; restore skipped.");
                return new LayoutBackupRestoreResult(false, 0, 0);
            }

            var restoredCount = 0;
            var missingCount = 0;
            _isAdjustingPosition = true;
            try
            {
                foreach (var position in positions)
                {
                    if (!_windows.TryGetValue(position.BoxId, out var window))
                    {
                        missingCount++;
                        continue;
                    }

                    if (position.IsPhysicalPixels)
                    {
                        window.MoveWindowOriginPixels(position.Left, position.Top);
                    }
                    else
                    {
                        // Version 1 backups stored WPF DIPs without monitor identity.
                        // Keep the legacy interpretation for one-time migration.
                        window.Left = position.Left;
                        window.Top = position.Top;
                    }
                    window.ResyncSizeToContent();
                    window.UpdateLayout();

                    var bounds = window.GetVisibleBoundsPixels();
                    var workArea = window.GetWorkAreaPixels();
                    if (bounds.Width > 0 && bounds.Height > 0 && !workArea.IsEmpty)
                    {
                        var origin = CalculateClampedVisibleOrigin(bounds, workArea);
                        window.MoveToVisibleOriginPixels(origin.X, origin.Y);
                    }

                    if (window.IsVisible)
                    {
                        window.QueueSendToBottom();
                    }

                    restoredCount++;
                }
            }
            finally
            {
                _isAdjustingPosition = false;
            }

            await SaveAllPositionsAsync();
            _logger.Info(
                $"Restored layout backup slot {slot}: restored={restoredCount}, missing={missingCount}.");
            return new LayoutBackupRestoreResult(true, restoredCount, missingCount);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Reattaches desktop boxes after Explorer recreates the Shell desktop.
    /// If Explorer destroyed an owned HWND, remove the stale WPF window and let
    /// the normal refresh path recreate it from persisted box data.
    /// </summary>
    public async Task RecoverDesktopHostsAsync()
    {
        if (_closing)
        {
            return;
        }

        // TaskbarCreated is broadcast as Explorer comes back. Give Progman a
        // short window to finish creating before resolving the new owner HWND.
        await Task.Delay(350);
        if (_closing)
        {
            return;
        }

        var recreateRequired = false;
        foreach (var (boxId, window) in _windows.ToArray())
        {
            if (window.IsNativeWindowAlive)
            {
                window.RefreshDesktopHost();
                window.QueueSendToBottom();
                continue;
            }

            window.LocationChanged -= OnWindowLocationChanged;
            window.SizeChanged -= OnWindowSizeChanged;
            window.PreviewMouseLeftButtonUp -= OnWindowMouseUp;
            // 外部（Explorer 重建桌面）销毁 HWND 不会触发 WPF Closed，必须显式 ForceClose
            // 让 OnClosed 里的退订/清理执行，否则整棵窗口对象图被静态事件永久引用（僵尸泄漏）。
            window.ForceClose();
            _windows.Remove(boxId);
            recreateRequired = true;
        }

        if (recreateRequired)
        {
            await RefreshAsync();
        }

        if (DesktopToolWindow.RepairShellLastActivePopup())
        {
            _logger.Info("Reset Progman last-active-popup after recovering desktop hosts.");
        }
    }

    /// <summary>
    /// Reopens the desktop window for a box that was hidden via its close (X)
    /// button. If the window still exists in memory it is simply shown again;
    /// otherwise a full refresh is triggered so it gets recreated.
    /// </summary>
    /// <returns><see langword="true"/> if a window was shown; <see langword="false"/> otherwise.</returns>
    public async Task<bool> ShowAsync(Guid boxId)
    {
        if (_closing)
        {
            return false;
        }

        if (_windows.TryGetValue(boxId, out var window))
        {
            if (!window.IsVisible)
            {
                await window.ViewModel.LoadAsync();
                window.Show();
            }

            // 用户显式显示这个盒子：它不再算被「双击桌面」整体隐藏。
            _desktopToggleVisibility.Forget(boxId);
            window.QueueSendToBottom();
            return true;
        }

        // Window was destroyed (e.g. fully closed) or never created this session:
        // refresh so the box window is recreated for the current box set.
        await RefreshAsync();
        return _windows.TryGetValue(boxId, out var refreshed) && refreshed.IsVisible;
    }

    public async Task ShowAllAsync()
    {
        if (_closing)
        {
            return;
        }

        await _refreshGate.WaitAsync();
        try
        {
            if (_closing)
            {
                return;
            }

            // 用户显式要求显示全部：整体隐藏状态就此结束。
            _desktopToggleVisibility.EndHide();
            var hiddenWindows = SnapshotHiddenWindows(
                _windows.Values,
                static window => window.IsVisible);
            foreach (var window in hiddenWindows)
            {
                await window.ViewModel.LoadAsync();
                if (_closing)
                {
                    return;
                }

                window.Show();
                window.QueueSendToBottom();
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>「双击桌面空白」是否把收纳盒整体隐藏了。</summary>
    public bool IsHiddenByDesktopToggle => _desktopToggleVisibility.IsHidden;

    /// <summary>
    /// 双击桌面空白时，把收纳盒与桌面图标一起隐藏/恢复。
    /// 隐藏只作用于此刻可见的窗口；恢复只作用于当时被隐藏的那些，不会把用户
    /// 自己用 X 关掉的盒子、或合并进宿主盒的成员盒一并拉起来。
    /// </summary>
    public async Task SetHiddenByDesktopToggleAsync(bool hidden)
    {
        if (_closing)
        {
            return;
        }

        await _refreshGate.WaitAsync();
        try
        {
            if (_closing)
            {
                return;
            }

            if (hidden)
            {
                var visibleBoxIds = _windows
                    .Where(pair => pair.Value.IsVisible)
                    .Select(pair => pair.Key);
                foreach (var boxId in _desktopToggleVisibility.BeginHide(visibleBoxIds))
                {
                    if (_windows.TryGetValue(boxId, out var window))
                    {
                        window.Hide();
                    }
                }

                return;
            }

            foreach (var boxId in _desktopToggleVisibility.EndHide())
            {
                if (_windows.TryGetValue(boxId, out var window))
                {
                    window.Show();
                    window.QueueSendToBottom();
                }
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    internal static TWindow[] SnapshotHiddenWindows<TWindow>(
        IEnumerable<TWindow> windows,
        Func<TWindow, bool> isVisible)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(isVisible);
        return windows.Where(window => !isVisible(window)).ToArray();
    }

    /// <summary>
    /// 把成员盒合并进宿主盒：持久化宿主/成员关系，隐藏成员的独立桌面窗口，
    /// 刷新宿主窗口使其重新装载各成员标签页。
    /// 合并时把宿主的当前宽高快照复制到成员的独立设置键：成员移出后保持合并时的尺寸。
    /// </summary>
    public async Task MergeAsync(Guid memberId, Guid hostId)
    {
        if (_closing)
        {
            return;
        }

        await _tabGroupService.MergeAsync(memberId, hostId);
        // 快照必须在窗口重建前做：此时两个 VM 都还活着。
        if (_windows.TryGetValue(hostId, out var hostWindow)
            && _windows.TryGetValue(memberId, out var memberWindow))
        {
            await memberWindow.ViewModel.CopySizeSnapshotFromAsync(hostWindow.ViewModel);
            memberWindow.Hide();
        }

        await RefreshAsync();
    }

    /// <summary>
    /// 解散盒的标签组：宿主盒则解散整组，成员盒则把自己从宿主移除，
    /// 刷新相关窗口以恢复成员的独立桌面窗口。
    /// </summary>
    public async Task DissolveAsync(Guid boxId)
    {
        if (_closing)
        {
            return;
        }

        if (await _tabGroupService.GetHostAsync(boxId) is not null)
        {
            // 该盒是某组的成员：只把自己从宿主移除。
            await _tabGroupService.RemoveMemberAsync(boxId);
        }
        else
        {
            // 该盒是宿主（或不是组成员）：整体解散组。
            await _tabGroupService.DissolveAsync(boxId);
        }

        await RefreshAsync();
    }

    /// <summary>
    /// 把任意标签从组合中拆出为独立盒子：
    /// 成员盒→只把自己移出；宿主盒自身→组内第一个成员继任为新宿主（组不散），
    /// 被移出的宿主成为独立盒子。刷新后各盒恢复独立窗口。
    /// </summary>
    public async Task DetachAsync(Guid boxId)
    {
        if (_closing)
        {
            return;
        }

        if (await _tabGroupService.GetHostAsync(boxId) is not null)
        {
            // 该盒是某组的成员：只把自己从宿主移除。
            await _tabGroupService.RemoveMemberAsync(boxId);
        }
        else if (await _tabGroupService.IsHostAsync(boxId))
        {
            // 该盒是宿主自身：第一个成员继任为宿主，原宿主拆出为独立盒。
            await _tabGroupService.RemoveHostWithSuccessionAsync(boxId);
        }
        else
        {
            // 不是任何组的成员：无需操作。
            return;
        }

        await RefreshAsync();
    }

    public async Task SavePositionAsync(Guid boxId)
    {
        if (!_windows.TryGetValue(boxId, out var window))
        {
            return;
        }

        var key = BoxPositionSettingPrefix + boxId.ToString("N");
        var value = CaptureStoredPosition(window);
        await _drawerService.SetSettingAsync(key, value);
    }

    public async Task<bool> CenterBoxOnScreenAsync(Guid boxId)
    {
        if (_closing)
        {
            return false;
        }

        await _refreshGate.WaitAsync();
        try
        {
            if (_closing || !_windows.TryGetValue(boxId, out var window))
            {
                _logger.Info($"Skipped screen-center recall for missing desktop box {boxId:N}.");
                return false;
            }

            if (!window.IsVisible)
            {
                await window.ViewModel.LoadAsync();
                if (_closing)
                {
                    return false;
                }

                window.Show();
            }

            // 用户把盒子召回屏幕中央：它不再算被「双击桌面」整体隐藏。
            _desktopToggleVisibility.Forget(boxId);
            window.ResyncSizeToContent();
            window.UpdateLayout();
            var bounds = window.GetVisibleBoundsPixels();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                _logger.Info($"Skipped screen-center recall for unmeasured desktop box {boxId:N}.");
                return false;
            }

            var workArea = DesktopBoxWindow.GetPrimaryWorkAreaPixels();
            if (workArea.IsEmpty)
            {
                _logger.Info($"Skipped screen-center recall because the primary work area is unavailable for {boxId:N}.");
                return false;
            }

            var origin = CalculateCenteredVisibleOrigin(bounds.Size, workArea);
            _isAdjustingPosition = true;
            try
            {
                window.MoveToVisibleOriginPixels(origin.X, origin.Y);
            }
            finally
            {
                _isAdjustingPosition = false;
            }

            window.QueueSendToBottom();
            var key = BoxPositionSettingPrefix + boxId.ToString("N");
            var value = CaptureStoredPosition(window);
            await _drawerService.SetSettingAsync(key, value);
            _logger.Info($"Recalled desktop box {boxId:N} to screen center at {value}.");
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task CloseAllAsync()
    {
        _closing = true;
        await SaveAllPositionsAsync();
        foreach (var window in _windows.Values)
        {
            window.LocationChanged -= OnWindowLocationChanged;
            window.SizeChanged -= OnWindowSizeChanged;
            window.PreviewMouseLeftButtonUp -= OnWindowMouseUp;
            window.ForceClose();
        }

        _windows.Clear();
        var foregroundChangeCts = Interlocked.Exchange(ref _foregroundChangeCts, null);
        foregroundChangeCts?.Cancel();
        foregroundChangeCts?.Dispose();
        var desktopContextMenuRestoreCts = Interlocked.Exchange(ref _desktopContextMenuRestoreCts, null);
        desktopContextMenuRestoreCts?.Cancel();
        desktopContextMenuRestoreCts?.Dispose();
        _foregroundWindowMonitor.ForegroundWindowChanged -= OnForegroundWindowChanged;
        _foregroundWindowMonitor.Dispose();
        _mouseButtonMonitor.MouseButtonDown -= OnGlobalMouseButtonDown;
        _mouseButtonMonitor.MouseButtonPressed -= OnGlobalMouseButtonPressed;
        _mouseButtonMonitor.Dispose();
        _desktopMouseButtonEvents.Writer.TryComplete();
        await _desktopMouseButtonProcessor;

        _verticalGuide?.Close();
        _verticalGuide = null;
        _horizontalGuide?.Close();
        _horizontalGuide = null;
        _mappingFolderWatcher.Dispose();
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    /// <summary>
    /// 盒子带 WS_EX_NOACTIVATE：点击盒子不激活任何窗口，随后点击桌面时
    /// Window/Application.Deactivated 都不会触发，选中框（蓝框）会一直残留。
    /// 全局鼠标钩子在每次按键时命中测试光标下的窗口：命中点不在某个盒子的
    /// 窗口上，就清掉那个盒子的选中态。命中本盒子时保留——本盒子自己的鼠标
    /// 处理（点空白清空/点项目改选）会接着处理这次点击。
    /// </summary>
    internal static bool ShouldClearSelectionOnOutsideClick(nint clickedHandle, nint boxHandle) =>
        clickedHandle != boxHandle;

    private void OnGlobalMouseButtonDown(int screenX, int screenY)
    {
        if (_closing)
        {
            return;
        }

        if (!_windows.Values.Any(window => window.HasSelection))
        {
            return;
        }

        var clickedHandle = GlobalMouseButtonMonitor.HitTestWindowHandle(screenX, screenY);
        foreach (var window in _windows.Values)
        {
            if (ShouldClearSelectionOnOutsideClick(clickedHandle, window.NativeHandle))
            {
                window.ClearSelectionFromOutside();
            }
        }
    }

    private void OnGlobalMouseButtonPressed(
        int screenX,
        int screenY,
        uint timestamp,
        GlobalMouseButton button)
    {
        if (_closing)
        {
            return;
        }

        // 桌面空白处右键按下时：系统随后会弹桌面菜单/清理确认等 Shell 弹窗。
        // 抢在弹窗出现前把盒子脱离 Shell 属主，避免弹窗被抬起时盒子一起闪到前层。
        if (button == GlobalMouseButton.Right
            && ForegroundWindowMonitor.IsDesktopWindow(
                GlobalMouseButtonMonitor.HitTestWindowHandle(screenX, screenY)))
        {
            SuspendForDesktopContextMenu();
        }

        _desktopMouseButtonEvents.Writer.TryWrite(new DesktopMouseButtonEvent(
            screenX,
            screenY,
            timestamp,
            button,
            _isDesktopDoubleClickEnabled()));
    }

    private async Task ProcessDesktopMouseButtonEventsAsync()
    {
        await foreach (var mouseEvent in _desktopMouseButtonEvents.Reader.ReadAllAsync()
                           .ConfigureAwait(false))
        {
            if (_closing || !mouseEvent.IsDesktopDoubleClickEnabled)
            {
                _desktopDoubleClickDetector.Reset();
                continue;
            }

            bool isBlankDesktopPoint;
            try
            {
                isBlankDesktopPoint = mouseEvent.Button == GlobalMouseButton.Left
                    && DesktopIconVisibility.IsBlankDesktopPoint(
                        mouseEvent.ScreenX,
                        mouseEvent.ScreenY);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Failed to test the desktop click target.");
                _desktopDoubleClickDetector.Reset();
                continue;
            }

            if (!_desktopDoubleClickDetector.RegisterButtonDown(
                    mouseEvent.ScreenX,
                    mouseEvent.ScreenY,
                    mouseEvent.Timestamp,
                    mouseEvent.Button,
                    isBlankDesktopPoint))
            {
                continue;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                continue;
            }

            _ = dispatcher.BeginInvoke(() =>
            {
                if (!_closing && _isDesktopDoubleClickEnabled())
                {
                    DesktopBackgroundDoubleClicked?.Invoke(this, EventArgs.Empty);
                }
            });
        }
    }

    private void OnForegroundWindowChanged(nint windowHandle)
    {
        if (_closing)
        {
            return;
        }

        // Shell 属主弹窗（如回收站确认框）可能出现就把盒子连带抬起的闪动。
        // 不等 80ms 防抖，立即在 UI 线程上先脱离属主并压底，减少闪烁。
        if (windowHandle != nint.Zero
            && !ForegroundWindowMonitor.IsDesktopWindow(windowHandle)
            && ForegroundWindowMonitor.IsWindowOwnedByShell(windowHandle))
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is { HasShutdownStarted: false })
            {
                dispatcher.BeginInvoke(
                    () =>
                    {
                        if (_closing)
                        {
                            return;
                        }

                        var isOwnBox = _windows.Values.Any(
                            window => window.NativeHandle == windowHandle);
                        if (!isOwnBox)
                        {
                            SuspendAllShellPopupOwnership();
                        }
                    },
                    System.Windows.Threading.DispatcherPriority.Send);
            }
        }

        if (DesktopToolWindow.IsShowDesktopShortcutPressed())
        {
            Interlocked.Exchange(
                ref _showDesktopShortcutObservedUntilTick,
                Environment.TickCount64 + 750);
        }

        // Win+D emits a short burst of foreground changes (for example Progman,
        // WorkerW and transient shell windows). Applying every intermediate handle
        // moves all boxes up and down several times and produces a visible flash.
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _foregroundChangeCts, next);
        previous?.Cancel();
        previous?.Dispose();
        FireAndForget.Run(
                ApplyForegroundWindowAfterSettlingAsync(next),
                _logger,
                "Failed to apply foreground window state after settling.");
    }

    private async Task ApplyForegroundWindowAfterSettlingAsync(CancellationTokenSource changeCts)
    {
        var cancellationToken = changeCts.Token;
        try
        {
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _closing))
            {
                return;
            }

            var windowHandle = ForegroundWindowMonitor.GetCurrentForegroundWindow();
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            await dispatcher.InvokeAsync(
                () =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        ApplyForegroundWindow(windowHandle);
                    }
                },
                System.Windows.Threading.DispatcherPriority.Background,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _foregroundChangeCts, null, changeCts),
                    changeCts))
            {
                changeCts.Dispose();
            }
        }
    }

    private readonly record struct DesktopMouseButtonEvent(
        int ScreenX,
        int ScreenY,
        uint Timestamp,
        GlobalMouseButton Button,
        bool IsDesktopDoubleClickEnabled);

    private void ApplyForegroundWindow(nint windowHandle)
    {
        if (_closing || windowHandle == nint.Zero)
        {
            return;
        }

        var isDesktopWindow = ForegroundWindowMonitor.IsDesktopWindow(windowHandle);
        var isDesktopBoxWindow = _windows.Values.Any(
            window => window.NativeHandle == windowHandle);
        // 系统弹窗（如回收站确认框）与盒子同在 Shell 属主链：临时脱离属主，
        // 避免压底盒子时把弹窗连带压到普通程序下面。
        var isShellPopup = !isDesktopWindow
            && !isDesktopBoxWindow
            && ForegroundWindowMonitor.IsWindowOwnedByShell(windowHandle);
        if (isShellPopup)
        {
            SuspendAllShellPopupOwnership();
        }
        else
        {
            RestoreAllShellPopupOwnership();
        }

        var desktopIsForeground = ResolveDesktopForegroundState(
            isDesktopWindow,
            isDesktopBoxWindow);
        SetDesktopForeground(desktopIsForeground);

        if (ShouldLowerMainWindowForShowDesktop(
                desktopIsForeground,
                Environment.TickCount64,
                Interlocked.Read(ref _showDesktopShortcutObservedUntilTick)))
        {
            Interlocked.Exchange(ref _showDesktopShortcutObservedUntilTick, 0);
            ShowDesktopActivated?.Invoke(this, EventArgs.Empty);
        }
    }


    internal static bool ResolveDesktopForegroundState(
        bool isDesktopWindow,
        bool isDesktopBoxWindow) =>
        isDesktopWindow && !isDesktopBoxWindow;

    internal static bool ShouldLowerMainWindowForShowDesktop(
        bool desktopIsForeground,
        long currentTick,
        long shortcutObservedUntilTick) =>
        desktopIsForeground
        && shortcutObservedUntilTick > 0
        && currentTick <= shortcutObservedUntilTick;

    private void SetDesktopForeground(bool isForeground)
    {
        if (_desktopIsForeground == isForeground)
        {
            return;
        }

        _desktopIsForeground = isForeground;
        foreach (var window in _windows.Values)
        {
            window.SetDesktopForeground(isForeground);
        }
    }

    private void SuspendAllShellPopupOwnership()
    {
        foreach (var window in _windows.Values)
        {
            window.SuspendDesktopOwnershipForShellPopup();
        }
    }

    /// <summary>
    /// 桌面右键预挂起：先把盒子脱离 Shell 属主并压底，
    /// 让后续 Shell 菜单/确认弹窗出现时不再把盒子带起来。
    /// 3 秒后若前台已不是 Shell 弹窗则自动恢复挂载。
    /// </summary>
    private void SuspendForDesktopContextMenu()
    {
        SuspendAllShellPopupOwnership();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _desktopContextMenuRestoreCts, next);
        previous?.Cancel();
        previous?.Dispose();
        var token = next.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                if (token.IsCancellationRequested || _closing)
                {
                    return;
                }

                var foreground = ForegroundWindowMonitor.GetCurrentForegroundWindow();
                var stillShellPopup = foreground != nint.Zero
                    && !ForegroundWindowMonitor.IsDesktopWindow(foreground)
                    && ForegroundWindowMonitor.IsWindowOwnedByShell(foreground);
                if (!stillShellPopup)
                {
                    await dispatcher.InvokeAsync(RestoreAllShellPopupOwnership);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (ReferenceEquals(
                        Interlocked.CompareExchange(ref _desktopContextMenuRestoreCts, null, next),
                        next))
                {
                    next.Dispose();
                }
            }
        }, token);
    }

    private void RestoreAllShellPopupOwnership()
    {
        foreach (var window in _windows.Values)
        {
            window.RestoreDesktopOwnershipAfterShellPopup();
        }
    }

    private void ApplyBoxLayoutPreset(BoxLayoutPresetChangedMessage message)
    {
        if (!_windows.TryGetValue(message.BoxId, out var window))
        {
            return;
        }

        window.ViewModel.LayoutSettings.ApplyPresetWithoutCallback(message.Preset);
    }

    private void ApplyBoxSizeMode(BoxSizeModeChangedMessage message)
    {
        if (!_windows.TryGetValue(message.BoxId, out var window))
        {
            return;
        }

        window.ViewModel.ApplySizeMode(
            new BoxSizeModeState(message.IsFixed, message.Columns, message.Rows));
    }

    private void ApplyBoxPositionLockState(
        BoxPositionLockStateChangedMessage message)
    {
        if (!_windows.TryGetValue(message.BoxId, out var window))
        {
            return;
        }

        window.SetPositionLocked(message.IsPositionLocked);
        _logger.Info(
            $"Applied position lock state {message.IsPositionLocked} "
            + $"to desktop box {message.BoxId:N}.");
    }

    private void ApplyTitleVisibility(
        BoxTitleVisibilityChangedMessage message)
    {
        if (_windows.TryGetValue(message.BoxId, out var window))
        {
            window.ViewModel.ApplyTitleVisibility(message.IsVisible);
        }
    }

    private void ApplyFileNameVisibility(
        BoxFileNameVisibilityChangedMessage message)
    {
        if (_windows.TryGetValue(message.BoxId, out var window))
        {
            window.ViewModel.ApplyFileNameVisibility(message.IsVisible);
            if (window.ViewModel.IsDrawerBox)
            {
                FireAndForget.Run(
                    window.ViewModel.SaveDrawerCoverSizeAsync(),
                    _logger,
                    $"Failed to save resized drawer cover for box {message.BoxId:N}.");
            }
        }
    }

    private void ApplyDrawerSortMode(DrawerSortModeChangedMessage message)
    {
        if (_windows.TryGetValue(message.BoxId, out var window)
            && window.ViewModel.ApplyDrawerSortMode(message.SortMode))
        {
            // 排序模式变化：重排盒内显示（自由模式则从 DB 恢复记忆布局）。
            _ = window.ViewModel.LoadAsync();
        }
    }

    /// <summary>
    /// 若该盒是 Tab 宿主，创建各成员盒的 ViewModel 并装载到宿主窗口。
    /// 非宿主盒调用此方法是空操作。宿主自身的 VM 始终是第一个标签。
    /// </summary>
    private async Task InitializeTabMembersAsync(DesktopBoxWindow hostWindow, Box hostBox)
    {
        var memberIds = await _tabGroupService.GetMembersAsync(hostBox.Id);
        if (memberIds.Count == 0)
        {
            hostWindow.ViewModel.IsTabHost = false;
            return;
        }

        var boxes = await _drawerService.GetBoxesAsync();
        var boxById = boxes.ToDictionary(b => b.Id);
        var memberVms = new List<DesktopBoxViewModel>(memberIds.Count);
        foreach (var memberId in memberIds)
        {
            if (!boxById.TryGetValue(memberId, out var memberBox))
            {
                continue;
            }

            var memberVisualStyle = await _boxVisualStyleStore.LoadAsync(memberBox);
            var memberLayout = new DesktopBoxLayoutSettings(
                memberBox.Type == WitchDrawer.Core.Models.BoxType.Drawer);
            var savedPreset = await _drawerService.GetSettingAsync(
                BoxViewModel.GetLayoutPresetSettingKey(memberId));
            memberLayout.ApplyPresetWithoutCallback(savedPreset);

            var memberVm = new DesktopBoxViewModel(
                memberBox,
                _drawerService,
                _todoService,
                _launcher,
                _logger,
                memberVisualStyle,
                memberLayout);
            await memberVm.LoadTitleVisibilityAsync();
            await memberVm.LoadFileNameVisibilityAsync();
            await memberVm.LoadMappingViewModeAsync();
            await memberVm.LoadMappingListWidthAsync();
            await memberVm.LoadMappingListHeightAsync();
            await memberVm.LoadTodoBoxSizeAsync();
            await memberVm.LoadDrawerStyleAsync();
            await memberVm.LoadDrawerCoverSizeAsync();
            await memberVm.LoadRollUpStateAsync();
            await memberVm.LoadSortModeAsync();
            await memberVm.LoadSizeModeAsync();
            await memberVm.LoadAsync();
            memberVms.Add(memberVm);
        }

        hostWindow.ViewModel.IsTabHost = true;
        hostWindow.ViewModel.InitializeTabs(memberVms);
    }

    /// <summary>
    /// 恢复窗口位置。返回 <see langword="true"/> 表示该窗口需要参与重叠消解：
    /// 没有存档位置的新盒子（按级联位落位，可能压住已有盒子），或存档位置被
    /// 工作区钳制过（分辨率/显示器变化导致多个盒子挤到同一边缘）。原样还原的
    /// 盒子返回 <see langword="false"/>——保持用户摆好的相对关系，哪怕彼此重叠。
    /// </summary>
    private async Task<bool> PlaceWindowAsync(DesktopBoxWindow window, Guid boxId, int fallbackIndex)
    {
        var savedPosition = await _drawerService.GetSettingAsync(BoxPositionSettingPrefix + boxId.ToString("N"));
        if (TryParseStoredPosition(savedPosition, out var left, out var top, out var isPhysicalPixels))
        {
            if (isPhysicalPixels)
            {
                // Create the HWND before restoring. Assigning physical coordinates
                // through WPF Left/Top would reinterpret them using the primary DPI.
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                window.MoveWindowOriginPixels(left, top);
            }
            else
            {
                // Old builds stored monitor-dependent WPF DIPs without a monitor id.
                // Interpret them once with the legacy behavior; the next save writes px:.
                window.Left = left;
                window.Top = top;
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            }

            // Do not clamp yet. Before Show + LoadAsync + the first stable
            // SizeToContent pass, DesiredSize can be wider than the final HWND
            // (notably for a collapsed drawer). Clamping that provisional size
            // permanently shifts a right-edge box left after every restart.
            return false;
        }

        // New windows still need a pre-show measurement for their fallback placement.
        window.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        PlaceNewWindow(window, fallbackIndex);
        return true;
    }

    private static bool ClampWindowToCurrentWorkArea(DesktopBoxWindow window)
    {
        // UpdateLayout has completed the stable SizeToContent pass. Use layout
        // dimensions here because the native HWND resize can lag the WPF layout event.
        var bounds = window.GetLayoutVisibleBoundsPixels();
        var workArea = window.GetWorkAreaPixels();
        if (bounds.IsEmpty || workArea.IsEmpty)
        {
            return false;
        }

        var origin = CalculateClampedVisibleOrigin(bounds, workArea);
        var wasClamped = Math.Abs(origin.X - bounds.Left) > 0.5
            || Math.Abs(origin.Y - bounds.Top) > 0.5;
        if (wasClamped)
        {
            window.MoveToVisibleOriginPixels(origin.X, origin.Y);
        }

        return wasClamped;
    }

    internal static string SerializePosition(double left, double top) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{left:R}{PositionSeparator}{top:R}");

    internal static string SerializePhysicalPosition(double leftPixels, double topPixels) =>
        PhysicalPositionPrefix + SerializePosition(leftPixels, topPixels);

    internal static bool TryParseStoredPosition(
        string? raw,
        out double left,
        out double top,
        out bool isPhysicalPixels)
    {
        isPhysicalPixels = raw?.StartsWith(PhysicalPositionPrefix, StringComparison.Ordinal) == true;
        var coordinates = isPhysicalPixels ? raw![PhysicalPositionPrefix.Length..] : raw;
        if (TryParsePosition(coordinates, out left, out top))
        {
            return true;
        }

        isPhysicalPixels = false;
        return false;
    }

    internal static bool TryParsePosition(string? raw, out double left, out double top)
    {
        left = 0;
        top = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split(PositionSeparator, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out top))
        {
            left = 0;
            top = 0;
            return false;
        }

        return double.IsFinite(left) && double.IsFinite(top);
    }

    private static string CaptureStoredPosition(DesktopBoxWindow window)
    {
        if (window.TryGetWindowBoundsPixels(out var bounds))
        {
            return SerializePhysicalPosition(bounds.Left, bounds.Top);
        }

        // A visible desktop box normally always has an HWND. Retain the legacy
        // fallback so shutdown cannot lose a position if a handle is being rebuilt.
        return SerializePosition(window.Left, window.Top);
    }

    private static LayoutBackupPosition CaptureLayoutBackupPosition(
        Guid boxId,
        DesktopBoxWindow window)
    {
        if (window.TryGetWindowBoundsPixels(out var bounds))
        {
            return new LayoutBackupPosition(boxId, bounds.Left, bounds.Top, IsPhysicalPixels: true);
        }

        return new LayoutBackupPosition(boxId, window.Left, window.Top);
    }

    internal static Point CalculateCenteredVisibleOrigin(Size visibleSize, Rect workArea)
    {
        if (!double.IsFinite(visibleSize.Width)
            || !double.IsFinite(visibleSize.Height)
            || visibleSize.Width < 0
            || visibleSize.Height < 0
            || workArea.IsEmpty
            || !double.IsFinite(workArea.Left)
            || !double.IsFinite(workArea.Top)
            || !double.IsFinite(workArea.Width)
            || !double.IsFinite(workArea.Height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workArea),
                "Visible size and work area must contain finite, non-negative dimensions.");
        }

        var centeredLeft = workArea.Left + ((workArea.Width - visibleSize.Width) / 2);
        var centeredTop = workArea.Top + ((workArea.Height - visibleSize.Height) / 2);
        var left = Math.Max(
            workArea.Left,
            Math.Min(centeredLeft, workArea.Right - visibleSize.Width));
        var top = Math.Max(
            workArea.Top,
            Math.Min(centeredTop, workArea.Bottom - visibleSize.Height));
        return new Point(left, top);
    }

    internal static Point CalculateClampedVisibleOrigin(Rect visibleBounds, Rect workArea)
    {
        var left = Math.Max(
            workArea.Left,
            Math.Min(visibleBounds.Left, workArea.Right - visibleBounds.Width));
        var top = Math.Max(
            workArea.Top,
            Math.Min(visibleBounds.Top, workArea.Bottom - visibleBounds.Height));
        return new Point(left, top);
    }

    internal static string GetLayoutBackupSettingKey(int slot)
    {
        if (slot is < 1 or > LayoutBackupSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Layout backup slot must be from 1 to 3.");
        }

        return LayoutBackupSettingPrefix + slot.ToString(CultureInfo.InvariantCulture);
    }

    internal static string SerializeLayoutBackup(IEnumerable<LayoutBackupPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var snapshot = positions.ToArray();
        if (!IsValidLayoutBackup(snapshot))
        {
            throw new ArgumentException("Layout backup contains invalid or duplicate box positions.", nameof(positions));
        }

        return JsonSerializer.Serialize(new LayoutBackupPayload(LayoutBackupVersion, snapshot));
    }

    internal static bool TryParseLayoutBackup(string? raw, out LayoutBackupPosition[] positions)
    {
        positions = [];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<LayoutBackupPayload>(raw);
            if (payload is null
                || (payload.Version != LayoutBackupVersion
                    && payload.Version != LegacyLayoutBackupVersion)
                || !IsValidLayoutBackup(payload.Positions))
            {
                return false;
            }

            positions = payload.Positions;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsValidLayoutBackup(LayoutBackupPosition[]? positions)
    {
        if (positions is null || positions.Length > MaxLayoutBackupPositions)
        {
            return false;
        }

        var ids = new HashSet<Guid>();
        return positions.All(position =>
            position.BoxId != Guid.Empty
            && double.IsFinite(position.Left)
            && double.IsFinite(position.Top)
            && ids.Add(position.BoxId));
    }

    internal readonly record struct LayoutBackupPosition(
        Guid BoxId,
        double Left,
        double Top,
        bool IsPhysicalPixels = false);

    public readonly record struct LayoutBackupRestoreResult(
        bool BackupFound,
        int RestoredCount,
        int MissingCount);

    private sealed record LayoutBackupPayload(int Version, LayoutBackupPosition[] Positions);

    private static void PlaceNewWindow(Window window, int index)
    {
        const double margin = 18;
        const double gap = 12;
        const double topPadding = 84;

        var workArea = SystemParameters.WorkArea;
        var centerX = workArea.Left + (workArea.Width - window.DesiredSize.Width) / 2;
        var centerY = workArea.Top + (workArea.Height - window.DesiredSize.Height) / 2;

        var offset = index * (window.DesiredSize.Width + gap);
        window.Left = Math.Max(workArea.Left + margin, Math.Min(centerX + offset, workArea.Right - window.DesiredSize.Width - margin));
        window.Top = Math.Max(workArea.Top + margin, Math.Min(centerY + topPadding * 0.5, workArea.Bottom - window.DesiredSize.Height - margin));
    }

    /// <summary>
    /// Nudges apart windows that were placed without a usable saved position this
    /// refresh (new boxes, or saved positions clamped by a resolution/monitor
    /// change). Windows restored from an intact saved position keep their exact
    /// spot — the user's arrangement is authoritative even when boxes overlap —
    /// but still count as obstacles so movable windows cascade around them.
    /// </summary>
    private void ResolveWindowOverlaps()
    {
        var entries = _windows.Values
            .Where(window => window.IsVisible)
            .OfType<DesktopBoxWindow>()
            .Select(window => (Window: window, Bounds: window.GetVisibleBoundsPixels()))
            .Where(entry => entry.Bounds.Width > 0 && entry.Bounds.Height > 0)
            .ToArray();

        // Restored positions are authoritative obstacles regardless of database order.
        var placed = entries
            .Where(entry => !_overlapResolutionBoxIds.Contains(entry.Window.ViewModel.BoxId))
            .Select(entry => entry.Bounds)
            .ToList();

        foreach (var entry in entries.Where(entry => _overlapResolutionBoxIds.Contains(entry.Window.ViewModel.BoxId)))
        {
            var bounds = entry.Bounds;
            var workArea = entry.Window.GetWorkAreaPixels();
            if (workArea.IsEmpty)
            {
                // A transient monitor query failure must not turn Rect.Empty's
                // infinities into an invalid native window position.
                placed.Add(bounds);
                continue;
            }

            var resolved = ResolveOverlapCascade(
                bounds,
                placed,
                workArea);

            if (Math.Abs(bounds.Left - resolved.Left) > 0.5
                || Math.Abs(bounds.Top - resolved.Top) > 0.5)
            {
                entry.Window.MoveToVisibleOriginPixels(resolved.Left, resolved.Top);
            }

            placed.Add(resolved);
        }
    }

    /// <summary>
    /// Cascades <paramref name="bounds"/> below/right of every rect in
    /// <paramref name="placed"/> until no overlap remains, then clamps the result
    /// into <paramref name="workArea"/>. Pure math kept separate from the window
    /// loop above so the collision rules stay unit-testable.
    /// </summary>
    internal static Rect ResolveOverlapCascade(
        Rect bounds,
        IReadOnlyList<Rect> placed,
        Rect workArea)
    {
        const double cascadeStep = 12;

        var moved = true;
        var guard = 0;
        while (moved && guard++ < 200)
        {
            moved = false;
            foreach (var other in placed)
            {
                if (!bounds.IntersectsWith(other))
                {
                    continue;
                }

                var nextLeft = bounds.Left;
                var nextTop = other.Bottom + cascadeStep;
                if (nextTop + bounds.Height > workArea.Bottom)
                {
                    // No room below; wrap to the right of the blocking box and
                    // restart from the top so the cascade stays on screen.
                    nextLeft = other.Right + cascadeStep;
                    nextTop = workArea.Top;
                }

                bounds = new Rect(nextLeft, nextTop, bounds.Width, bounds.Height);
                moved = true;
            }
        }

        // Clamp back into the work area so a wrapped cascade never leaves the box
        // hanging off the right/bottom edge.
        var clampedLeft = Math.Max(workArea.Left, Math.Min(bounds.Left, workArea.Right - bounds.Width));
        var clampedTop = Math.Max(workArea.Top, Math.Min(bounds.Top, workArea.Bottom - bounds.Height));
        if (Math.Abs(clampedLeft - bounds.Left) > 0.5
            || Math.Abs(clampedTop - bounds.Top) > 0.5)
        {
            bounds = new Rect(clampedLeft, clampedTop, bounds.Width, bounds.Height);
        }

        return bounds;
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (_isAdjustingPosition || _closing)
        {
            return;
        }

        if (sender is not DesktopBoxWindow draggedWindow)
        {
            return;
        }

        if (System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            HideGuides();
            return;
        }

        _isAdjustingPosition = true;
        try
        {
            PerformSnappingAndAlignment(draggedWindow, applySnap: false);
            UpdateMergePreview(draggedWindow);
        }
        finally
        {
            _isAdjustingPosition = false;
        }
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_isAdjustingPosition || _closing)
        {
            return;
        }

        if (sender is not DesktopBoxWindow resizedWindow)
        {
            return;
        }

        if (!e.HeightChanged && !e.WidthChanged)
        {
            return;
        }

        if (System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            HideGuides();
            return;
        }

        _isAdjustingPosition = true;
        try
        {
            if (e.HeightChanged)
            {
                PerformHeightResizeAlignment(resizedWindow, applySnap: true);
            }

            if (e.WidthChanged)
            {
                PerformWidthResizeAlignment(resizedWindow, applySnap: true);
            }
        }
        finally
        {
            _isAdjustingPosition = false;
        }
    }

    private void PerformHeightResizeAlignment(DesktopBoxWindow resizedWindow, bool applySnap = false)
    {
        var dpi = resizedWindow.GetDpiScale();
        var snapThreshold = 10.0 * dpi.DpiScaleX;
        var heightSnapThreshold = 10.0;

        var boundsA = resizedWindow.GetVisibleBoundsPixels();
        if (boundsA.IsEmpty)
        {
            HideHorizontalGuide();
            return;
        }

        double currentTop = boundsA.Top;
        double currentBottom = boundsA.Bottom;
        double currentLeft = boundsA.Left;
        double currentRight = boundsA.Right;

        var currentHeight = GetBoxHeight(resizedWindow);
        double? snappedHeight = null;
        double? snappedHeightDiff = null;

        var horizontalGuideYs = new List<double>();
        double horizontalGuideXMin = double.MaxValue;
        double horizontalGuideXMax = double.MinValue;

        foreach (var pair in _windows)
        {
            var otherWindow = pair.Value;
            if (otherWindow == resizedWindow || !otherWindow.IsVisible)
            {
                continue;
            }

            // Only snap between boxes in the same visual mode so DrawerCoverHeight
            // and MappingListHeight are compared with compatible units/meaning.
            if (currentHeight.HasValue
                && resizedWindow.ViewModel.IsMappingListMode == otherWindow.ViewModel.IsMappingListMode)
            {
                var otherHeight = GetBoxHeight(otherWindow);
                if (otherHeight.HasValue)
                {
                    var diff = Math.Abs(currentHeight.Value - otherHeight.Value);
                    if (diff <= heightSnapThreshold
                        && (!snappedHeightDiff.HasValue || diff < snappedHeightDiff.Value))
                    {
                        snappedHeight = otherHeight;
                        snappedHeightDiff = diff;
                    }
                }
            }

            var boundsB = otherWindow.GetVisibleBoundsPixels();
            if (boundsB.IsEmpty)
            {
                continue;
            }

            double topB = boundsB.Top;
            double bottomB = boundsB.Bottom;
            double heightB = boundsB.Height;
            double leftB = boundsB.Left;
            double rightB = boundsB.Right;

            // Align with another box's equal height.
            var equalHeightBottom = currentTop + heightB;
            var equalHeightActive = Math.Abs(currentBottom - equalHeightBottom) <= snapThreshold;
            if (equalHeightActive)
            {
                AddHorizontalGuide(horizontalGuideYs, ref horizontalGuideXMin, ref horizontalGuideXMax, equalHeightBottom, currentLeft, leftB, currentRight, rightB);
            }

            // Align bottom edge with another box's bottom edge.
            // 仅在等高线未命中时画：等高命中时它与等高线同一语义，两线并排会重影。
            if (!equalHeightActive && Math.Abs(currentBottom - bottomB) <= snapThreshold)
            {
                AddHorizontalGuide(horizontalGuideYs, ref horizontalGuideXMin, ref horizontalGuideXMax, bottomB, currentLeft, leftB, currentRight, rightB);
            }

            // Align bottom edge with another box's top edge.
            if (Math.Abs(currentBottom - topB) <= snapThreshold)
            {
                AddHorizontalGuide(horizontalGuideYs, ref horizontalGuideXMin, ref horizontalGuideXMax, topB, currentLeft, leftB, currentRight, rightB);
            }
        }

        if (applySnap && snappedHeight.HasValue)
        {
            if (resizedWindow.ViewModel.IsMappingListMode)
            {
                resizedWindow.ViewModel.ResizeMappingListHeight(snappedHeight.Value);
            }
            else
            {
                resizedWindow.ViewModel.ResizeDrawerCover(
                    resizedWindow.ViewModel.DrawerCoverWidth,
                    snappedHeight.Value);
            }
        }

        if (horizontalGuideYs.Count > 0 && horizontalGuideXMax > horizontalGuideXMin)
        {
            ShowHorizontalGuide(horizontalGuideYs, horizontalGuideXMin, horizontalGuideXMax - horizontalGuideXMin);
        }
        else
        {
            HideHorizontalGuide();
        }
    }

    /// <summary>
    /// 调整盒子宽度时的对齐：上下排列的盒子接近同一宽度时，
    /// 显示纵向虚线（等宽右缘/右缘/左缘对齐线），接近阈值内自动吸附宽度。
    /// 与 <see cref="PerformHeightResizeAlignment"/> 对称，只比较同视觉模式
    /// 的宽度（MappingListWidth 与 DrawerCoverWidth 单位不可混比）。
    /// </summary>
    private void PerformWidthResizeAlignment(DesktopBoxWindow resizedWindow, bool applySnap = false)
    {
        var dpi = resizedWindow.GetDpiScale();
        var snapThreshold = 10.0 * dpi.DpiScaleX;

        var boundsA = resizedWindow.GetVisibleBoundsPixels();
        if (boundsA.IsEmpty)
        {
            HideVerticalGuide();
            return;
        }

        double currentTop = boundsA.Top;
        double currentBottom = boundsA.Bottom;
        double currentLeft = boundsA.Left;
        double currentRight = boundsA.Right;

        var currentWidth = GetBoxWidth(resizedWindow);
        var currentPhysicalWidth = boundsA.Width;
        // 候选按物理宽度配对逻辑宽度：吸附判定在物理坐标系（与虚线一致），
        // 命中后用 chrome 补偿换算回本盒的目标逻辑宽度。
        var candidatePairs = new List<(double Physical, double Logical)>();
        // 右缘对齐吸附候选（与"右缘对齐虚线"一一对应）：把右缘移到目标 x。
        var edgeSnapCandidates = new List<(double TargetX, double Distance)>();

        var verticalGuideXs = new List<double>();
        double verticalGuideYMin = double.MaxValue;
        double verticalGuideYMax = double.MinValue;

        foreach (var pair in _windows)
        {
            var otherWindow = pair.Value;
            if (otherWindow == resizedWindow || !otherWindow.IsVisible)
            {
                continue;
            }

            // 同视觉模式才比较宽度值：列表模式宽度与抽屉封面宽度语义不同。
            if (currentWidth.HasValue
                && resizedWindow.ViewModel.IsMappingListMode == otherWindow.ViewModel.IsMappingListMode)
            {
                var otherWidth = GetBoxWidth(otherWindow);
                if (otherWidth.HasValue)
                {
                    candidatePairs.Add((otherWindow.GetVisibleBoundsPixels().Width, otherWidth.Value));
                }
            }

            var boundsB = otherWindow.GetVisibleBoundsPixels();
            if (boundsB.IsEmpty)
            {
                continue;
            }

            double topB = boundsB.Top;
            double bottomB = boundsB.Bottom;
            double widthB = boundsB.Width;
            double leftB = boundsB.Left;
            double rightB = boundsB.Right;

            // Align with another box's equal width (right edge follows the other's
            // right edge when the widths match).
            var equalWidthRight = currentLeft + widthB;
            var equalWidthActive = Math.Abs(currentRight - equalWidthRight) <= snapThreshold;
            if (equalWidthActive)
            {
                AddVerticalGuide(verticalGuideXs, ref verticalGuideYMin, ref verticalGuideYMax, equalWidthRight, currentTop, topB, currentBottom, bottomB);
            }

            // Align right edge with another box's right edge.
            // 仅在等宽线未命中时画：等宽命中时它与等宽线描述同一语义（宽度一致→右缘对齐），
            // 两线位置只差几像素并排会出现重影（用户看到"两条虚线"）。
            if (!equalWidthActive && Math.Abs(currentRight - rightB) <= snapThreshold)
            {
                AddVerticalGuide(verticalGuideXs, ref verticalGuideYMin, ref verticalGuideYMax, rightB, currentTop, topB, currentBottom, bottomB);
                edgeSnapCandidates.Add((rightB, Math.Abs(currentRight - rightB)));
            }

            // Align right edge with another box's left edge.
            if (Math.Abs(currentRight - leftB) <= snapThreshold)
            {
                AddVerticalGuide(verticalGuideXs, ref verticalGuideYMin, ref verticalGuideYMax, leftB, currentTop, topB, currentBottom, bottomB);
                edgeSnapCandidates.Add((leftB, Math.Abs(currentRight - leftB)));
            }
        }

        if (applySnap && currentWidth.HasValue)
        {
            double? targetLogical = null;
            var bestDistance = double.MaxValue;

            // 1) 等宽吸附：物理宽度接近对方（含 chrome 补偿）。
            if (candidatePairs.Count > 0)
            {
                var snappedPhysical = FindWidthSnapTarget(
                    currentPhysicalWidth,
                    candidatePairs.Select(pair => pair.Physical),
                    snapThreshold);
                if (snappedPhysical.HasValue)
                {
                    var match = candidatePairs.First(pair =>
                        Math.Abs(pair.Physical - snappedPhysical.Value) < 0.001);
                    var candidateLogical = ComputeSnappedLogicalWidth(
                        currentWidth.Value,
                        currentPhysicalWidth,
                        match.Logical,
                        match.Physical);
                    var distance = Math.Abs(currentPhysicalWidth - snappedPhysical.Value);
                    if (distance < bestDistance)
                    {
                        targetLogical = candidateLogical;
                        bestDistance = distance;
                    }
                }
            }

            // 2) 右缘对齐吸附：与"右缘对齐虚线"对应——右缘移到目标 x（吸到虚线上）。
            if (edgeSnapCandidates.Count > 0)
            {
                var (targetX, distance) = edgeSnapCandidates.MinBy(candidate => candidate.Distance);
                if (distance < bestDistance)
                {
                    targetLogical = ComputeEdgeSnapLogicalWidth(
                        currentLeft,
                        currentRight,
                        currentWidth.Value,
                        targetX);
                    bestDistance = distance;
                }
            }

            if (targetLogical.HasValue)
            {
                if (resizedWindow.ViewModel.IsMappingListMode)
                {
                    resizedWindow.ViewModel.ResizeMappingListWidth(targetLogical.Value);
                }
                else
                {
                    resizedWindow.ViewModel.ResizeDrawerCover(
                        targetLogical.Value,
                        resizedWindow.ViewModel.DrawerCoverHeight);
                }
            }
        }

        if (verticalGuideXs.Count > 0 && verticalGuideYMax > verticalGuideYMin)
        {
            ShowVerticalGuide(verticalGuideXs, verticalGuideYMin, verticalGuideYMax - verticalGuideYMin);
        }
        else
        {
            HideVerticalGuide();
        }
    }

    /// <summary>
    /// 计算把本盒右缘移到 <paramref name="targetX"/>（虚线位置）时所需的目标逻辑宽度。
    /// 化简后 = 当前逻辑宽 + (目标x − 当前右缘)，chrome 自然抵消。
    /// 用于"右缘对齐对方右缘/左缘"的吸附——与等宽吸附对应不同的虚线。
    /// </summary>
    internal static double ComputeEdgeSnapLogicalWidth(
        double currentLeft,
        double currentRight,
        double currentLogical,
        double targetX)
    {
        // chrome = (currentRight - currentLeft) - currentLogical；
        // 目标物理宽 = targetX - currentLeft；目标逻辑 = 目标物理宽 - chrome
        // = targetX - currentLeft - (currentRight - currentLeft) + currentLogical
        // = targetX - currentRight + currentLogical。
        return targetX - currentRight + currentLogical;
    }

    /// <summary>
    /// 计算吸附到对方宽度时本盒应设的目标逻辑宽度，使两盒**物理宽度**精确相等。
    /// 物理宽 = 逻辑宽 + 窗口边距（chrome）；不同预设 scale 的盒子 chrome 不同，
    /// 只把逻辑宽设成相同值会留下几像素的物理差，视觉上"感觉没对齐"。
    /// 补偿 = 对方逻辑宽 + 对方 chrome − 本盒 chrome。
    /// </summary>
    internal static double ComputeSnappedLogicalWidth(
        double currentLogical,
        double currentPhysical,
        double otherLogical,
        double otherPhysical)
    {
        var currentChrome = currentPhysical - currentLogical;
        var otherChrome = otherPhysical - otherLogical;
        return otherLogical + otherChrome - currentChrome;
    }

    /// <summary>
    /// 从候选宽度里挑最近且在阈值内的吸附目标；无命中返回 null。
    /// 抽成纯函数便于单元测试。
    /// </summary>
    internal static double? FindWidthSnapTarget(
        double currentWidth,
        IEnumerable<double> candidateWidths,
        double threshold)
    {
        double? best = null;
        double bestDiff = double.MaxValue;
        foreach (var candidate in candidateWidths)
        {
            if (!double.IsFinite(candidate) || candidate <= 0)
            {
                continue;
            }

            var diff = Math.Abs(currentWidth - candidate);
            if (diff <= threshold && diff < bestDiff)
            {
                best = candidate;
                bestDiff = diff;
            }
        }

        return best;
    }

    private static double? GetBoxWidth(DesktopBoxWindow window)
    {
        var viewModel = window.ViewModel;
        if (viewModel.IsMappingListMode)
        {
            return viewModel.MappingListWidth;
        }

        return viewModel.DrawerCoverWidth;
    }

    private static double? GetBoxHeight(DesktopBoxWindow window)
    {
        var viewModel = window.ViewModel;
        if (viewModel.IsMappingListMode)
        {
            var height = viewModel.MappingListHeight;
            return double.IsNaN(height) ? null : height;
        }

        return viewModel.DrawerCoverHeight;
    }

    private void OnWindowMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HideGuides();
        if (sender is not DesktopBoxWindow window)
        {
            ClearMergePreview();
            return;
        }

        // 拖动中预览到某宿主盒：松手即把被拖盒合并为该宿主的标签。
        if (_mergePreviewHost is DesktopBoxWindow hostWindow
            && hostWindow != window)
        {
            var memberId = window.ViewModel.BoxId;
            var hostId = hostWindow.ViewModel.BoxId;
            ClearMergePreview();
            FireAndForget.Run(
                MergeAsync(memberId, hostId),
                _logger,
                $"Failed to merge box {memberId:N} into host {hostId:N}.");
            return;
        }

        FireAndForget.Run(
            SavePositionAsync(window.ViewModel.BoxId),
            _logger,
            $"Failed to save position for box {window.ViewModel.BoxId:N}.");
        ClearMergePreview();
    }

    /// <summary>
    /// 拖动盒子的过程中更新"合并宿主预览"：若被拖盒中心落入某个可见盒子的
    /// 可见边界内，把该盒子标记为合并宿主并高亮；否则清除高亮。
    /// </summary>
    private void UpdateMergePreview(DesktopBoxWindow draggedWindow)
    {
        var draggedBounds = draggedWindow.GetVisibleBoundsPixels();
        if (draggedBounds.IsEmpty)
        {
            ClearMergePreview();
            return;
        }

        var candidateId = FindMergePreviewCandidateId(
            draggedBounds,
            _windows.Values
                .Where(w => w != draggedWindow && w.IsVisible)
                .Select(w => (w.ViewModel.BoxId, w.GetVisibleBoundsPixels())));

        DesktopBoxWindow? candidate = null;
        if (candidateId is Guid id && _windows.TryGetValue(id, out var matched))
        {
            candidate = matched;
        }

        if (ReferenceEquals(candidate, _mergePreviewHost))
        {
            return;
        }

        ClearMergePreview();
        if (candidate is not null)
        {
            _mergePreviewHost = candidate;
            candidate.IsMergePreviewTarget = true;
        }
    }

    /// <summary>
    /// 纯函数：从候选盒子中找中心点落入其可见边界内的那个（作为合并宿主）。
    /// 抽取出来便于单元测试。返回 null 表示无重叠目标。
    /// </summary>
    internal static Guid? FindMergePreviewCandidateId(
        Rect draggedBounds,
        IEnumerable<(Guid BoxId, Rect Bounds)> candidates)
    {
        var center = new Point(
            draggedBounds.Left + draggedBounds.Width / 2.0,
            draggedBounds.Top + draggedBounds.Height / 2.0);

        foreach (var (boxId, bounds) in candidates)
        {
            if (bounds.IsEmpty || !bounds.Contains(center))
            {
                continue;
            }

            return boxId;
        }

        return null;
    }

    private void ClearMergePreview()
    {
        if (_mergePreviewHost is { } host)
        {
            host.IsMergePreviewTarget = false;
            _mergePreviewHost = null;
        }
    }

    private void HideGuides()
    {
        HideVerticalGuide();
        HideHorizontalGuide();
    }

    private void ShowVerticalGuide(IReadOnlyList<double> xs, double yStart, double height)
    {
        if (_verticalGuide == null)
        {
            _verticalGuide = new GuideLineWindow(true);
        }
        if (!_verticalGuide.IsVisible)
        {
            _verticalGuide.Show();
        }
        _verticalGuide.UpdateLines(xs.Select(x => (x, yStart, x, yStart + height)).ToArray());
    }

    private void HideVerticalGuide()
    {
        _verticalGuide?.Hide();
    }

    private void ShowHorizontalGuide(IReadOnlyList<double> ys, double xStart, double width)
    {
        if (_horizontalGuide == null)
        {
            _horizontalGuide = new GuideLineWindow(false);
        }
        if (!_horizontalGuide.IsVisible)
        {
            _horizontalGuide.Show();
        }
        _horizontalGuide.UpdateLines(ys.Select(y => (xStart, y, xStart + width, y)).ToArray());
    }

    private void HideHorizontalGuide()
    {
        _horizontalGuide?.Hide();
    }

    private void PerformSnappingAndAlignment(DesktopBoxWindow draggedWindow, bool applySnap = true)
    {
        var dpi = draggedWindow.GetDpiScale();
        var snapThreshold = 10.0 * dpi.DpiScaleX;
        var visualGap = 8.0 * dpi.DpiScaleX;
        var tightGap = 1.0 * dpi.DpiScaleX;

        var boundsA = draggedWindow.GetVisibleBoundsPixels();
        if (boundsA.IsEmpty)
        {
            HideGuides();
            return;
        }
        double currentLeft = boundsA.Left;
        double currentTop = boundsA.Top;
        double width = boundsA.Width;
        double height = boundsA.Height;
        double rightA = boundsA.Right;
        double bottomA = boundsA.Bottom;
        double hCenterA = currentLeft + width / 2.0;
        double vCenterA = currentTop + height / 2.0;
        double? bestSnappedVisibleLeft = null;
        int bestLeftPriority = int.MaxValue;
        double bestLeftDistance = double.MaxValue;
        double? bestSnappedVisibleTop = null;
        int bestTopPriority = int.MaxValue;
        double bestTopDistance = double.MaxValue;

        var verticalGuideXs = new List<double>();
        double verticalGuideYMin = double.MaxValue;
        double verticalGuideYMax = double.MinValue;

        var horizontalGuideYs = new List<double>();
        double horizontalGuideXMin = double.MaxValue;
        double horizontalGuideXMax = double.MinValue;

        foreach (var pair in _windows)
        {
            var otherWindow = pair.Value;
            if (otherWindow == draggedWindow || !otherWindow.IsVisible)
            {
                continue;
            }

            var boundsB = otherWindow.GetVisibleBoundsPixels();
            if (boundsB.IsEmpty)
            {
                continue;
            }
            double leftB = boundsB.Left;
            double topB = boundsB.Top;
            double widthB = boundsB.Width;
            double heightB = boundsB.Height;
            double rightB = boundsB.Right;
            double bottomB = boundsB.Bottom;
            double hCenterB = leftB + widthB / 2.0;
            double vCenterB = topB + heightB / 2.0;

            // 1. Vertical snapping - collect every matching guide line.
            if (Math.Abs(currentLeft - leftB) <= snapThreshold)
            {
                AddVerticalGuide(verticalGuideXs, ref verticalGuideYMin, ref verticalGuideYMax, leftB, currentTop, topB, bottomA, bottomB);
            }
            if (Math.Abs(rightA - rightB) <= snapThreshold)
            {
                AddVerticalGuide(verticalGuideXs, ref verticalGuideYMin, ref verticalGuideYMax, rightB, currentTop, topB, bottomA, bottomB);
            }
            if (Math.Abs(currentLeft - (rightB + tightGap)) <= snapThreshold)
            {
                AddVerticalGuide(verticalGuideXs, ref verticalGuideYMin, ref verticalGuideYMax, rightB + tightGap / 2.0, currentTop, topB, bottomA, bottomB);
            }
            if (Math.Abs(rightA - (leftB - tightGap)) <= snapThreshold)
            {
                AddVerticalGuide(verticalGuideXs, ref verticalGuideYMin, ref verticalGuideYMax, leftB - tightGap / 2.0, currentTop, topB, bottomA, bottomB);
            }
            if (Math.Abs(hCenterA - hCenterB) <= snapThreshold)
            {
                AddVerticalGuide(verticalGuideXs, ref verticalGuideYMin, ref verticalGuideYMax, hCenterB, currentTop, topB, bottomA, bottomB);
            }

            // 1b. Vertical snap target (nearest candidate wins, priority breaks ties).
            if (Math.Abs(currentLeft - leftB) <= snapThreshold)
            {
                TryUpdateSnapTarget(ref bestSnappedVisibleLeft, ref bestLeftPriority, ref bestLeftDistance, leftB, 1, currentLeft);
            }
            if (Math.Abs(rightA - rightB) <= snapThreshold)
            {
                TryUpdateSnapTarget(ref bestSnappedVisibleLeft, ref bestLeftPriority, ref bestLeftDistance, rightB - width, 2, currentLeft);
            }
            if (Math.Abs(currentLeft - (rightB + tightGap)) <= snapThreshold)
            {
                TryUpdateSnapTarget(ref bestSnappedVisibleLeft, ref bestLeftPriority, ref bestLeftDistance, rightB + tightGap, 3, currentLeft);
            }
            if (Math.Abs(rightA - (leftB - tightGap)) <= snapThreshold)
            {
                TryUpdateSnapTarget(ref bestSnappedVisibleLeft, ref bestLeftPriority, ref bestLeftDistance, leftB - tightGap - width, 4, currentLeft);
            }
            if (Math.Abs(hCenterA - hCenterB) <= snapThreshold)
            {
                TryUpdateSnapTarget(ref bestSnappedVisibleLeft, ref bestLeftPriority, ref bestLeftDistance, hCenterB - width / 2.0, 5, currentLeft);
            }
            // 2. Horizontal snapping - collect every matching guide line.
            if (Math.Abs(currentTop - topB) <= snapThreshold)
            {
                AddHorizontalGuide(horizontalGuideYs, ref horizontalGuideXMin, ref horizontalGuideXMax, topB, currentLeft, leftB, rightA, rightB);
            }
            if (Math.Abs(bottomA - bottomB) <= snapThreshold)
            {
                AddHorizontalGuide(horizontalGuideYs, ref horizontalGuideXMin, ref horizontalGuideXMax, bottomB, currentLeft, leftB, rightA, rightB);
            }
            if (Math.Abs(currentTop - (bottomB + visualGap)) <= snapThreshold)
            {
                AddHorizontalGuide(horizontalGuideYs, ref horizontalGuideXMin, ref horizontalGuideXMax, bottomB + visualGap / 2.0, currentLeft, leftB, rightA, rightB);
            }
            if (Math.Abs(bottomA - (topB - visualGap)) <= snapThreshold)
            {
                AddHorizontalGuide(horizontalGuideYs, ref horizontalGuideXMin, ref horizontalGuideXMax, topB - visualGap / 2.0, currentLeft, leftB, rightA, rightB);
            }
            if (Math.Abs(vCenterA - vCenterB) <= snapThreshold)
            {
                AddHorizontalGuide(horizontalGuideYs, ref horizontalGuideXMin, ref horizontalGuideXMax, vCenterB, currentLeft, leftB, rightA, rightB);
            }

            // 2b. Horizontal snap target (nearest candidate wins, priority breaks ties).
            if (Math.Abs(currentTop - topB) <= snapThreshold)
            {
                TryUpdateSnapTarget(ref bestSnappedVisibleTop, ref bestTopPriority, ref bestTopDistance, topB, 1, currentTop);
            }
            if (Math.Abs(bottomA - bottomB) <= snapThreshold)
            {
                TryUpdateSnapTarget(ref bestSnappedVisibleTop, ref bestTopPriority, ref bestTopDistance, bottomB - height, 2, currentTop);
            }
            if (Math.Abs(currentTop - (bottomB + visualGap)) <= snapThreshold)
            {
                TryUpdateSnapTarget(ref bestSnappedVisibleTop, ref bestTopPriority, ref bestTopDistance, bottomB + visualGap, 3, currentTop);
            }
            if (Math.Abs(bottomA - (topB - visualGap)) <= snapThreshold)
            {
                TryUpdateSnapTarget(ref bestSnappedVisibleTop, ref bestTopPriority, ref bestTopDistance, topB - visualGap - height, 4, currentTop);
            }
            if (Math.Abs(vCenterA - vCenterB) <= snapThreshold)
            {
                TryUpdateSnapTarget(ref bestSnappedVisibleTop, ref bestTopPriority, ref bestTopDistance, vCenterB - height / 2.0, 5, currentTop);
            }
        }
        if (applySnap)
        {
            if (bestSnappedVisibleLeft.HasValue || bestSnappedVisibleTop.HasValue)
            {
                draggedWindow.MoveToVisibleOriginPixels(
                    bestSnappedVisibleLeft ?? currentLeft,
                    bestSnappedVisibleTop ?? currentTop);
            }
        }

        if (verticalGuideXs.Count > 0 && verticalGuideYMax > verticalGuideYMin)
        {
            ShowVerticalGuide(verticalGuideXs, verticalGuideYMin, verticalGuideYMax - verticalGuideYMin);
        }
        else
        {
            HideVerticalGuide();
        }

        if (horizontalGuideYs.Count > 0 && horizontalGuideXMax > horizontalGuideXMin)
        {
            ShowHorizontalGuide(horizontalGuideYs, horizontalGuideXMin, horizontalGuideXMax - horizontalGuideXMin);
        }
        else
        {
            HideHorizontalGuide();
        }
    }


    private static void TryUpdateSnapTarget(
        ref double? bestTarget,
        ref int bestPriority,
        ref double bestDistance,
        double target,
        int priority,
        double current)
    {
        var distance = Math.Abs(target - current);
        if (!bestTarget.HasValue
            || distance < bestDistance
            || (distance == bestDistance && priority < bestPriority))
        {
            bestTarget = target;
            bestPriority = priority;
            bestDistance = distance;
        }
    }
    private static void AddVerticalGuide(
        List<double> guideXs,
        ref double yMin,
        ref double yMax,
        double x,
        double currentTop,
        double topB,
        double bottomA,
        double bottomB)
    {
        if (!guideXs.Contains(x))
        {
            guideXs.Add(x);
        }
        yMin = Math.Min(yMin, Math.Min(currentTop, topB));
        yMax = Math.Max(yMax, Math.Max(bottomA, bottomB));
    }

    private static void AddHorizontalGuide(
        List<double> guideYs,
        ref double xMin,
        ref double xMax,
        double y,
        double currentLeft,
        double leftB,
        double rightA,
        double rightB)
    {
        if (!guideYs.Contains(y))
        {
            guideYs.Add(y);
        }
        xMin = Math.Min(xMin, Math.Min(currentLeft, leftB));
        xMax = Math.Max(xMax, Math.Max(rightA, rightB));
    }

}
