using System.Globalization;
using WitchDrawer.Core.Models;
using WitchDrawer.Core.Storage;

namespace WitchDrawer.Core.Services;

public sealed class TodoService
{
    public const int MaximumTitleLength = 200;
    public const string DailyResetSettingPrefix = "TodoDailyReset:";

    public const string DailyResetCompletedOnSettingPrefix = "TodoDailyResetCompletedOn:";


    private readonly DrawerRepository _repository;

    public TodoService(DrawerRepository repository)
    {
        _repository = repository;
    }

    public Task<IReadOnlyList<TodoItem>> GetTodosAsync(
        Guid boxId,
        CancellationToken cancellationToken = default)
    {
        return _repository.GetTodosAsync(boxId, cancellationToken);
    }

    public Task<IReadOnlyList<TodoItem>> GetArchivedTodosAsync(
        Guid? boxId = null,
        CancellationToken cancellationToken = default)
    {
        return _repository.GetArchivedTodosAsync(boxId, cancellationToken);
    }

    public async Task<TodoItem> AddTodoAsync(
        Guid boxId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var box = await _repository.GetBoxAsync(boxId, cancellationToken)
            ?? throw new InvalidOperationException("待办盒不存在或已被删除。");
        if (box.Type != BoxType.Todo)
        {
            throw new InvalidOperationException("只能向待办盒添加待办事项。");
        }

        var normalizedTitle = NormalizeTitle(title);
        var now = DateTimeOffset.UtcNow;
        var todo = new TodoItem(
            Guid.NewGuid(),
            boxId,
            normalizedTitle,
            IsCompleted: false,
            await _repository.GetNextTodoSortOrderAsync(boxId, cancellationToken),
            now,
            now);

        await _repository.AddTodoAsync(todo, cancellationToken);
        await RefreshDailyResetMarkAsync(
            boxId,
            await GetDailyResetAsync(boxId, cancellationToken),
            DateTimeOffset.Now,
            cancellationToken);
        return todo;
    }

    public async Task<TodoItem> SetCompletedAsync(
        Guid todoId,
        bool isCompleted,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetTodoAsync(todoId, cancellationToken)
            ?? throw new InvalidOperationException("待办事项不存在或已被删除。");

        if (existing.IsCompleted == isCompleted)
        {
            return existing;
        }

        var updatedAt = DateTimeOffset.UtcNow;
        DateTimeOffset? completedAt = isCompleted ? updatedAt : null;
        await _repository.UpdateTodoCompletionAsync(
            todoId,
            isCompleted,
            completedAt,
            updatedAt,
            cancellationToken);

        await RefreshDailyResetMarkAsync(
            existing.BoxId,
            await GetDailyResetAsync(existing.BoxId, cancellationToken),
            DateTimeOffset.Now,
            cancellationToken);
        return existing with
        {
            IsCompleted = isCompleted,
            CompletedAt = completedAt,
            UpdatedAt = updatedAt
        };
    }

    public Task DeleteTodoAsync(Guid todoId, CancellationToken cancellationToken = default)
    {
        return _repository.RemoveTodoAsync(todoId, cancellationToken);
    }

    public async Task<int> ArchiveCompletedAsync(
        Guid boxId,
        CancellationToken cancellationToken = default)
    {
        var box = await _repository.GetBoxAsync(boxId, cancellationToken)
            ?? throw new InvalidOperationException("待办盒不存在或已被删除。");
        if (box.Type != BoxType.Todo)
        {
            throw new InvalidOperationException("只能归档待办盒中的事项。");
        }

        return await _repository.ArchiveCompletedTodosAsync(
            boxId,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public async Task<TodoItem> RestoreArchivedAsync(
        Guid todoId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetTodoAsync(todoId, cancellationToken)
            ?? throw new InvalidOperationException("归档事项不存在或已被删除。");

        if (!existing.IsArchived)
        {
            return existing;
        }

        var updatedAt = DateTimeOffset.UtcNow;
        await _repository.UpdateTodoArchiveStateAsync(
            todoId,
            isArchived: false,
            archivedAt: null,
            updatedAt,
            cancellationToken);

        return existing with
        {
            IsArchived = false,
            ArchivedAt = null,
            UpdatedAt = updatedAt
        };
    }

    /// <summary>读取某个待办盒是否开启了「每日循环」。</summary>
    public async Task<bool> GetDailyResetAsync(
        Guid boxId,
        CancellationToken cancellationToken = default)
    {
        var value = await _repository.GetSettingAsync(
            GetDailyResetSettingKey(boxId),
            cancellationToken);
        return bool.TryParse(value, out var enabled) && enabled;
    }

    /// <summary>
    /// 开关「每日循环」。开启时若当前已全部勾选完成，就把「完成日」记成今天，
    /// 这样第二天才会自动重置（今天完成的任务当天不会被动到）。
    /// </summary>
    public async Task SetDailyResetAsync(
        Guid boxId,
        bool enabled,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var box = await _repository.GetBoxAsync(boxId, cancellationToken)
            ?? throw new InvalidOperationException("待办盒不存在或已被删除。");
        if (box.Type != BoxType.Todo)
        {
            throw new InvalidOperationException("只有待办盒支持每日循环。");
        }

        await _repository.SetSettingAsync(
            GetDailyResetSettingKey(boxId),
            enabled ? bool.TrueString : bool.FalseString,
            cancellationToken);

        await RefreshDailyResetMarkAsync(boxId, enabled, now, cancellationToken);
    }

    /// <summary>
    /// 执行一次每日循环检查：「上次全部完成」的日期早于今天，就把勾选全部清空。
    /// 返回被清除勾选的事项数量。
    /// </summary>
    public async Task<int> ApplyDailyResetAsync(
        Guid boxId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!await GetDailyResetAsync(boxId, cancellationToken))
        {
            return 0;
        }

        var todos = await _repository.GetTodosAsync(boxId, cancellationToken);
        if (todos.Count == 0 || !todos.All(todo => todo.IsCompleted))
        {
            await ClearDailyResetMarkAsync(boxId, cancellationToken);
            return 0;
        }

        var today = FormatDay(now);
        var completedOn = await _repository.GetSettingAsync(
            GetDailyResetCompletedOnKey(boxId),
            cancellationToken);
        if (string.IsNullOrEmpty(completedOn))
        {
            // 刚开启每日循环、或标记丢了：从今天开始算，明天再重置。
            await _repository.SetSettingAsync(
                GetDailyResetCompletedOnKey(boxId),
                today,
                cancellationToken);
            return 0;
        }

        if (string.CompareOrdinal(completedOn, today) >= 0)
        {
            return 0;
        }

        var resetCount = await _repository.ResetTodoCompletionsAsync(
            boxId,
            now,
            cancellationToken);
        await ClearDailyResetMarkAsync(boxId, cancellationToken);
        return resetCount;
    }

    /// <summary>对所有待办盒执行一次每日循环检查，返回真的发生了重置的盒子。</summary>
    public async Task<IReadOnlyList<Guid>> ApplyDailyResetsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var boxes = await _repository.GetBoxesAsync(cancellationToken);
        var resetBoxIds = new List<Guid>();
        foreach (var box in boxes)
        {
            if (box.Type != BoxType.Todo)
            {
                continue;
            }

            if (await ApplyDailyResetAsync(box.Id, now, cancellationToken) > 0)
            {
                resetBoxIds.Add(box.Id);
            }
        }

        return resetBoxIds;
    }

    public static string GetDailyResetSettingKey(Guid boxId) =>
        DailyResetSettingPrefix + boxId.ToString("N");

    public static string GetDailyResetCompletedOnKey(Guid boxId) =>
        DailyResetCompletedOnSettingPrefix + boxId.ToString("N");

    private static string FormatDay(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private Task ClearDailyResetMarkAsync(
        Guid boxId,
        CancellationToken cancellationToken = default)
    {
        return _repository.DeleteSettingAsync(
            GetDailyResetCompletedOnKey(boxId),
            cancellationToken);
    }

    /// <summary>
    /// 维护「完成日」标记：只有「开启每日循环 + 当前全部勾选完成」时才记今天，
    /// 其它情况一律清掉，避免部分完成的任务第二天被误重置。
    /// </summary>
    private async Task RefreshDailyResetMarkAsync(
        Guid boxId,
        bool enabled,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            await ClearDailyResetMarkAsync(boxId, cancellationToken);
            return;
        }

        var todos = await _repository.GetTodosAsync(boxId, cancellationToken);
        if (todos.Count == 0 || !todos.All(todo => todo.IsCompleted))
        {
            await ClearDailyResetMarkAsync(boxId, cancellationToken);
            return;
        }

        await _repository.SetSettingAsync(
            GetDailyResetCompletedOnKey(boxId),
            FormatDay(now),
            cancellationToken);
    }

    private static string NormalizeTitle(string title)
    {
        var normalized = title?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("待办内容不能为空。", nameof(title));
        }

        if (normalized.Length > MaximumTitleLength)
        {
            throw new ArgumentException(
                $"待办内容不能超过 {MaximumTitleLength} 个字符。",
                nameof(title));
        }

        return normalized;
    }
}
