using WitchDrawer.Core;
using WitchDrawer.Core.Models;
using WitchDrawer.Core.Services;
using WitchDrawer.Core.Storage;

namespace WitchDrawer.Core.Tests;

public sealed class TodoServiceTests
{
    [Fact]
    public async Task AddTodoAsync_PersistsTrimmedTitleAndSortOrder()
    {
        using var workspace = await TodoWorkspace.CreateAsync();

        var first = await workspace.Service.AddTodoAsync(workspace.BoxId, "  first task  ");
        var second = await workspace.Service.AddTodoAsync(workspace.BoxId, "second task");

        var reloadedService = new TodoService(new DrawerRepository(workspace.DatabasePath));
        var todos = await reloadedService.GetTodosAsync(workspace.BoxId);

        Assert.Collection(
            todos,
            item =>
            {
                Assert.Equal(first.Id, item.Id);
                Assert.Equal(workspace.BoxId, item.BoxId);
                Assert.Equal("first task", item.Title);
                Assert.Equal(0, item.SortOrder);
                Assert.False(item.IsCompleted);
            },
            item =>
            {
                Assert.Equal(second.Id, item.Id);
                Assert.Equal(1, item.SortOrder);
            });
    }

    [Fact]
    public async Task AddTodoAsync_RejectsEmptyAndOverlongTitles()
    {
        using var workspace = await TodoWorkspace.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Service.AddTodoAsync(workspace.BoxId, "   "));
        await Assert.ThrowsAsync<ArgumentException>(
            () => workspace.Service.AddTodoAsync(
                workspace.BoxId,
                new string('x', TodoService.MaximumTitleLength + 1)));

        Assert.Empty(await workspace.Service.GetTodosAsync(workspace.BoxId));
    }

    [Fact]
    public async Task SetCompletedAsync_UpdatesStateAndMovesCompletedItemAfterActiveItems()
    {
        using var workspace = await TodoWorkspace.CreateAsync();

        var first = await workspace.Service.AddTodoAsync(workspace.BoxId, "first");
        var second = await workspace.Service.AddTodoAsync(workspace.BoxId, "second");

        var completed = await workspace.Service.SetCompletedAsync(first.Id, isCompleted: true);
        var afterCompletion = await workspace.Service.GetTodosAsync(workspace.BoxId);

        Assert.True(completed.IsCompleted);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal([second.Id, first.Id], afterCompletion.Select(item => item.Id));

        var reopened = await workspace.Service.SetCompletedAsync(first.Id, isCompleted: false);

        Assert.False(reopened.IsCompleted);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public async Task DeleteTodoAsync_RemovesOnlyRequestedItem()
    {
        using var workspace = await TodoWorkspace.CreateAsync();

        var kept = await workspace.Service.AddTodoAsync(workspace.BoxId, "keep");
        var removed = await workspace.Service.AddTodoAsync(workspace.BoxId, "remove");

        await workspace.Service.DeleteTodoAsync(removed.Id);

        var remaining = Assert.Single(await workspace.Service.GetTodosAsync(workspace.BoxId));
        Assert.Equal(kept.Id, remaining.Id);
    }

    [Fact]
    public async Task ArchiveCompletedAsync_HidesCompletedItemsAndRestoreReturnsThemToBox()
    {
        using var workspace = await TodoWorkspace.CreateAsync();

        var active = await workspace.Service.AddTodoAsync(workspace.BoxId, "still active");
        var completed = await workspace.Service.AddTodoAsync(workspace.BoxId, "ready to archive");
        await workspace.Service.SetCompletedAsync(completed.Id, isCompleted: true);

        var archivedCount = await workspace.Service.ArchiveCompletedAsync(workspace.BoxId);

        Assert.Equal(1, archivedCount);
        var remaining = Assert.Single(await workspace.Service.GetTodosAsync(workspace.BoxId));
        Assert.Equal(active.Id, remaining.Id);

        var reloadedService = new TodoService(new DrawerRepository(workspace.DatabasePath));
        var archived = Assert.Single(await reloadedService.GetArchivedTodosAsync());
        Assert.Equal(completed.Id, archived.Id);
        Assert.True(archived.IsCompleted);
        Assert.True(archived.IsArchived);
        Assert.NotNull(archived.ArchivedAt);

        var restored = await reloadedService.RestoreArchivedAsync(completed.Id);

        Assert.False(restored.IsArchived);
        Assert.Null(restored.ArchivedAt);
        Assert.Empty(await reloadedService.GetArchivedTodosAsync());
        Assert.Equal(
            [active.Id, completed.Id],
            (await reloadedService.GetTodosAsync(workspace.BoxId)).Select(item => item.Id));
    }

    [Fact]
    public async Task ArchiveCompletedAsync_ArchivesOnlyTheRequestedTodoBox()
    {
        using var workspace = await TodoWorkspace.CreateAsync();
        var secondBox = await workspace.DrawerService.CreateBoxAsync("second todo", BoxType.Todo);
        var firstBoxTodo = await workspace.Service.AddTodoAsync(workspace.BoxId, "first box");
        var secondBoxTodo = await workspace.Service.AddTodoAsync(secondBox.Id, "second box");
        await workspace.Service.SetCompletedAsync(firstBoxTodo.Id, isCompleted: true);
        await workspace.Service.SetCompletedAsync(secondBoxTodo.Id, isCompleted: true);

        await workspace.Service.ArchiveCompletedAsync(workspace.BoxId);

        Assert.Empty(await workspace.Service.GetTodosAsync(workspace.BoxId));
        Assert.Single(await workspace.Service.GetTodosAsync(secondBox.Id));
        var archived = Assert.Single(await workspace.Service.GetArchivedTodosAsync(workspace.BoxId));
        Assert.Equal(firstBoxTodo.Id, archived.Id);
    }

    [Fact]
    public async Task TodoBoxes_KeepIndependentListsAndCascadeOnBoxDelete()
    {
        using var workspace = await TodoWorkspace.CreateAsync();
        var secondBox = await workspace.DrawerService.CreateBoxAsync("second todo", BoxType.Todo);

        await workspace.Service.AddTodoAsync(workspace.BoxId, "first box task");
        await workspace.Service.AddTodoAsync(secondBox.Id, "second box task");

        Assert.Single(await workspace.Service.GetTodosAsync(workspace.BoxId));
        Assert.Single(await workspace.Service.GetTodosAsync(secondBox.Id));

        await workspace.DrawerService.DeleteBoxAsync(workspace.BoxId);

        Assert.Empty(await workspace.Service.GetTodosAsync(workspace.BoxId));
        Assert.Single(await workspace.Service.GetTodosAsync(secondBox.Id));
    }

    [Fact]
    public async Task DailyReset_ClearsCompletionsOnlyOnTheNextDay()
    {
        using var workspace = await TodoWorkspace.CreateAsync();
        var today = DateTimeOffset.Now;
        var tomorrow = today.AddDays(1);
        var first = await workspace.Service.AddTodoAsync(workspace.BoxId, "first");
        var second = await workspace.Service.AddTodoAsync(workspace.BoxId, "second");
        await workspace.Service.SetDailyResetAsync(workspace.BoxId, enabled: true, today);

        // 当天全部完成：只是记下完成日，不重置。
        await workspace.Service.SetCompletedAsync(first.Id, isCompleted: true);
        await workspace.Service.SetCompletedAsync(second.Id, isCompleted: true);

        var sameDay = await workspace.Service.ApplyDailyResetAsync(workspace.BoxId, today);
        Assert.Equal(0, sameDay);
        Assert.All(await workspace.Service.GetTodosAsync(workspace.BoxId), todo => Assert.True(todo.IsCompleted));

        // 第二天：全部清回未完成。
        var nextDay = await workspace.Service.ApplyDailyResetAsync(workspace.BoxId, tomorrow);
        Assert.Equal(2, nextDay);

        var resetTodos = await workspace.Service.GetTodosAsync(workspace.BoxId);
        Assert.All(resetTodos, todo => Assert.False(todo.IsCompleted));
        Assert.All(resetTodos, todo => Assert.Null(todo.CompletedAt));

        // 同一天再跑一次：已经重置过，不应再报数量。
        var again = await workspace.Service.ApplyDailyResetAsync(workspace.BoxId, tomorrow.AddHours(1));
        Assert.Equal(0, again);
    }

    [Fact]
    public async Task DailyReset_PartiallyCompletedListStillCountsAsDoneForTheDay()
    {
        using var workspace = await TodoWorkspace.CreateAsync();
        var today = DateTimeOffset.Now;
        var first = await workspace.Service.AddTodoAsync(workspace.BoxId, "first");
        await workspace.Service.AddTodoAsync(workspace.BoxId, "second");
        await workspace.Service.SetDailyResetAsync(workspace.BoxId, enabled: true, today);
        await workspace.Service.SetCompletedAsync(first.Id, isCompleted: true);

        // 同一天里只勾了一部分：不重置。
        var sameDay = await workspace.Service.ApplyDailyResetAsync(workspace.BoxId, today);
        Assert.Equal(0, sameDay);

        // 跨天后：部分完成也算过完当天，勾选要被清空（未勾选的保持未完成）。
        var nextDay = await workspace.Service.ApplyDailyResetAsync(workspace.BoxId, today.AddDays(1));
        Assert.Equal(1, nextDay);
        var todos = await workspace.Service.GetTodosAsync(workspace.BoxId);
        Assert.All(todos, todo => Assert.False(todo.IsCompleted));
        Assert.Equal(2, todos.Count);
    }

    [Fact]
    public async Task DailyReset_BoxWithoutAnyCompletionIsLeftAlone()
    {
        using var workspace = await TodoWorkspace.CreateAsync();
        var today = DateTimeOffset.Now;
        await workspace.Service.AddTodoAsync(workspace.BoxId, "never checked");
        await workspace.Service.SetDailyResetAsync(workspace.BoxId, enabled: true, today);

        var resetCount = await workspace.Service.ApplyDailyResetAsync(workspace.BoxId, today.AddDays(1));

        Assert.Equal(0, resetCount);
        var todo = Assert.Single(await workspace.Service.GetTodosAsync(workspace.BoxId));
        Assert.False(todo.IsCompleted);
    }


    [Fact]
    public async Task DailyReset_DisabledBoxIsNeverReset()
    {
        using var workspace = await TodoWorkspace.CreateAsync();
        var todo = await workspace.Service.AddTodoAsync(workspace.BoxId, "only");
        await workspace.Service.SetCompletedAsync(todo.Id, isCompleted: true);

        var resetCount = await workspace.Service.ApplyDailyResetAsync(
            workspace.BoxId,
            DateTimeOffset.Now.AddDays(1));

        Assert.Equal(0, resetCount);
        Assert.True((await workspace.Service.GetTodosAsync(workspace.BoxId)).Single().IsCompleted);
        Assert.False(await workspace.Service.GetDailyResetAsync(workspace.BoxId));
    }

    [Fact]
    public async Task ApplyDailyResetsAsync_ReportsOnlyBoxesThatWereReset()
    {
        using var workspace = await TodoWorkspace.CreateAsync();
        var today = DateTimeOffset.Now;
        var otherBox = await workspace.DrawerService.CreateBoxAsync("todo-2", BoxType.Todo);
        var first = await workspace.Service.AddTodoAsync(workspace.BoxId, "first");
        var other = await workspace.Service.AddTodoAsync(otherBox.Id, "other");
        await workspace.Service.SetDailyResetAsync(workspace.BoxId, enabled: true, today);
        await workspace.Service.SetCompletedAsync(first.Id, isCompleted: true);
        await workspace.Service.SetCompletedAsync(other.Id, isCompleted: true);

        var resetBoxIds = await workspace.Service.ApplyDailyResetsAsync(today.AddDays(1));

        Assert.Equal([workspace.BoxId], resetBoxIds);
        Assert.False((await workspace.Service.GetTodosAsync(workspace.BoxId)).Single().IsCompleted);
        Assert.True((await workspace.Service.GetTodosAsync(otherBox.Id)).Single().IsCompleted);
    }

    private sealed class TodoWorkspace : IDisposable
    {
        private TodoWorkspace(
            string root,
            string databasePath,
            Guid boxId,
            TodoService service,
            DrawerService drawerService)
        {
            Root = root;
            DatabasePath = databasePath;
            BoxId = boxId;
            Service = service;
            DrawerService = drawerService;
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public Guid BoxId { get; }

        public TodoService Service { get; }

        public DrawerService DrawerService { get; }

        public static async Task<TodoWorkspace> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "WitchDrawer.TodoTests", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(root);
            var databasePath = paths.DatabasePath;
            var repository = new DrawerRepository(databasePath);
            var drawerService = new DrawerService(paths, repository);
            await drawerService.InitializeAsync();
            var todoBox = await drawerService.CreateBoxAsync("todo", BoxType.Todo);

            return new TodoWorkspace(
                root,
                databasePath,
                todoBox.Id,
                new TodoService(repository),
                drawerService);
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
}
