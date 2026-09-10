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
/// 标签栏每格显示的是「盒子自己的名字」（<see cref="DesktopBoxViewModel.TabLabel"/>），
/// 不是会跟随当前选中项的窗口名（<see cref="DesktopBoxViewModel.Name"/>）。
/// 回归场景：宿主盒的 Name 代理到活动标签，用它渲染标签栏会让第一格标签的文字
/// 跟着被选中的标签一起变（选中标签2 → 第一格显示标签2的文字）。
/// </summary>
public sealed class TabLabelNamingTests
{
    [Fact]
    public async Task TabLabel_KeepsOwnName_WhileNameFollowsTheActiveTab()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "WitchDrawer.TabLabel",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var repository = new DrawerRepository(paths.DatabasePath);
            var drawerService = new DrawerService(paths, repository);
            await drawerService.InitializeAsync();
            var todoService = new TodoService(repository);

            var host = CreateViewModel("工具", drawerService, todoService);
            var member = CreateViewModel("便携优化", drawerService, todoService);

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

    private static DesktopBoxViewModel CreateViewModel(
        string name,
        DrawerService drawerService,
        TodoService todoService)
    {
        var now = DateTimeOffset.UtcNow;
        var box = new Box(
            Guid.NewGuid(),
            name,
            BoxType.Normal,
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
