using System.IO;
using WitchDrawer.App.ViewModels;
using WitchDrawer.Core;
using WitchDrawer.Core.Abstractions;
using WitchDrawer.Core.Logging;
using WitchDrawer.Core.Models;
using WitchDrawer.Core.Services;
using WitchDrawer.Core.Storage;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 统一排序（自由/名称/大小/类型/修改日期）的显示与自由布局记忆测试。
/// </summary>
public sealed class UnifiedBoxSortTests
{
    [Fact]
    public async Task NameSort_ArrangesItemsRowFirstInNameOrder()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("普通盒", BoxType.Normal);
            // 故意以非名称序、散乱格位导入。
            var beta = CreateSourceFile(root, "beta.txt");
            var alpha = CreateSourceFile(root, "alpha.txt");
            var gamma = CreateSourceFile(root, "gamma.txt");
            await drawerService.ImportPathAsync(box.Id, beta, 3, 1);
            await drawerService.ImportPathAsync(box.Id, alpha, 0, 2);
            await drawerService.ImportPathAsync(box.Id, gamma, 1, 0);

            var viewModel = CreateViewModel(box, drawerService, repository);
            viewModel.ApplyDrawerSortMode(DrawerItemSortMode.Name);
            await viewModel.LoadAsync();

            // 名称序 + 行优先（默认 wrap ≥ 4 列，单行排开）。
            Assert.Equal(
                new[] { "alpha.txt", "beta.txt", "gamma.txt" },
                viewModel.Items.Select(item => item.DisplayName).ToArray());
            Assert.Equal(
                new[] { (0, 0), (1, 0), (2, 0) },
                viewModel.Items.Select(item => (item.GridColumn, item.GridRow)).ToArray());
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task SwitchingBackToFree_RestoresRememberedFreeLayout()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("普通盒", BoxType.Normal);
            var beta = CreateSourceFile(root, "beta.txt");
            var alpha = CreateSourceFile(root, "alpha.txt");
            var betaItem = await drawerService.ImportPathAsync(box.Id, beta, 3, 1);
            var alphaItem = await drawerService.ImportPathAsync(box.Id, alpha, 0, 2);

            var viewModel = CreateViewModel(box, drawerService, repository);
            await viewModel.LoadAsync();
            Assert.Equal((3, 1), (viewModel.Items.Single(i => i.Id == betaItem.Id).GridColumn,
                viewModel.Items.Single(i => i.Id == betaItem.Id).GridRow));

            // 切到名称排序：行优先重排。
            viewModel.ApplyDrawerSortMode(DrawerItemSortMode.Name);
            await viewModel.LoadAsync();
            Assert.Equal(
                new[] { "alpha.txt", "beta.txt" },
                viewModel.Items.Select(item => item.DisplayName).ToArray());

            // 切回自由：精确恢复切走前的自由布局。
            viewModel.ApplyDrawerSortMode(DrawerItemSortMode.Free);
            await viewModel.LoadAsync();

            Assert.Equal(
                (3, 1),
                (viewModel.Items.Single(i => i.Id == betaItem.Id).GridColumn,
                    viewModel.Items.Single(i => i.Id == betaItem.Id).GridRow));
            Assert.Equal(
                (0, 2),
                (viewModel.Items.Single(i => i.Id == alphaItem.Id).GridColumn,
                    viewModel.Items.Single(i => i.Id == alphaItem.Id).GridRow));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task SortedMode_ImportWritesNoGridPosition_AndFreeLayoutStaysIntact()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("普通盒", BoxType.Normal);
            var beta = CreateSourceFile(root, "beta.txt");
            var alphaItem = await drawerService.ImportPathAsync(
                box.Id, CreateSourceFile(root, "alpha.txt"), 2, 1);
            await drawerService.ImportPathAsync(box.Id, beta, 0, 0);

            var viewModel = CreateViewModel(box, drawerService, repository);
            viewModel.ApplyDrawerSortMode(DrawerItemSortMode.Name);
            await viewModel.LoadAsync();

            // 排序模式下经桌面盒导入：不写格位。
            var delta = CreateSourceFile(root, "delta.txt");
            await viewModel.ImportPathsAsync([delta]);

            var persisted = await drawerService.GetItemsAsync(box.Id);
            var deltaItem = persisted.Single(item => item.DisplayName == "delta.txt");
            Assert.True(deltaItem.GridColumn is null or < 0 || deltaItem.GridRow is null or < 0);

            // 切回自由：原有项目的自由布局不受影响，新项目分配空位。
            viewModel.ApplyDrawerSortMode(DrawerItemSortMode.Free);
            await viewModel.LoadAsync();

            Assert.Equal(
                (2, 1),
                (viewModel.Items.Single(i => i.Id == alphaItem.Id).GridColumn,
                    viewModel.Items.Single(i => i.Id == alphaItem.Id).GridRow));
            var deltaSlots = viewModel.Items
                .Where(item => item.Id != deltaItem.Id)
                .Select(item => (item.GridColumn, item.GridRow))
                .ToHashSet();
            var deltaSlot = viewModel.Items.Single(i => i.Id == deltaItem.Id);
            Assert.DoesNotContain((deltaSlot.GridColumn, deltaSlot.GridRow), deltaSlots);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task SortedMode_SameBoxDropDoesNotRearrange()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("普通盒", BoxType.Normal);
            var alphaItem = await drawerService.ImportPathAsync(
                box.Id, CreateSourceFile(root, "alpha.txt"), 0, 0);
            await drawerService.ImportPathAsync(box.Id, CreateSourceFile(root, "beta.txt"), 1, 0);

            var viewModel = CreateViewModel(box, drawerService, repository);
            viewModel.ApplyDrawerSortMode(DrawerItemSortMode.Name);
            await viewModel.LoadAsync();

            var before = (viewModel.Items.Single(i => i.Id == alphaItem.Id).GridColumn,
                viewModel.Items.Single(i => i.Id == alphaItem.Id).GridRow);

            var result = await viewModel.DropDrawerItemAsync(alphaItem.Id, 5, 5);

            Assert.True(result);
            var after = (viewModel.Items.Single(i => i.Id == alphaItem.Id).GridColumn,
                viewModel.Items.Single(i => i.Id == alphaItem.Id).GridRow);
            Assert.Equal(before, after);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task MappingListDrop_ReordersAtHoveredIndexAndPersistsAllGridPositions()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("映射盒", BoxType.Mapping);
            var alpha = await drawerService.ImportPathAsync(
                box.Id, CreateSourceFile(root, "alpha.txt"), 0, 0);
            await drawerService.ImportPathAsync(
                box.Id, CreateSourceFile(root, "beta.txt"), 1, 0);
            await drawerService.ImportPathAsync(
                box.Id, CreateSourceFile(root, "gamma.txt"), 0, 1);

            var viewModel = CreateViewModel(box, drawerService, repository);
            await viewModel.LoadAsync();
            await viewModel.UseMappingListModeCommand.ExecuteAsync(null);

            var originalSlots = viewModel.Items
                .Select(item => (item.GridColumn, item.GridRow))
                .ToHashSet();
            var moved = await viewModel.DropDrawerItemAsync(alpha.Id, 0, 2);

            Assert.True(moved);
            Assert.Equal(
                new[] { "beta.txt", "gamma.txt", "alpha.txt" },
                viewModel.Items.Select(item => item.DisplayName).ToArray());
            Assert.True(originalSlots.SetEquals(
                viewModel.Items.Select(item => (item.GridColumn, item.GridRow))));

            var reloaded = CreateViewModel(box, drawerService, repository);
            await reloaded.LoadAsync();
            Assert.Equal(
                new[] { "beta.txt", "gamma.txt", "alpha.txt" },
                reloaded.Items.Select(item => item.DisplayName).ToArray());
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task MappingListWidth_IsClampedAndPersistedPerBox()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var firstBox = await drawerService.CreateBoxAsync("映射盒一", BoxType.Mapping);
            var secondBox = await drawerService.CreateBoxAsync("映射盒二", BoxType.Mapping);
            var first = CreateViewModel(firstBox, drawerService, repository);

            first.ResizeMappingListWidth(412.5);
            await first.SaveMappingListWidthAsync();

            var restored = CreateViewModel(firstBox, drawerService, repository);
            await restored.LoadMappingListWidthAsync();
            var untouched = CreateViewModel(secondBox, drawerService, repository);
            await untouched.LoadMappingListWidthAsync();

            Assert.Equal(412.5, restored.MappingListWidth);
            Assert.Equal(untouched.LayoutSettings.MappingListWidth, untouched.MappingListWidth);
            restored.ResizeMappingListWidth(double.PositiveInfinity);
            Assert.Equal(
                restored.LayoutSettings.MappingListWidth,
                restored.MappingListWidth);
            restored.ResizeMappingListWidth(5000);
            Assert.Equal(DesktopBoxViewModel.MaximumMappingListWidth, restored.MappingListWidth);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task FreeGridDrop_InsertsIntoOccupiedCellAndKeepsSlotShape()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("普通盒", BoxType.Normal);
            await drawerService.ImportPathAsync(box.Id, CreateSourceFile(root, "a.txt"), 0, 0);
            await drawerService.ImportPathAsync(box.Id, CreateSourceFile(root, "b.txt"), 1, 0);
            await drawerService.ImportPathAsync(box.Id, CreateSourceFile(root, "c.txt"), 0, 1);
            await drawerService.ImportPathAsync(box.Id, CreateSourceFile(root, "d.txt"), 1, 1);

            var viewModel = CreateViewModel(box, drawerService, repository);
            await viewModel.LoadAsync();
            Assert.True(viewModel.IsFreeSort);

            var originalSlots = viewModel.Items
                .Select(item => (item.GridColumn, item.GridRow))
                .ToHashSet();

            // 把第二行第一个（c）拖到第一行第二个格位（被 b 占着）：
            // 以前会退回自己的格子（看起来没反应），现在应插到 b 前面。
            var moved = await viewModel.DropDrawerItemAsync(
                viewModel.Items.Single(item => item.DisplayName == "c.txt").Id, 1, 0);

            Assert.True(moved);
            Assert.Equal(
                new[] { "a.txt", "c.txt", "b.txt", "d.txt" },
                viewModel.Items.Select(item => item.DisplayName).ToArray());
            Assert.True(originalSlots.SetEquals(
                viewModel.Items.Select(item => (item.GridColumn, item.GridRow))));

            var reloaded = CreateViewModel(box, drawerService, repository);
            await reloaded.LoadAsync();
            Assert.Equal(
                new[] { "a.txt", "c.txt", "b.txt", "d.txt" },
                reloaded.Items.Select(item => item.DisplayName).ToArray());
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void CalculateGridInsertionIndex_CountsOtherSlotsBeforeTheTargetCell()
    {
        var slots = new List<(int GridColumn, int GridRow)>
        {
            (0, 0), (1, 0), (0, 1), (1, 1)
        };

        Assert.Equal(0, DesktopBoxViewModel.CalculateGridInsertionIndex(slots, 0, 0));
        Assert.Equal(1, DesktopBoxViewModel.CalculateGridInsertionIndex(slots, 1, 0));
        Assert.Equal(2, DesktopBoxViewModel.CalculateGridInsertionIndex(slots, 0, 1));
        Assert.Equal(3, DesktopBoxViewModel.CalculateGridInsertionIndex(slots, 1, 1));
        Assert.Equal(4, DesktopBoxViewModel.CalculateGridInsertionIndex(slots, 3, 3));
    }

    [Fact]
    public async Task CollapsedDrawerCoverDrop_InsertsAtTheCoverCell()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("普通盒", BoxType.Normal);
            await drawerService.ImportPathAsync(box.Id, CreateSourceFile(root, "a.txt"), 0, 0);
            await drawerService.ImportPathAsync(box.Id, CreateSourceFile(root, "b.txt"), 1, 0);
            await drawerService.ImportPathAsync(box.Id, CreateSourceFile(root, "c.txt"), 0, 1);

            var viewModel = CreateViewModel(box, drawerService, repository);
            await viewModel.LoadDrawerStyleAsync();
            await viewModel.LoadAsync();
            Assert.True(viewModel.IsDrawerCollapsed);
            Assert.True(viewModel.DrawerCoverColumns >= 2);

            // 折叠封面：拖到行优先序号 1 的封面格子，应插到第二个位置，而不是追加到末尾。
            var moved = await viewModel.DropDrawerItemAsync(
                viewModel.Items.Single(item => item.DisplayName == "c.txt").Id, 0, 1);

            Assert.True(moved);
            Assert.Equal(
                new[] { "a.txt", "c.txt", "b.txt" },
                viewModel.Items.Select(item => item.DisplayName).ToArray());
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task FreeGridDrop_OntoAnOccupiedCell_LandsOnThatExactCell()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, repository) = await CreateDrawerServiceAsync(root);
            var box = await drawerService.CreateBoxAsync("普通盒", BoxType.Normal);
            var alpha = await drawerService.ImportPathAsync(
                box.Id, CreateSourceFile(root, "alpha.txt"), 0, 0);
            var beta = await drawerService.ImportPathAsync(
                box.Id, CreateSourceFile(root, "beta.txt"), 1, 0);

            var viewModel = CreateViewModel(box, drawerService, repository);
            await viewModel.LoadAsync();

            // 把左边的 alpha 拖到 beta 所在的格子：应正好占住那一格（beta 让位），
            // 而不是像以前那样退回自己的格子。
            var moved = await viewModel.DropDrawerItemAsync(alpha.Id, 1, 0);

            Assert.True(moved);
            var movedAlpha = viewModel.Items.Single(item => item.Id == alpha.Id);
            var movedBeta = viewModel.Items.Single(item => item.Id == beta.Id);
            Assert.Equal((1, 0), (movedAlpha.GridColumn, movedAlpha.GridRow));
            Assert.Equal((0, 0), (movedBeta.GridColumn, movedBeta.GridRow));
            Assert.Equal(
                new[] { "beta.txt", "alpha.txt" },
                viewModel.Items.Select(item => item.DisplayName).ToArray());
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
        public Task OpenAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
