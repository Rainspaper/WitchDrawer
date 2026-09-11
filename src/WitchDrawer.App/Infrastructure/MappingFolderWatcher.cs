using System.IO;
using System.Windows;
using WitchDrawer.Core.Logging;
using WitchDrawer.Core.Services;

namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 监视映射收纳盒已注册的映射源文件夹：内容新增/改名/删除时，
/// 先在后台同步（导入新项、移除失效引用），再在 UI 线程触发回调让对应盒子刷新。
/// 事件做了防抖合并，避免连续 IO 风暴。
/// 健壮性：缓冲加大到 64KB + 溢出（Error）后自动重建监视——批量删除一个软件目录
/// 会产生大量 Deleted 事件冲爆默认 8KB 缓冲，若不重建，该盒的自动同步会永久失效。
/// </summary>
public sealed class MappingFolderWatcher : IDisposable
{
    internal const int InternalBufferSizeBytes = 64 * 1024;

    private readonly DrawerService _drawerService;
    private readonly Func<Guid, Task> _onFolderChanged;
    private readonly IAppLogger _logger;
    private readonly int _debounceMilliseconds;
    private readonly int _rebuildDelayMilliseconds;
    private readonly Dictionary<Guid, List<FileSystemWatcher>> _watchers = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _debounce = [];
    private readonly object _gate = new();
    private bool _disposed;

    public MappingFolderWatcher(
        DrawerService drawerService,
        Func<Guid, Task> onFolderChanged,
        IAppLogger logger,
        int debounceMilliseconds = 400,
        int rebuildDelayMilliseconds = 500)
    {
        _drawerService = drawerService;
        _onFolderChanged = onFolderChanged;
        _logger = logger;
        _debounceMilliseconds = debounceMilliseconds;
        _rebuildDelayMilliseconds = rebuildDelayMilliseconds;
    }

    public async Task SynchronizeAsync(Guid boxId, CancellationToken cancellationToken = default)
    {
        var folders = await _drawerService.GetMappingFolderPathsAsync(boxId, cancellationToken);
        Synchronize(boxId, folders);
    }

    public void Synchronize(Guid boxId, IReadOnlyList<string> folderPaths)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_watchers.TryGetValue(boxId, out var existing))
            {
                foreach (var watcher in existing)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }

                _watchers.Remove(boxId);
            }

            var folders = folderPaths
                .Where(path => Directory.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (folders.Count == 0)
            {
                return;
            }

            var list = new List<FileSystemWatcher>(folders.Count);
            foreach (var folder in folders)
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    // 默认 8KB 缓冲在批量删除（一个软件目录几十上百个文件）时极易溢出，
                    // 溢出会触发 Error 并丢失事件——加大缓冲降低概率。
                    InternalBufferSize = InternalBufferSizeBytes,
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                watcher.Created += (_, _) => OnFolderChanged(boxId);
                watcher.Changed += (_, _) => OnFolderChanged(boxId);
                watcher.Renamed += (_, _) => OnFolderChanged(boxId);
                watcher.Deleted += (_, _) => OnFolderChanged(boxId);
                watcher.Error += (_, e) => OnWatcherError(boxId, e);
                list.Add(watcher);
            }

            _watchers[boxId] = list;
        }
    }

    public void StopBox(Guid boxId)
    {
        lock (_gate)
        {
            if (_watchers.TryGetValue(boxId, out var existing))
            {
                foreach (var watcher in existing)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }

                _watchers.Remove(boxId);
            }

            if (_debounce.TryGetValue(boxId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _debounce.Remove(boxId);
            }
        }
    }

    private void OnFolderChanged(Guid boxId)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_debounce.TryGetValue(boxId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            cts = new CancellationTokenSource();
            _debounce[boxId] = cts;
        }

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_debounceMilliseconds), token);
                var result = await _drawerService.SyncMappingFoldersAsync(boxId, token);
                if (result.Changed)
                {
                    if (Application.Current is { } app)
                    {
                        await app.Dispatcher.InvokeAsync(() => _onFolderChanged(boxId));
                    }
                    else
                    {
                        // 无 WPF 宿主（后台任务/测试环境）：直接回调，同步结果不能丢。
                        await _onFolderChanged(boxId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 防抖被新事件替代：旧任务的取消是预期行为。
            }
            catch (Exception exception)
            {
                // 文件监视的偶发异常不应影响主程序，但要留下痕迹。
                _logger.Error(
                    exception,
                    $"Mapping folder sync failed for box {boxId:N}.");
            }
        }, token);
    }

    internal void HandleWatcherError(Guid boxId)
    {
        _logger.Error(
            new IOException($"FileSystemWatcher error for box {boxId:N}."),
            $"Mapping folder watcher error for box {boxId:N}; rebuilding watcher.");

        // 缓冲溢出/文件夹被删：清掉旧监视，短暂延后重建。
        // 不再永久停掉——否则大文件夹批量操作之后该盒自动同步永久失效。
        StopBox(boxId);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_rebuildDelayMilliseconds));
                await SynchronizeAsync(boxId);
            }
            catch (Exception exception)
            {
                _logger.Error(
                    exception,
                    $"Failed to rebuild mapping folder watcher for box {boxId:N}.");
            }
        });
    }

    private void OnWatcherError(Guid boxId, ErrorEventArgs e)
    {
        HandleWatcherError(boxId);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var list in _watchers.Values)
            {
                foreach (var watcher in list)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
            }

            _watchers.Clear();
            foreach (var cts in _debounce.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _debounce.Clear();
        }
    }
}