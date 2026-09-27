using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using WitchDrawer.App.Infrastructure;
using WitchDrawer.App.ViewModels;
using WitchDrawer.App.Views;
using WitchDrawer.Core;
using WitchDrawer.Core.Logging;
using WitchDrawer.Core.Services;
using WitchDrawer.Core.Storage;
using WitchDrawer.Native.Files;
using WitchDrawer.Native.Shell;
using WitchDrawer.Native.Windows;

namespace WitchDrawer.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\WitchDrawer.SingleInstance";
    private const string SingleInstancePipeName = "WitchDrawer.SingleInstance";
    private const string ActivateInstanceCommand = "activate";
    private const string ThemeSettingKey = "Theme";

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _singleInstancePipeCts;
    private TaskbarIcon? _taskbarIcon;
    private MainWindow? _mainWindow;
    private DesktopBoxManager? _desktopBoxManager;
    private IAppLogger? _logger;
    private WorkingSetTrimmer? _workingSetTrimmer;
    private int _shutdownStarted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 启动阶段计时探针：用 [perf] 前缀的结构化日志记录各阶段耗时，
        // 便于对比优化前后的启动延迟（见 tools/perf 测量脚本）。
        var startupWatch = System.Diagnostics.Stopwatch.StartNew();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (ProcessElevation.RequiresUnelevatedRelaunch())
        {
            var executablePath = Environment.ProcessPath;
            var nativeErrorCode = 0;
            if (!string.IsNullOrWhiteSpace(executablePath)
                && ProcessElevation.TryRelaunchCurrentProcessUnelevated(
                    executablePath,
                    e.Args,
                    AppContext.BaseDirectory,
                    out nativeErrorCode))
            {
                Shutdown(0);
                return;
            }

            MessageBox.Show(
                $"WitchDrawer 当前以管理员身份运行，Windows 会阻止桌面文件拖入盒子。\n\n"
                + $"自动切换到普通权限失败（错误码 {nativeErrorCode}）。请退出后直接双击启动，不要选择“以管理员身份运行”。",
                "WitchDrawer 无法接收桌面拖放",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(-1);
            return;
        }

        var silentStart = StartupLaunchPolicy.IsSilent(e.Args);

        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (StartupLaunchPolicy.ShouldActivateExistingInstance(e.Args))
            {
                await SignalExistingInstanceAsync();
            }

            Shutdown(0);
            return;
        }

        try
        {
            // ForCurrentUser 会创建目录并校验可写性（SQLite WAL 需要同目录旁路文件）。
            var paths = AppPaths.ForCurrentUser();

            var logger = new FileAppLogger(paths.LogsDirectory);
            _logger = logger;
            var shortcutMigration = await Task.Run(() =>
                StartupShortcutMigration.EnsureSilentArguments(
                    Environment.ProcessPath,
                    StartupLaunchPolicy.SilentArgument));
            if (shortcutMigration.UpdatedCount > 0)
            {
                logger.Info(
                    $"Added silent startup arguments to {shortcutMigration.UpdatedCount} legacy shortcut(s).");
            }

            foreach (var exception in shortcutMigration.Errors)
            {
                logger.Error(exception, "Failed to update a legacy startup shortcut.");
            }

            var runKeyMigration = await Task.Run(() =>
                StartupRunKeyMigration.EnsureSilentArguments(
                    Environment.ProcessPath,
                    StartupLaunchPolicy.SilentArgument));
            if (runKeyMigration.UpdatedCount > 0)
            {
                logger.Info(
                    $"Added silent startup argument to the {runKeyMigration.UpdatedCount} legacy Run registry value(s).");
            }

            foreach (var exception in runKeyMigration.Errors)
            {
                logger.Error(exception, "Failed to update the legacy startup Run registry value.");
            }

            var repository = new DrawerRepository(paths.DatabasePath);
            var drawerService = new DrawerService(paths, repository);
            var launcher = new ShellFileLauncher();
            var todoService = new TodoService(repository);
            var updateService = new UpdateService(logger);
            await updateService.CleanupLegacyUpdaterArtifactsAsync();
            var quickPanelHotKeySettings = new QuickPanelHotKeySettingsStore(drawerService);
            var boxVisualStyleStore = new BoxVisualStyleStore(drawerService, logger);
            var boxPositionLockStateStore =
                new BoxPositionLockStateStore(drawerService, logger);
            var boxTabGroupService =
                new BoxTabGroupService(drawerService, logger);
            var storageLocationStore = StorageLocationStore.ForCurrentUser();
            var dataStorageMigrationService =
                new DataStorageMigrationService(paths, repository, storageLocationStore);

            logger.Info($"[perf] services constructed at {startupWatch.ElapsedMilliseconds} ms, ws {Environment.WorkingSet / (1024 * 1024)} MB.");
            logger.Info("Data directory: " + paths.RootDirectory);
            logger.Info("Database path: " + paths.DatabasePath);

            var dataInitWatch = System.Diagnostics.Stopwatch.StartNew();
            var quickPanelHotKey = await InitializeDataAndLoadQuickPanelHotKeyAsync(
                drawerService,
                quickPanelHotKeySettings);
            logger.Info($"[perf] database initialized in {dataInitWatch.ElapsedMilliseconds} ms.");
            AppThemeManager.Apply(await LoadSavedThemeAsync(drawerService));

            var quickPanelViewModel = new QuickPanelViewModel(
                drawerService,
                launcher,
                logger,
                boxVisualStyleStore);
            // 快捷面板窗口不参与启动路径：它只在用户按下热键时才需要显示，而首次构造
            // 一个 AllowsTransparency 窗口要付整套 XAML 首次解析成本（实测 280-420 ms）。
            // 改为启动路径跑完后在 UI 空闲时预热，既不拖慢启动，也不让首次唤出变慢。
            QuickPanelWindow CreateQuickPanel() => new(quickPanelViewModel);
            var mainViewModel = new MainViewModel(
                drawerService,
                todoService,
                launcher,
                logger,
                quickPanelViewModel,
                updateService,
                boxVisualStyleStore,
                boxPositionLockStateStore,
                paths,
                dataStorageMigrationService);
            _desktopBoxManager = new DesktopBoxManager(
                drawerService,
                todoService,
                launcher,
                logger,
                boxVisualStyleStore,
                boxPositionLockStateStore,
                boxTabGroupService,
                // 开机自启（--silent）时不启用“双击桌面切换图标”的自动触发：
                // 自启后程序后台挂着全局鼠标钩子，用户在桌面上双击（哪怕只是双击
                // 图标想打开程序）会被判定为“桌面空白双击”而切走全部图标（尤其多
                // 显示器下 ListView 不可见时误判为空白）。保持设置页里手动的
                // “隐藏桌面图标”开关不受影响；双击切换仅在可交互会话里生效。
                () => mainViewModel.IsDesktopDoubleClickEnabled && !silentStart);
            var mainWindowWatch = System.Diagnostics.Stopwatch.StartNew();
            _mainWindow = new MainWindow(
                mainViewModel,
                CreateQuickPanel,
                logger,
                quickPanelHotKeySettings,
                quickPanelHotKey);
            logger.Info($"[perf] main window constructed in {mainWindowWatch.ElapsedMilliseconds} ms.");
            _desktopBoxManager.ShowDesktopActivated += (_, _) =>
                _mainWindow.SendBehindDesktop();
            StartSingleInstanceServer(logger);
            logger.Info($"[perf] view models and windows constructed at {startupWatch.ElapsedMilliseconds} ms, ws {Environment.WorkingSet / (1024 * 1024)} MB.");

            // 这些事件处理器是 async void：刷新期间的异常（如 SQLite 写入失败）会直接逃出
            // 成为进程级未处理异常，必须就地捕获记录。
            mainViewModel.BoxesChanged += async (_, _) =>
                await GuardRefreshAsync(() => _desktopBoxManager.RefreshAsync(), "RefreshAsync", logger);
            mainViewModel.ItemsChanged += async (_, eventArgs) =>
                await GuardRefreshAsync(() => _desktopBoxManager.RefreshItemsAsync(eventArgs.BoxId), "RefreshItemsAsync", logger);
            _desktopBoxManager.ItemsChanged += async (_, eventArgs) =>
                await GuardRefreshAsync(
                    () => mainViewModel.ReloadItemsFromDesktopAsync(eventArgs.BoxId),
                    "ReloadItemsFromDesktopAsync",
                    logger);
            _desktopBoxManager.DesktopBackgroundDoubleClicked += (_, _) =>
            {
                if (mainViewModel.ToggleDesktopIconsCommand.CanExecute(null))
                {
                    mainViewModel.ToggleDesktopIconsCommand.Execute(null);
                }
            };
            _mainWindow.ReopenBoxRequested += async (_, boxId) => await _desktopBoxManager.ShowAsync(boxId);
            _mainWindow.RecordLayoutBackupRequested += async (_, slot) =>
                await GuardRefreshAsync(
                    async () =>
                    {
                        var count = await _desktopBoxManager.RecordLayoutBackupAsync(slot);
                        _mainWindow.SetLayoutBackupSlotState(slot, hasBackup: true);
                        mainViewModel.ReportStatus($"已将 {count} 个盒子记录到布局备份槽位 {slot}");
                    },
                    "RecordLayoutBackupAsync",
                    logger);
            _mainWindow.RestoreLayoutBackupRequested += async (_, slot) =>
                await GuardRefreshAsync(
                    async () =>
                    {
                        var result = await _desktopBoxManager.RestoreLayoutBackupAsync(slot);
                        if (!result.BackupFound)
                        {
                            _mainWindow.SetLayoutBackupSlotState(slot, hasBackup: false);
                            mainViewModel.ReportStatus($"布局备份槽位 {slot} 为空或不可用");
                            return;
                        }

                        var missingText = result.MissingCount > 0
                            ? $"，另有 {result.MissingCount} 个已删除盒子被跳过"
                            : string.Empty;
                        mainViewModel.ReportStatus(
                            $"已从布局备份槽位 {slot} 恢复 {result.RestoredCount} 个盒子{missingText}");
                    },
                    "RestoreLayoutBackupAsync",
                    logger);
            _mainWindow.DeleteLayoutBackupRequested += async (_, slot) =>
                await GuardRefreshAsync(
                    async () =>
                    {
                        var deleted = await _desktopBoxManager.DeleteLayoutBackupAsync(slot);
                        _mainWindow.SetLayoutBackupSlotState(slot, hasBackup: false);
                        mainViewModel.ReportStatus(
                            deleted
                                ? $"已删除布局备份槽位 {slot}"
                                : $"布局备份槽位 {slot} 已经为空");
                    },
                    "DeleteLayoutBackupAsync",
                    logger);
            _mainWindow.RecallBoxToScreenCenterRequested += async (_, boxId) =>
                await GuardRefreshAsync(
                    async () =>
                    {
                        if (await _desktopBoxManager.CenterBoxOnScreenAsync(boxId))
                        {
                            mainViewModel.ReportStatus("已将盒子召回主屏中心");
                        }
                    },
                    "CenterBoxOnScreenAsync",
                    logger);
            _mainWindow.DesktopShellRestarted += async (_, _) =>
                await _desktopBoxManager.RecoverDesktopHostsAsync();
            _mainWindow.MergeBoxRequested += async (_, e) =>
                await GuardRefreshAsync(
                    () => _desktopBoxManager.MergeAsync(e.MemberId, e.HostId),
                    "MergeBoxAsync",
                    logger);
            _mainWindow.DissolveTabGroupRequested += async (_, boxId) =>
                await GuardRefreshAsync(
                    () => _desktopBoxManager.DissolveAsync(boxId),
                    "DissolveTabGroupAsync",
                    logger);
            mainViewModel.UpdateRequested += async (_, result) =>
            {
                var versionText = $"v{result.LatestVersion.Major}.{result.LatestVersion.Minor}.{result.LatestVersion.Build}";
                var dialogResult = System.Windows.MessageBox.Show(
                    $"发现新版本 {versionText}\n\n是否立即更新？\n更新将自动下载并重启应用。",
                    "发现新版本",
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxImage.Question);

                if (dialogResult == System.Windows.MessageBoxResult.OK)
                {
                    await mainViewModel.ExecuteUpdateAsync(result.DownloadUrl);
                }
            };

            mainViewModel.UpdateConfirmed += async (_, _) =>
            {
                await PerformShutdownAsync();
            };

            var layoutBackupStates = await Task.WhenAll(
                Enumerable.Range(1, 3).Select(async slot =>
                    (Slot: slot, HasBackup: await _desktopBoxManager.HasLayoutBackupAsync(slot))));
            foreach (var state in layoutBackupStates)
            {
                _mainWindow.SetLayoutBackupSlotState(state.Slot, state.HasBackup);
            }

            InitializeTaskbarIcon(paths, logger);

            MainWindow = _mainWindow;
            // 1. 先创建桌面盒（无入场动画），让用户最快看到抽屉
            await _desktopBoxManager.RefreshAsync(animateNewWindows: false);
            logger.Info($"[perf] desktop boxes visible at {startupWatch.ElapsedMilliseconds} ms, ws {Environment.WorkingSet / (1024 * 1024)} MB, managed {GC.GetTotalMemory(false) / (1024 * 1024)} MB.");
            // 2. 再加载主窗口数据（会触发 BoxesChanged，但桌面盒已存在，只做增量更新）
            await mainViewModel.LoadAsync();
            logger.Info($"[perf] main view model loaded at {startupWatch.ElapsedMilliseconds} ms, ws {Environment.WorkingSet / (1024 * 1024)} MB.");
            // 3. 最后显示主窗口（非静默），避免主窗口渲染与桌面盒创建同时抢占 GPU/CPU
            if (silentStart)
            {
                _mainWindow.MinimizeToTray();
            }
            else
            {
                _mainWindow.Show();
                logger.Info($"[perf] main window shown at {startupWatch.ElapsedMilliseconds} ms, ws {Environment.WorkingSet / (1024 * 1024)} MB.");
            }
            // QuickPanel 只在用户真正打开时才加载（ToggleAsync 内部会 LoadAsync），
            // 启动时不再预加载，减少启动期的 I/O 与 CPU 竞争。
            await updateService.ConfirmUpdateStartupAsync();
            logger.Info($"[perf] startup complete at {startupWatch.ElapsedMilliseconds} ms, ws {Environment.WorkingSet / (1024 * 1024)} MB, managed {GC.GetTotalMemory(false) / (1024 * 1024)} MB.");

            // 启动路径结束后才开始空闲计时：首屏渲染期间 WPF 与显卡驱动按需载入了大量
            // 代码页，此后若用户不用这个应用，它们就没有继续驻留的必要（见 WorkingSetTrimmer）。
            _workingSetTrimmer = new WorkingSetTrimmer(logger);
            _workingSetTrimmer.Start();

            // 启动路径已跑完：在 UI 空闲时预热快捷面板窗口，避开首屏渲染与启动 I/O 的争抢。
            _ = Dispatcher.InvokeAsync(
                () =>
                {
                    try
                    {
                        _mainWindow?.PrewarmQuickPanel();
                    }
                    catch (Exception exception)
                    {
                        logger.Error(exception, "Failed to prewarm the quick panel window.");
                    }
                },
                System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception exception)
        {
            var sb = new System.Text.StringBuilder();
            var ex = exception;
            while (ex != null)
            {
                sb.AppendLine(ex.GetType().Name + ": " + ex.Message);
                sb.AppendLine(ex.StackTrace);
                sb.AppendLine("---");
                ex = ex.InnerException;
            }
            MessageBox.Show(
                sb.ToString(),
                "WitchDrawer startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    internal static async Task<QuickPanelHotKey> InitializeDataAndLoadQuickPanelHotKeyAsync(
        DrawerService drawerService,
        QuickPanelHotKeySettingsStore quickPanelHotKeySettings,
        CancellationToken cancellationToken = default)
    {
        await drawerService.InitializeAsync(cancellationToken);
        return await quickPanelHotKeySettings.LoadAsync(cancellationToken);
    }

    private static async Task<AppTheme> LoadSavedThemeAsync(DrawerService drawerService)
    {
        var savedTheme = await drawerService.GetSettingAsync(ThemeSettingKey);
        return Enum.TryParse<AppTheme>(savedTheme, ignoreCase: true, out var theme)
            ? theme
            : AppTheme.Moe;
    }

    private static async Task GuardRefreshAsync(Func<Task> refresh, string operationName, IAppLogger logger)
    {
        try
        {
            await refresh();
        }
        catch (Exception exception)
        {
            logger.Error(exception, $"Desktop box refresh failed during {operationName}.");
        }
    }

    private void StartSingleInstanceServer(IAppLogger logger)
    {
        _singleInstancePipeCts = new CancellationTokenSource();
        var cancellationToken = _singleInstancePipeCts.Token;

        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        SingleInstancePipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var command = await reader.ReadLineAsync(cancellationToken);
                    if (string.Equals(command, ActivateInstanceCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        await Dispatcher.InvokeAsync(ActivateExistingMainWindow);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Single-instance pipe server failed.");
                    await Task.Delay(250, cancellationToken);
                }
            }
        }, cancellationToken);
    }

    private static async Task SignalExistingInstanceAsync()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    SingleInstancePipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
                await client.ConnectAsync(timeoutCts.Token);
                await using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync(ActivateInstanceCommand);
                return;
            }
            catch
            {
                await Task.Delay(120);
            }
        }
    }

    private void ActivateExistingMainWindow()
    {
        _mainWindow?.RestoreFromTray();
    }

    private void InitializeTaskbarIcon(AppPaths paths, IAppLogger logger)
    {
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

        if (!File.Exists(iconPath))
        {
            iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
        }

        if (!File.Exists(iconPath))
        {
            iconPath = ExtractEmbeddedIcon();
        }

        _taskbarIcon = new TaskbarIcon(nint.Zero, iconPath, "WitchDrawer");

        _taskbarIcon.LeftClick += (_, _) =>
        {
            if (_mainWindow is null)
            {
                return;
            }

            if (_mainWindow.IsVisible)
            {
                _mainWindow.MinimizeToTray();
            }
            else
            {
                _mainWindow.RestoreFromTray();
            }
        };

        _taskbarIcon.RightClick += (_, _) =>
        {
            if (_mainWindow is null)
            {
                return;
            }

            var menu = CreatePopupMenu();
            var showOrHideText = _mainWindow.IsVisible ? "隐藏主窗口" : "显示主窗口";
            AppendMenuW(menu, 0, 1, showOrHideText);
            AppendMenuW(menu, 0, 2, "显示全部收纳盒");
            AppendMenuW(menu, 0, 3, "退出 WitchDrawer");

            var pt = GetCursorPosition();
            _taskbarIcon.ShowContextMenu(menu, pt.X, pt.Y);
            DestroyMenu(menu);
        };

        _taskbarIcon.MenuCommand += async (_, e) =>
        {
            switch (e.CommandId)
            {
                case 1:
                    if (_mainWindow is null)
                    {
                        return;
                    }

                    if (_mainWindow.IsVisible)
                    {
                        _mainWindow.MinimizeToTray();
                    }
                    else
                    {
                        _mainWindow.RestoreFromTray();
                    }
                    break;
                case 2:
                    if (_desktopBoxManager is not null)
                    {
                        await GuardRefreshAsync(
                            () => _desktopBoxManager.ShowAllAsync(),
                            "ShowAllAsync",
                            logger);
                    }
                    break;
                case 3:
                    await PerformShutdownAsync();
                    break;
            }
        };

        _taskbarIcon.Show();
    }

    private async Task PerformShutdownAsync()
    {
        var shutdownWatch = System.Diagnostics.Stopwatch.StartNew();
        _logger?.Info("[perf] shutdown requested.");
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        _taskbarIcon?.Dispose();
        _taskbarIcon = null;

        var desktopBoxManager = _desktopBoxManager;
        _desktopBoxManager = null;
        if (desktopBoxManager is not null)
        {
            try
            {
                // Keep the dispatcher free while positions are saved and desktop windows close.
                // A synchronous wait here deadlocks because CloseAllAsync resumes on the UI thread.
                await desktopBoxManager.CloseAllAsync().WaitAsync(TimeSpan.FromSeconds(10));
                _logger?.Info($"[perf] desktop boxes closed at {shutdownWatch.ElapsedMilliseconds} ms.");
            }
            catch (TimeoutException exception)
            {
                _logger?.Error(exception, "Timed out while closing desktop boxes during shutdown.");
            }
            catch (Exception exception)
            {
                // Shutdown must remain available even if position persistence or native cleanup fails.
                _logger?.Error(exception, "Failed to close desktop boxes during shutdown.");
            }
        }

        if (_mainWindow is not null)
        {
            _mainWindow.ForceClose();
            _mainWindow = null;
            _logger?.Info($"[perf] main window closed at {shutdownWatch.ElapsedMilliseconds} ms.");
        }

        _logger?.Info($"[perf] shutdown dispatching at {shutdownWatch.ElapsedMilliseconds} ms.");
        Shutdown(0);
    }

    /// <summary>
    /// 数据目录迁移后的重启：先布置一个分离的"等待当前进程退出再启动"的辅助进程，
    /// 再走完整关闭流程。直接启动新进程会被单实例检测吸收（新实例信号旧实例后自行退出），
    /// 导致重启落空；绕过 PerformShutdownAsync 又可能丢掉位置保存等收尾工作。
    /// </summary>
    internal async Task RestartApplicationAsync()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var arguments =
                    "-NoProfile -WindowStyle Hidden -Command \""
                    + $"while (Get-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue) "
                    + "{ Start-Sleep -Milliseconds 300 }; "
                    + $"Start-Process -FilePath '{processPath}' -WorkingDirectory '{AppContext.BaseDirectory}'\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                });
            }
        }
        catch (Exception exception)
        {
            // 重启安排失败不阻塞关闭：用户可手动启动。
            _logger?.Error(exception, "Failed to schedule application restart after migration.");
        }

        await PerformShutdownAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Info("[perf] OnExit reached.");
        _singleInstancePipeCts?.Cancel();
        _singleInstancePipeCts?.Dispose();
        _workingSetTrimmer?.Dispose();
        _taskbarIcon?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private static POINT GetCursorPosition()
    {
        GetCursorPos(out var pt);
        return pt;
    }

    private static string ExtractEmbeddedIcon()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WitchDrawer");
        Directory.CreateDirectory(tempDir);
        var tempPath = System.IO.Path.Combine(tempDir, "app.ico");

        if (File.Exists(tempPath))
        {
            return tempPath;
        }

        var uri = new Uri("pack://application:,,,/Assets/app.ico");
        var resourceInfo = Application.GetResourceStream(uri);

        if (resourceInfo is not null)
        {
            using var stream = resourceInfo.Stream;
            using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
            stream.CopyTo(fs);
        }

        return tempPath;
    }
}
