using System.IO;
using WitchDrawer.App.Infrastructure;
using WitchDrawer.Core;
using WitchDrawer.Core.Logging;
using WitchDrawer.Core.Services;
using WitchDrawer.Core.Storage;

namespace WitchDrawer.App.Tests;

public sealed class BoxTabGroupServiceTests
{
    [Fact]
    public async Task GetMembersAsync_EmptyWithoutSavedData()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        var members = await workspace.Service.GetMembersAsync(Guid.NewGuid());

        Assert.Empty(members);
    }

    [Fact]
    public async Task GetHostAsync_NullWithoutSavedData()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        var host = await workspace.Service.GetHostAsync(Guid.NewGuid());

        Assert.Null(host);
    }

    [Fact]
    public async Task MergeAsync_AddsMemberToHostAndSetsReverseLookup()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var hostId = Guid.NewGuid();
        var memberId = Guid.NewGuid();

        await workspace.Service.MergeAsync(memberId, hostId);

        Assert.Equal([memberId], await workspace.Service.GetMembersAsync(hostId));
        Assert.Equal(hostId, await workspace.Service.GetHostAsync(memberId));
        Assert.True(await workspace.Service.IsHostAsync(hostId));
        Assert.True(await workspace.Service.IsMemberAsync(memberId));
    }

    [Fact]
    public async Task MergeAsync_MultipleMembersPreserveOrder()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var hostId = Guid.NewGuid();
        var member1 = Guid.NewGuid();
        var member2 = Guid.NewGuid();

        await workspace.Service.MergeAsync(member1, hostId);
        await workspace.Service.MergeAsync(member2, hostId);

        Assert.Equal([member1, member2], await workspace.Service.GetMembersAsync(hostId));
    }

    [Fact]
    public async Task MergeAsync_MovingMemberRemovesFromPreviousHost()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var host1 = Guid.NewGuid();
        var host2 = Guid.NewGuid();
        var member = Guid.NewGuid();

        await workspace.Service.MergeAsync(member, host1);
        await workspace.Service.MergeAsync(member, host2);

        Assert.Empty(await workspace.Service.GetMembersAsync(host1));
        Assert.Equal([member], await workspace.Service.GetMembersAsync(host2));
        Assert.Equal(host2, await workspace.Service.GetHostAsync(member));
    }

    [Fact]
    public async Task MergeAsync_DuplicateMergeIsNoop()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var hostId = Guid.NewGuid();
        var memberId = Guid.NewGuid();

        await workspace.Service.MergeAsync(memberId, hostId);
        await workspace.Service.MergeAsync(memberId, hostId);

        Assert.Equal([memberId], await workspace.Service.GetMembersAsync(hostId));
    }

    [Fact]
    public async Task MergeAsync_ThrowsWhenMergingIntoSelf()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(
            () => workspace.Service.MergeAsync(id, id));
    }

    [Fact]
    public async Task RemoveMemberAsync_RemovesFromHost()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var hostId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await workspace.Service.MergeAsync(member, hostId);

        var removed = await workspace.Service.RemoveMemberAsync(member);

        Assert.True(removed);
        Assert.Empty(await workspace.Service.GetMembersAsync(hostId));
        Assert.Null(await workspace.Service.GetHostAsync(member));
    }

    [Fact]
    public async Task RemoveMemberAsync_ReturnsFalseForUnmergedBox()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        var removed = await workspace.Service.RemoveMemberAsync(Guid.NewGuid());

        Assert.False(removed);
    }

    [Fact]
    public async Task RemoveMemberAsync_LastMemberDissolvesHost()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var hostId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await workspace.Service.MergeAsync(member, hostId);

        await workspace.Service.RemoveMemberAsync(member);

        Assert.False(await workspace.Service.IsHostAsync(hostId));
    }

    [Fact]
    public async Task DissolveAsync_ClearsAllMembersAndHost()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var hostId = Guid.NewGuid();
        var member1 = Guid.NewGuid();
        var member2 = Guid.NewGuid();
        await workspace.Service.MergeAsync(member1, hostId);
        await workspace.Service.MergeAsync(member2, hostId);

        await workspace.Service.DissolveAsync(hostId);

        Assert.Empty(await workspace.Service.GetMembersAsync(hostId));
        Assert.Null(await workspace.Service.GetHostAsync(member1));
        Assert.Null(await workspace.Service.GetHostAsync(member2));
    }

    [Fact]
    public async Task Persistence_SurvivesNewServiceInstance()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var hostId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        await workspace.Service.MergeAsync(memberId, hostId);

        var reloaded = new BoxTabGroupService(workspace.DrawerService, workspace.Logger);

        Assert.Equal([memberId], await reloaded.GetMembersAsync(hostId));
        Assert.Equal(hostId, await reloaded.GetHostAsync(memberId));
    }

    [Fact]
    public async Task Persistence_IgnoresInvalidMemberIdInJson()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var hostId = Guid.NewGuid();
        await workspace.DrawerService.SetSettingAsync(
            "BoxTabHost:" + hostId.ToString("N"),
            "[\"not-a-guid\", \"" + Guid.NewGuid().ToString("N") + "\"]");

        var members = await workspace.Service.GetMembersAsync(hostId);

        Assert.Single(members);
    }

    private sealed class TestWorkspace : IAsyncDisposable
    {
        private TestWorkspace(
            string root,
            DrawerService drawerService,
            RecordingLogger logger,
            BoxTabGroupService service)
        {
            Root = root;
            DrawerService = drawerService;
            Logger = logger;
            Service = service;
        }

        public string Root { get; }

        public DrawerService DrawerService { get; }

        public RecordingLogger Logger { get; }

        public BoxTabGroupService Service { get; }

        public static async Task<TestWorkspace> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "WitchDrawerTests",
                Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(root);
            var repository = new DrawerRepository(paths.DatabasePath);
            var drawerService = new DrawerService(paths, repository);
            await drawerService.InitializeAsync();
            var logger = new RecordingLogger();
            return new TestWorkspace(
                root,
                drawerService,
                logger,
                new BoxTabGroupService(drawerService, logger));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Information { get; } = [];

        public List<(Exception Exception, string Message)> Errors { get; } = [];

        public void Info(string message)
        {
            Information.Add(message);
        }

        public void Error(Exception exception, string message)
        {
            Errors.Add((exception, message));
        }
    }
}
