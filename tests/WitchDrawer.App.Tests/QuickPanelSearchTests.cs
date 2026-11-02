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
/// 快捷面板检索：汉字子串、路径、盒名，以及拼音首字母（wx → 微信）。
/// </summary>
public sealed class QuickPanelSearchTests
{
    [Fact]
    public async Task SearchText_FindsChineseItemByPinyinInitials()
    {
        using var workspace = await SearchWorkspace.CreateAsync();
        await workspace.AddMappingItemAsync("工具", "微信.txt");
        await workspace.AddMappingItemAsync("工具", "火绒安全软件.txt");
        var viewModel = workspace.CreatePanel();
        await viewModel.LoadAsync();

        viewModel.SearchText = "wx";

        var item = Assert.Single(viewModel.Items);
        Assert.Equal("微信.txt", item.DisplayName);
    }

    [Fact]
    public async Task SearchText_IsCaseInsensitiveForInitials()
    {
        using var workspace = await SearchWorkspace.CreateAsync();
        await workspace.AddMappingItemAsync("工具", "微信.txt");
        var viewModel = workspace.CreatePanel();
        await viewModel.LoadAsync();

        viewModel.SearchText = "WX";

        Assert.Single(viewModel.Items);
    }

    [Fact]
    public async Task SearchText_StillMatchesPlainChineseSubstring()
    {
        using var workspace = await SearchWorkspace.CreateAsync();
        await workspace.AddMappingItemAsync("工具", "微信.txt");
        await workspace.AddMappingItemAsync("工具", "火绒安全软件.txt");
        var viewModel = workspace.CreatePanel();
        await viewModel.LoadAsync();

        viewModel.SearchText = "火绒";

        var item = Assert.Single(viewModel.Items);
        Assert.Equal("火绒安全软件.txt", item.DisplayName);
    }

    [Fact]
    public async Task SearchText_MatchesBoxNameByInitials()
    {
        using var workspace = await SearchWorkspace.CreateAsync();
        await workspace.AddMappingItemAsync("游戏", "原神.txt");
        await workspace.AddMappingItemAsync("工具", "微信.txt");
        var viewModel = workspace.CreatePanel();
        await viewModel.LoadAsync();

        // 盒名「游戏」的首字母是 yx
        viewModel.SearchText = "yx";

        var item = Assert.Single(viewModel.Items);
        Assert.Equal("原神.txt", item.DisplayName);
    }

    [Fact]
    public async Task SearchText_NeverMatchesWhenInitialsAndSubstringBothMiss()
    {
        using var workspace = await SearchWorkspace.CreateAsync();
        await workspace.AddMappingItemAsync("工具", "微信.txt");
        var viewModel = workspace.CreatePanel();
        await viewModel.LoadAsync();

        viewModel.SearchText = "zzz";

        Assert.Empty(viewModel.Items);
    }

    private sealed class SearchWorkspace : IDisposable
    {
        private readonly string _root;
        private readonly DrawerService _drawerService;
        private readonly IAppLogger _logger;

        private SearchWorkspace(string root, DrawerService drawerService, IAppLogger logger)
        {
            _root = root;
            _drawerService = drawerService;
            _logger = logger;
        }

        public static async Task<SearchWorkspace> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "WitchDrawer.QuickPanelSearchTests",
                Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "data"));
            var repository = new DrawerRepository(paths.DatabasePath);
            var drawerService = new DrawerService(paths, repository);
            await drawerService.InitializeAsync();
            return new SearchWorkspace(root, drawerService, new RecordingLogger());
        }

        public async Task AddMappingItemAsync(string boxName, string fileName)
        {
            var box = (await _drawerService.GetBoxesAsync())
                .FirstOrDefault(candidate => candidate.Name == boxName);
            if (box is null)
            {
                box = await _drawerService.CreateBoxAsync(boxName, BoxType.Mapping);
            }

            var path = Path.Combine(_root, fileName);
            await File.WriteAllTextAsync(path, "payload");
            await _drawerService.ImportPathAsync(box.Id, path);
        }

        public QuickPanelViewModel CreatePanel() =>
            new(_drawerService, new NoOpFileLauncher(), _logger,
                new BoxVisualStyleStore(_drawerService, _logger));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // Temp cleanup should not hide the test result.
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