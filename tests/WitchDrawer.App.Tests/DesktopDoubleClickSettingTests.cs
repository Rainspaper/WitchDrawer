using System.IO;
using WitchDrawer.App.Infrastructure;
using WitchDrawer.App.ViewModels;
using WitchDrawer.Core;
using WitchDrawer.Core.Abstractions;
using WitchDrawer.Core.Logging;
using WitchDrawer.Core.Services;
using WitchDrawer.Core.Storage;

namespace WitchDrawer.App.Tests;

public sealed class DesktopDoubleClickSettingTests
{
    [Fact]
    public async Task LegacyEnabledSetting_IsResetToDisabledOnceOnLoad()
    {
        // 升级场景：数据库里残留旧版本（历史误触或调试）留下的 True，
        // 且迁移版本键不存在。「双击桌面切换图标」会隐藏桌面图标、
        // 误触代价高（用户会以为桌面文件消失），升级时一次性重置为关闭。
        var root = CreateTempRoot();
        try
        {
            var (viewModel, drawerService) = await CreateViewModelAsync(root);
            await drawerService.SetSettingAsync(
                MainViewModel.DesktopDoubleClickSettingKey,
                bool.TrueString);

            await viewModel.LoadAsync();

            Assert.False(viewModel.IsDesktopDoubleClickEnabled);
            Assert.Equal(
                bool.FalseString,
                await drawerService.GetSettingAsync(MainViewModel.DesktopDoubleClickSettingKey));
            Assert.Equal(
                MainViewModel.DesktopDoubleClickDefaultVersion,
                await drawerService.GetSettingAsync(
                    MainViewModel.DesktopDoubleClickDefaultVersionSettingKey));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnabledSetting_AfterMigration_IsPreservedOnLoad()
    {
        // 迁移完成后用户主动开启属于显式偏好，必须保留：不能被再次重置。
        var root = CreateTempRoot();
        try
        {
            var (viewModel, drawerService) = await CreateViewModelAsync(root);
            await drawerService.SetSettingAsync(
                MainViewModel.DesktopDoubleClickDefaultVersionSettingKey,
                MainViewModel.DesktopDoubleClickDefaultVersion);
            await drawerService.SetSettingAsync(
                MainViewModel.DesktopDoubleClickSettingKey,
                bool.TrueString);

            await viewModel.LoadAsync();

            Assert.True(viewModel.IsDesktopDoubleClickEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReEnabledSetting_IsResetOnceOnUpgradeFromV1()
    {
        // v1 迁移（或更早）之后用户又把「双击切换」开了回来：升级到 v2 时
        // 再重置一次——之后版本键到位，不再打扰用户的选择。
        var root = CreateTempRoot();
        try
        {
            var (viewModel, drawerService) = await CreateViewModelAsync(root);
            await drawerService.SetSettingAsync(
                MainViewModel.DesktopDoubleClickDefaultVersionSettingKey,
                "1");
            await drawerService.SetSettingAsync(
                MainViewModel.DesktopDoubleClickSettingKey,
                bool.TrueString);

            await viewModel.LoadAsync();

            Assert.False(viewModel.IsDesktopDoubleClickEnabled);
            Assert.Equal(
                bool.FalseString,
                await drawerService.GetSettingAsync(MainViewModel.DesktopDoubleClickSettingKey));
            Assert.Equal(
                MainViewModel.DesktopDoubleClickDefaultVersion,
                await drawerService.GetSettingAsync(
                    MainViewModel.DesktopDoubleClickDefaultVersionSettingKey));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAndToggle_PersistsDesktopDoubleClickSetting()
    {
        var root = CreateTempRoot();
        try
        {
            var (viewModel, drawerService) = await CreateViewModelAsync(root);
            await drawerService.SetSettingAsync(
                MainViewModel.DesktopDoubleClickDefaultVersionSettingKey,
                MainViewModel.DesktopDoubleClickDefaultVersion);
            await drawerService.SetSettingAsync(
                MainViewModel.DesktopDoubleClickSettingKey,
                bool.TrueString);

            await viewModel.LoadAsync();
            Assert.True(viewModel.IsDesktopDoubleClickEnabled);

            await viewModel.ToggleDesktopDoubleClickCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsDesktopDoubleClickEnabled);
            Assert.Equal(
                bool.FalseString,
                await drawerService.GetSettingAsync(MainViewModel.DesktopDoubleClickSettingKey));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "WitchDrawerTests", Guid.NewGuid().ToString("N"));

    private static async Task<(MainViewModel ViewModel, DrawerService DrawerService)>
        CreateViewModelAsync(string root)
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
        return (viewModel, drawerService);
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
