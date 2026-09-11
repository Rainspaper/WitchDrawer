using System.IO;
using WitchDrawer.App.Infrastructure;
using WitchDrawer.Core;
using WitchDrawer.Core.Logging;
using WitchDrawer.Core.Models;
using WitchDrawer.Core.Services;
using WitchDrawer.Core.Storage;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 映射文件夹监视器的行为回归：
/// <list type="bullet">
/// <item>源文件夹里删除文件后，watcher 触发同步，Core 移除失效引用并回调 UI 刷新。</item>
/// <item>watcher 缓冲溢出（Error）后必须自动重建监视，而不是永久停掉——
/// 否则大文件夹批量删除会让该盒的自动同步永久失效。</item>
/// </list>
/// </summary>
public sealed class MappingFolderWatcherTests
{
    [Fact]
    public async Task DeleteSync_RemovesSourceReferenceAndInvokesCallback()
    {
        using var workspace = await TestWorkspace.CreateAsync();
        var mappingBox = workspace.GetMappingBox();

        var folder = Path.Combine(workspace.Root, "watch-folder");
        Directory.CreateDirectory(folder);
        var appPath = Path.Combine(folder, "SomeApp.exe");
        File.WriteAllText(appPath, "app");
        await workspace.Service.AddMappingFolderAsync(mappingBox.Id, folder);
        await workspace.Service.SyncMappingFoldersAsync(mappingBox.Id);
        Assert.Single(await workspace.Service.GetItemsAsync(mappingBox.Id));

        var syncSignaled = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new MappingFolderWatcher(
            workspace.Service,
            id =>
            {
                syncSignaled.TrySetResult(id);
                return Task.CompletedTask;
            },
            workspace.Logger,
            debounceMilliseconds: 50);
        await watcher.SynchronizeAsync(mappingBox.Id);

        // 源文件被删：watcher 触发同步 → Core 移除失效引用 → 回调通知 UI 刷新。
        File.Delete(appPath);
        var notifiedBoxId = await syncSignaled.Task.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(mappingBox.Id, notifiedBoxId);
        Assert.Empty(await workspace.Service.GetItemsAsync(mappingBox.Id));
    }

    [Fact]
    public async Task Error_RebuildsWatcher_SoLaterDeletesStillSync()
    {
        using var workspace = await TestWorkspace.CreateAsync();
        var mappingBox = workspace.GetMappingBox();

        var folder = Path.Combine(workspace.Root, "watch-folder");
        Directory.CreateDirectory(folder);
        var aPath = Path.Combine(folder, "a.exe");
        File.WriteAllText(aPath, "a");
        var bPath = Path.Combine(folder, "b.exe");
        File.WriteAllText(bPath, "b");
        await workspace.Service.AddMappingFolderAsync(mappingBox.Id, folder);
        await workspace.Service.SyncMappingFoldersAsync(mappingBox.Id);
        Assert.Equal(2, (await workspace.Service.GetItemsAsync(mappingBox.Id)).Count);

        var syncSignaled = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new MappingFolderWatcher(
            workspace.Service,
            id =>
            {
                syncSignaled.TrySetResult(id);
                return Task.CompletedTask;
            },
            workspace.Logger,
            debounceMilliseconds: 50);
        await watcher.SynchronizeAsync(mappingBox.Id);

        // 模拟 watcher 缓冲溢出错误：内部应自动重建监视，而不是永久停掉。
        watcher.HandleWatcherError(mappingBox.Id);

        // 等重建完成（重建延迟 + 同步防抖）。
        await Task.Delay(TimeSpan.FromMilliseconds(900));

        // 重建后删除源文件仍应触发同步（Error 不再杀死自动同步）。
        File.Delete(bPath);
        var notifiedBoxId = await syncSignaled.Task.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(mappingBox.Id, notifiedBoxId);
        var remaining = await workspace.Service.GetItemsAsync(mappingBox.Id);
        Assert.Single(remaining);
        Assert.DoesNotContain(remaining, item => item.DisplayName == "b.exe");
    }

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(string root, DrawerService service, RecordingLogger logger)
        {
            Root = root;
            Service = service;
            Logger = logger;
        }

        public string Root { get; }

        public DrawerService Service { get; }

        public RecordingLogger Logger { get; }

        public Box GetMappingBox()
        {
            var boxes = Service.GetBoxesAsync().GetAwaiter().GetResult();
            return boxes.Single(box => box.Type == BoxType.Mapping);
        }

        public static async Task<TestWorkspace> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "WitchDrawer.MappingFolderWatcher",
                Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(root);
            var repository = new DrawerRepository(paths.DatabasePath);
            var service = new DrawerService(paths, repository);
            await service.InitializeAsync();
            return new TestWorkspace(root, service, new RecordingLogger());
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Temp cleanup should not hide the test result.
            }
        }
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<(Exception Exception, string Message)> Errors { get; } = [];

        public void Info(string message)
        {
        }

        public void Error(Exception exception, string message)
        {
            Errors.Add((exception, message));
        }
    }
}