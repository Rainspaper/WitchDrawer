using System.IO;
using WitchDrawer.App.Infrastructure;
using WitchDrawer.App.ViewModels;
using WitchDrawer.Core;
using WitchDrawer.Core.Abstractions;
using WitchDrawer.Core.Logging;
using WitchDrawer.Core.Models;
using WitchDrawer.Core.Services;
using WitchDrawer.Core.Storage;

namespace WitchDrawer.App.Tests;

/// <summary>
/// Drawer-style storage for normal/mapping boxes: when the global
/// "storage style" toggle is enabled, extra icons fold into the last tile
/// exactly like a drawer box.
/// </summary>
public sealed class DesktopBoxDrawerStyleTests
{
    [Fact]
    public async Task NormalBox_CollapsesToDrawerCoverWhenStyleEnabled()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("normal", BoxType.Normal);

            // Import more files than the default cover capacity (3x2 = 6) to force overflow.
            var sourcePaths = Enumerable.Range(0, 9)
                .Select(index => CreateSourceFile(root, $"item-{index:D2}.txt"))
                .ToArray();
            foreach (var path in sourcePaths)
            {
                await drawerService.ImportPathAsync(box.Id, path);
            }

            var viewModel = CreateViewModel(box, drawerService, repository);
            await viewModel.LoadAsync();

            // Setting is off by default: the normal box keeps the plain grid.
            Assert.False(viewModel.IsDrawerStyleEnabled);
            Assert.False(viewModel.IsDrawerCollapsed);

            // Enable the global drawer-style setting and reload it on the view model.
            await drawerService.SetSettingAsync(
                DesktopBoxViewModel.DrawerStyleSettingKey,
                bool.TrueString);
            await viewModel.LoadDrawerStyleAsync();

            Assert.True(viewModel.DrawerStyleEnabled);
            Assert.True(viewModel.IsDrawerStyleEnabled);
            Assert.True(viewModel.IsDrawerCollapsed);
            Assert.True(viewModel.DrawerHasOverflow);
            // Cover shows capacity - 1 items plus the expand (fold) tile.
            Assert.Equal(viewModel.DrawerDirectItemCount + 1, viewModel.DrawerCoverTiles.Count);
            Assert.Equal(1, viewModel.DrawerCoverTiles.Count(tile => tile.IsExpandTile));
            // The fold tile label shows how many icons were folded away.
            Assert.Equal(
                $"+{viewModel.Items.Count - viewModel.DrawerDirectItemCount}",
                viewModel.DrawerExpandTileLabel);

            // Disable again: the box returns to the plain grid.
            await drawerService.SetSettingAsync(
                DesktopBoxViewModel.DrawerStyleSettingKey,
                bool.FalseString);
            await viewModel.LoadDrawerStyleAsync();

            Assert.False(viewModel.IsDrawerStyleEnabled);
            Assert.False(viewModel.IsDrawerCollapsed);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task EmptyNormalBox_WithDrawerStyleStillCollapsesToCover()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("empty", BoxType.Normal);
            var viewModel = CreateViewModel(box, drawerService, repository);
            await viewModel.LoadAsync();

            await drawerService.SetSettingAsync(
                DesktopBoxViewModel.DrawerStyleSettingKey,
                bool.TrueString);
            await viewModel.LoadDrawerStyleAsync();

            Assert.True(viewModel.IsDrawerStyleEnabled);
            Assert.True(viewModel.IsDrawerCollapsed);
            Assert.False(viewModel.DrawerHasOverflow);
            Assert.Empty(viewModel.DrawerCoverTiles);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task MappingBox_ListModeStaysInListWhenDrawerStyleEnabled()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("mapping", BoxType.Mapping);
            var viewModel = CreateViewModel(box, drawerService, repository);

            await drawerService.SetSettingAsync(
                DesktopBoxViewModel.DrawerStyleSettingKey,
                bool.TrueString);
            await viewModel.LoadDrawerStyleAsync();

            Assert.True(viewModel.IsDrawerStyleEnabled);
            Assert.True(viewModel.IsDrawerCollapsed);

            await viewModel.UseMappingListModeCommand.ExecuteAsync(null);

            Assert.True(viewModel.IsMappingListMode);
            Assert.False(viewModel.IsDrawerCollapsed);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task DrawerBox_IsAlwaysDrawerStyleRegardlessOfSetting()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("drawer", BoxType.Drawer);
            var viewModel = CreateViewModel(box, drawerService, repository);

            // The global toggle only affects normal/mapping boxes; a drawer box is
            // drawer-style even when the setting is off.
            Assert.True(viewModel.IsDrawerStyleEnabled);
            Assert.True(viewModel.IsDrawerCollapsed);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task MainViewModel_TogglePersistsDrawerStyleSetting()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new AppPaths(root);
            var repository = new DrawerRepository(paths.DatabasePath);
            var drawerService = new DrawerService(paths, repository);
            await drawerService.InitializeAsync();
            var logger = new RecordingLogger();
            var launcher = new NoOpFileLauncher();
            var visualStyleStore = new BoxVisualStyleStore(drawerService, logger);
            var viewModel = new MainViewModel(
                drawerService,
                new TodoService(repository),
                launcher,
                logger,
                new QuickPanelViewModel(drawerService, launcher, logger, visualStyleStore),
                new UpdateService(logger),
                visualStyleStore,
                new BoxPositionLockStateStore(drawerService, logger),
                paths,
                new DataStorageMigrationService(
                    paths,
                    repository,
                    new StorageLocationStore(Path.Combine(root, "storage-location.json"))));

            // 抽屉式收纳默认开启；先显式关闭以验证开关的持久化。
            await drawerService.SetSettingAsync(
                DesktopBoxViewModel.DrawerStyleSettingKey,
                bool.FalseString);
            await viewModel.LoadAsync();
            Assert.False(viewModel.DrawerStyleEnabled);

            await viewModel.ToggleDrawerStyleCommand.ExecuteAsync(null);

            Assert.True(viewModel.DrawerStyleEnabled);
            Assert.Equal(
                bool.TrueString,
                await drawerService.GetSettingAsync(DesktopBoxViewModel.DrawerStyleSettingKey));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task NormalBox_DrawerStyleEnabledByDefaultWhenSettingAbsent()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("normal", BoxType.Normal);
            var viewModel = CreateViewModel(box, drawerService, repository);

            Assert.Null(await drawerService.GetSettingAsync(DesktopBoxViewModel.DrawerStyleSettingKey));

            await viewModel.LoadDrawerStyleAsync();

            Assert.True(viewModel.DrawerStyleEnabled);
            Assert.True(viewModel.IsDrawerStyleEnabled);
            Assert.True(viewModel.IsDrawerCollapsed);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task CollapsedCover_MinimumWidthIsTwoColumns()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("mapping", BoxType.Mapping);

            // 保存一个远小于两列的封面尺寸，加载后应被钳制到至少 2 列宽。
            await drawerService.SetSettingAsync(
                DesktopBoxViewModel.GetDrawerCoverSizeSettingKey(box.Id),
                "57,131");

            var viewModel = CreateViewModel(box, drawerService, repository);
            await viewModel.LoadDrawerStyleAsync();
            await viewModel.LoadDrawerCoverSizeAsync();

            Assert.True(viewModel.DrawerCoverColumns >= 2);
            Assert.True(viewModel.DrawerCoverWidth >= 96);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task CollapsedCover_CapsHeaderWidthToTheCover()
    {
        // m00035：抽屉封面折叠时标题栏的自然宽度不能成为窗口宽度下限，
        // 否则封面缩到 2 列图标后盒子仍被标题栏撑宽，图标左右出现空白、右下角尺寸手柄也会离开盒子角。
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("mapping", BoxType.Mapping);
            var viewModel = CreateViewModel(box, drawerService, repository);
            await viewModel.LoadDrawerStyleAsync();
            await viewModel.LoadDrawerCoverSizeAsync();

            Assert.True(viewModel.IsDrawerCollapsed);
            Assert.Equal(viewModel.DrawerCoverWidth, viewModel.HeaderMaxWidth);

            // 缩到 2 列：封面与标题栏上限一起收窄，盒子才能跟着变窄。
            viewModel.ResizeDrawerCover(60, viewModel.DrawerCoverHeight);
            Assert.Equal(2, viewModel.DrawerCoverColumns);
            Assert.Equal(viewModel.DrawerCoverWidth, viewModel.HeaderMaxWidth);

            // 展开抽屉后不再限制标题栏宽度，恢复原布局。
            viewModel.IsDrawerExpanded = true;
            Assert.True(double.IsPositiveInfinity(viewModel.HeaderMaxWidth));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task CollapsedCover_NotifiesHeaderMaxWidthChanges()
    {
        // m00737：HeaderMaxWidth 是计算属性，不主动通知时标题栏 MaxWidth 绑定只会在首次求值生效。
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("mapping", BoxType.Mapping);
            var viewModel = CreateViewModel(box, drawerService, repository);
            await viewModel.LoadDrawerStyleAsync();
            await viewModel.LoadDrawerCoverSizeAsync();
            var raised = new List<string>();
            viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
            viewModel.ResizeDrawerCover(60, viewModel.DrawerCoverHeight);
            Assert.Contains(nameof(DesktopBoxViewModel.HeaderMaxWidth), raised);
            raised.Clear();
            viewModel.IsDrawerExpanded = true;
            Assert.Contains(nameof(DesktopBoxViewModel.HeaderMaxWidth), raised);
            raised.Clear();
            viewModel.IsDrawerExpanded = false;
            Assert.Contains(nameof(DesktopBoxViewModel.HeaderMaxWidth), raised);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    private static DesktopBoxViewModel CreateViewModel(
        Box box,
        DrawerService drawerService,
        DrawerRepository repository) =>
        new(
            box,
            drawerService,
            new TodoService(repository),
            new NoOpFileLauncher(),
            new RecordingLogger(),
            BoxVisualStyle.Modern);

    private static string CreateSourceFile(string root, string name)
    {
        var directory = Path.Combine(root, "sources");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "payload");
        return path;
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "WitchDrawerTests", Guid.NewGuid().ToString("N"));

    private static async Task<(DrawerService Service, DrawerRepository Repository)> CreateDrawerServiceAsync(
        string root)
    {
        var paths = new AppPaths(root);
        var repository = new DrawerRepository(paths.DatabasePath);
        var drawerService = new DrawerService(paths, repository);
        await drawerService.InitializeAsync();
        return (drawerService, repository);
    }

    private static void CleanupTempRoot(string root)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class NoOpFileLauncher : IFileLauncher
    {
        public Task OpenAsync(string path, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public void Info(string message)
        {
        }

        public void Error(Exception exception, string message)
        {
        }
    }
}
