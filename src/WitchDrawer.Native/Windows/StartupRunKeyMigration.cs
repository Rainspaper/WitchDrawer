using Microsoft.Win32;

namespace WitchDrawer.Native.Windows;

/// <summary>
/// 迁移 HKCU\...\CurrentVersion\Run 注册表键值：把旧版本写入的
/// 不含 --silent 的启动值补上 --silent 参数。旧值（如 v1.3.0 前的
/// `"C:\...\WitchDrawer.exe"`）会导致自启后 silentStart 判定为 false，
/// 双击桌面切换图标的自动触发仍被启用——这正是"开启自启后桌面图标
/// 被误藏"的根因。与 StartupShortcutMigration（启动文件夹快捷方式）
/// 职责对齐：仅当目标路径匹配当前可执行文件且缺少参数时才追加。
/// </summary>
public static class StartupRunKeyMigration
{
    private const string RunRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegistryKeyName = "WitchDrawer";

    /// <summary>
    /// 读取并（如需要）补写 Run 键值。返回更新计数与错误列表。
    /// </summary>
    public static StartupShortcutMigrationResult EnsureSilentArguments(
        string? executablePath,
        string silentArgument)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return StartupShortcutMigrationResult.Empty;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                RunRegistryPath,
                writable: true);
            if (key is null)
            {
                return StartupShortcutMigrationResult.Empty;
            }

            return EnsureSilentArgument(
                executablePath,
                silentArgument,
                () => key.GetValue(StartupRegistryKeyName) as string,
                value => key.SetValue(
                    StartupRegistryKeyName,
                    value,
                    RegistryValueKind.String));
        }
        catch (Exception exception)
        {
            return new StartupShortcutMigrationResult(0, [exception]);
        }
    }

    /// <summary>
    /// 从一条命令行首部解析带引号/不带引号的可执行文件路径，
    /// 并附加必需的参数（供迁移判定使用）。
    /// </summary>
    internal static bool TryGetExecutablePath(
        string commandLine,
        out string? executablePath)
    {
        executablePath = null;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var text = commandLine.TrimStart();
        if (text.Length == 0)
        {
            return false;
        }

        string candidate;
        if (text[0] == '"')
        {
            var closingQuote = text.IndexOf('"', 1);
            if (closingQuote < 0)
            {
                return false;
            }

            candidate = text[1..closingQuote];
        }
        else
        {
            var firstSpace = text.IndexOf(' ');
            candidate = firstSpace < 0 ? text : text[..firstSpace];
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        executablePath = candidate;
        return true;
    }

    /// <summary>供测试使用：把提取与追加逻辑拆成可注入的纯流程。</summary>
    internal static StartupShortcutMigrationResult EnsureSilentArgument(
        string? executablePath,
        string silentArgument,
        Func<string?> readValue,
        Action<string> writeValue)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return StartupShortcutMigrationResult.Empty;
        }

        var currentValue = readValue();
        if (string.IsNullOrWhiteSpace(currentValue))
        {
            return StartupShortcutMigrationResult.Empty;
        }

        if (!TryGetExecutablePath(currentValue, out var valueExecutable)
            || !PathsEqual(valueExecutable, executablePath))
        {
            return StartupShortcutMigrationResult.Empty;
        }

        if (ContainsArgument(currentValue, silentArgument))
        {
            return StartupShortcutMigrationResult.Empty;
        }

        var updatedValue = currentValue.Trim() + " " + silentArgument;
        writeValue(updatedValue);
        return new StartupShortcutMigrationResult(1, []);
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsArgument(string commandLine, string expectedArgument)
    {
        return commandLine
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(argument => string.Equals(
                argument.Trim('"'),
                expectedArgument,
                StringComparison.OrdinalIgnoreCase));
    }
}