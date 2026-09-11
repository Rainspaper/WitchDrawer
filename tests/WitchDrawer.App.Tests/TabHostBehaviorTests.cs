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
/// 多标签总盒子的行为回归：
/// <list type="bullet">
/// <item>标签栏每格显示「盒子自己的名字」（<see cref="DesktopBoxViewModel.TabLabel"/>），
/// 而不是会跟随选中项的窗口名（<see cref="DesktopBoxViewModel.Name"/>）。</item>
/// <item>网格/列表模式按盒子各自记录，切换标签时显示的是该标签自己那份模式。</item>
/// <item>切换标签必须整体刷新派生状态，否则内容区会停留在上一个标签上。</item>
/// </list>
/// </summary>
public sealed class TabHostBehaviorTests
{
    [Fact]
    public async Task TabLabel_KeepsOwnName_WhileNameFollowsTheActiveTab()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, todoService) = await CreateServicesAsync(root);
            var host = CreateViewModel("工具", BoxType.Normal, drawerService, todoService);
            var member = CreateViewModel("便携优化", BoxType.Normal, drawerService, todoService);

            host.IsTabHost = true;
            host.InitializeTabs([member]);

            Assert.Equal("工具", host.TabLabel);
            Assert.Equal("便携优化", member.TabLabel);

            // 切到成员标签：标签栏文字一律不动，只有标题栏跟随选中项。
            host.ActiveTabVm = member;

            Assert.Equal("工具", host.TabLabel);
            Assert.Equal("便携优化", member.TabLabel);
            Assert.Equal("便携优化", host.Name);
            Assert.True(member.IsActiveTab);
            Assert.False(host.IsActiveTab);

            // 切回宿主标签，同样成立。
            host.ActiveTabVm = host;

            Assert.Equal("工具", host.TabLabel);
            Assert.Equal("工具", host.Name);
            Assert.True(host.IsActiveTab);
            Assert.False(member.IsActiveTab);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task GridListMode_IsRememberedPerTab_AndSwitchingRefreshesDerivedState()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, todoService) = await CreateServicesAsync(root);
            var host = CreateViewModel("工具", BoxType.Mapping, drawerService, todoService);
            var member = CreateViewModel("AI", BoxType.Mapping, drawerService, todoService);

            // 宿主自己切成列表模式，成员保持默认的网格模式。
            await host.UseMappingListModeCommand.ExecuteAsync(null);
            Assert.True(host.IsMappingListMode);
            Assert.Equal(
                "List",
                await drawerService.GetSettingAsync(
                    DesktopBoxViewModel.GetMappingViewModeSettingKey(host.BoxId)));

            host.IsTabHost = true;
            host.InitializeTabs([member]);
            Assert.True(host.IsMappingListMode);

            var raised = new List<string>();
            host.PropertyChanged += (_, args) => raised.Add(args.PropertyName ?? string.Empty);
            host.ActiveTabVm = member;

            // 关键：跟随成员自己的网格模式，而不是沿用宿主的列表模式。
            Assert.False(host.IsMappingListMode);
            Assert.True(host.IsGridMode);
            // 派生状态必须在切换时通知，否则绑定会停留在上一个标签的值上。
            Assert.Contains(nameof(DesktopBoxViewModel.IsMappingListMode), raised);
            Assert.Contains(nameof(DesktopBoxViewModel.IsGridMode), raised);
            Assert.Contains(nameof(DesktopBoxViewModel.Name), raised);
            Assert.Contains(nameof(DesktopBoxViewModel.HeaderRowHeight), raised);

            // 在成员标签上切模式：记到成员自己的设置键上，宿主那份不受影响。
            await host.UseMappingListModeCommand.ExecuteAsync(null);
            Assert.True(host.IsMappingListMode);
            Assert.Equal(
                "List",
                await drawerService.GetSettingAsync(
                    DesktopBoxViewModel.GetMappingViewModeSettingKey(member.BoxId)));
            Assert.Equal(
                "List",
                await drawerService.GetSettingAsync(
                    DesktopBoxViewModel.GetMappingViewModeSettingKey(host.BoxId)));

            // 切回宿主标签：宿主自己的模式仍然保留。
            host.ActiveTabVm = host;
            Assert.True(host.IsMappingListMode);
            Assert.True(host.IsActiveTab);
            Assert.False(member.IsActiveTab);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task MergedHost_UsesOneSharedWidthForGridAndList()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, todoService) = await CreateServicesAsync(root);
            var host = CreateViewModel("工具", BoxType.Mapping, drawerService, todoService);
            var member = CreateViewModel("AI", BoxType.Mapping, drawerService, todoService);

            // 单盒（未合并）不启用统一宽度，保持原来的"宽度跟内容走"。
            Assert.False(host.UsesUnifiedBoxSize);

            host.IsTabHost = true;
            host.InitializeTabs([member]);

            // 合并宿主（显示的是映射盒）启用统一宽度：网格与列表共用它。
            Assert.True(host.UsesUnifiedBoxSize);
            var defaultWidth = host.HostBoxWidth;
            Assert.InRange(
                defaultWidth,
                DesktopBoxViewModel.MinimumMappingListWidth,
                DesktopBoxViewModel.MaximumMappingListWidth);

            // 列表视图宽度由统一宽度换算（扣掉列表窗口外边距），比外宽小但同源。
            Assert.InRange(host.MappingListViewWidth, 1, defaultWidth);

            // 拖拽宽度（列表模式那套手柄）改的是同一个值，两种模式一起变。
            host.ResizeMappingListWidth(300);
            Assert.Equal(300, host.HostBoxWidth);
            Assert.True(host.MappingListViewWidth < 300);

            // 用户拖过宽度后内容不再影响它：切到列表模式也不会被撑宽。
            await host.UseMappingListModeCommand.ExecuteAsync(null);
            Assert.True(host.IsMappingListMode);
            Assert.Equal(300, host.HostBoxWidth);

            // 过窄仍被夹到下限，不会把内容裁掉。
            host.ResizeMappingListWidth(40);
            Assert.Equal(DesktopBoxViewModel.MinimumMappingListWidth, host.HostBoxWidth);

            // 切标签前后外宽一致（除内容确实更宽的情况，本用例内容为空）。
            var widthBeforeSwitch = host.HostBoxWidth;
            host.ActiveTabVm = member;
            Assert.Equal(widthBeforeSwitch, host.HostBoxWidth);

            // 高度：没拖过时不自适应以外的约束（NaN = 仍然按内容定高）。
            Assert.False(host.HasUnifiedHeight);
            Assert.True(double.IsNaN(host.HostBoxHeight));

            // 拖过高度后固定下来，切换标签（网格标签 ↔ 列表标签）不再变高变矮。
            host.ResizeMappingListHeight(340);
            Assert.True(host.HasUnifiedHeight);
            Assert.Equal(340, host.HostBoxHeight);

            host.ActiveTabVm = host;
            Assert.Equal(340, host.HostBoxHeight);
            Assert.True(host.HasUnifiedHeight);

            host.ActiveTabVm = member;
            Assert.Equal(340, host.HostBoxHeight);
            Assert.True(host.HasUnifiedHeight);

            // 合并/解散必须通知 UsesUnifiedBoxSize：窗口靠它决定"宽度按内容自适应"还是"固定尺寸"。
            var raisedOnMergeToggle = new List<string>();
            host.PropertyChanged += (_, args) => raisedOnMergeToggle.Add(args.PropertyName ?? string.Empty);
            host.IsTabHost = false;
            host.IsTabHost = true;
            Assert.Contains(nameof(DesktopBoxViewModel.UsesUnifiedBoxSize), raisedOnMergeToggle);
            Assert.Contains(nameof(DesktopBoxViewModel.HostBoxWidth), raisedOnMergeToggle);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CoverView_TakesItsSizeFromTheDrawerCoverHandle()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, todoService) = await CreateServicesAsync(root);
            var host = CreateViewModel("工具", BoxType.Mapping, drawerService, todoService);
            var member = CreateViewModel("AI", BoxType.Mapping, drawerService, todoService);
            host.IsTabHost = true;
            host.InitializeTabs([member]);

            // 全局"抽屉式收纳"打开时，网格模式下发显示的是抽屉封面（封面自带尺寸手柄）。
            await drawerService.SetSettingAsync(DesktopBoxViewModel.DrawerStyleSettingKey, "true");
            await host.LoadDrawerStyleAsync();
            Assert.True(host.IsDrawerCollapsed);

            // 拖封面手柄 → 统一外宽跟着封面走，窗口和封面只会有一套尺寸手柄。
            host.ResizeDrawerCover(260, 200);
            Assert.Equal(
                host.DrawerCoverWidth + DesktopBoxViewModel.WindowBorderInset,
                host.HostBoxWidth,
                precision: 6);
            // 封面视图高度由封面自己决定：不钉固定高度，交给窗口自适应。
            Assert.False(host.HasUnifiedHeight);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task EveryTab_CanBeDetached_IncludingHostItself()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, todoService) = await CreateServicesAsync(root);
            var host = CreateViewModel("工具", BoxType.Mapping, drawerService, todoService);
            var member = CreateViewModel("AI", BoxType.Mapping, drawerService, todoService);

            host.IsTabHost = true;
            host.InitializeTabs([member]);

            // 所有标签（含宿主自身）都应能通过右键菜单移出：
            // 移出宿主时由服务层让成员继任宿主，组不散。
            // 这里验证 VM 层不阻止任何标签：TabVms 里每个 VM 都对应一个可移出标签。
            Assert.Equal(2, host.TabVms.Count);
            Assert.Contains(host, host.TabVms);
            Assert.Contains(member, host.TabVms);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CopySizeSnapshotFromAsync_WritesIndependentSizeKeys()
    {
        var root = CreateTempRoot();
        try
        {
            var (drawerService, todoService) = await CreateServicesAsync(root);
            var host = CreateViewModel("工具", BoxType.Mapping, drawerService, todoService);
            var member = CreateViewModel("AI", BoxType.Mapping, drawerService, todoService);

            // 宿主设定自定义宽高（模拟合并后用户在宿主上调整过尺寸）。
            host.ResizeMappingListWidth(300);
            host.ResizeMappingListHeight(340);

            // 快照复制：宿主的当前宽高写入成员的独立设置键（复制一份，不是引用）。
            await member.CopySizeSnapshotFromAsync(host);

            // 成员的独立键已写入，值等于宿主的当前宽高。
            Assert.Equal(
                "300",
                await drawerService.GetSettingAsync(
                    DesktopBoxViewModel.GetMappingListWidthSettingKey(member.BoxId)));
            Assert.Equal(
                "340",
                await drawerService.GetSettingAsync(
                    DesktopBoxViewModel.GetMappingListHeightSettingKey(member.BoxId)));

            // 快照是单向复制：只写成员的键，不写宿主的键（宿主尺寸由宿主自己管理）。
            Assert.Null(await drawerService.GetSettingAsync(
                DesktopBoxViewModel.GetMappingListWidthSettingKey(host.BoxId)));

            // 成员重新装载后保持合并时的尺寸（移出即恢复该尺寸，而不是回退默认值）。
            await member.LoadMappingListWidthAsync();
            await member.LoadMappingListHeightAsync();
            Assert.Equal(300, member.MappingListWidth);
            Assert.Equal(340, member.MappingListHeight);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "WitchDrawer.TabHost",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<(DrawerService DrawerService, TodoService TodoService)> CreateServicesAsync(
        string root)
    {
        var paths = new AppPaths(root);
        var repository = new DrawerRepository(paths.DatabasePath);
        var drawerService = new DrawerService(paths, repository);
        await drawerService.InitializeAsync();
        return (drawerService, new TodoService(repository));
    }

    private static DesktopBoxViewModel CreateViewModel(
        string name,
        BoxType type,
        DrawerService drawerService,
        TodoService todoService)
    {
        var now = DateTimeOffset.UtcNow;
        var box = new Box(
            Guid.NewGuid(),
            name,
            type,
            StoragePath: null,
            SortOrder: 0,
            now,
            now);
        return new DesktopBoxViewModel(
            box,
            drawerService,
            todoService,
            new NoopLauncher(),
            NullAppLogger.Instance,
            BoxVisualStyle.Modern);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // SQLite 连接池或文件句柄可能还没释放，临时目录残留不影响测试结论。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class NoopLauncher : IFileLauncher
    {
        public Task OpenAsync(string path, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
