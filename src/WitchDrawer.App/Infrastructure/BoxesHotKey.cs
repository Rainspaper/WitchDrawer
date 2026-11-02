using System.Globalization;
using System.Windows.Input;
using WitchDrawer.Native.HotKeys;

namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 「唤起全部收纳盒/待办盒」的全局热键。与快捷面板热键相互独立：
/// 允许为空（未设置 / 用户主动清除），为空时不向系统注册任何热键。
/// </summary>
public sealed record BoxesHotKey(HotKeyModifiers Modifiers, uint VirtualKey)
{
    private const HotKeyModifiers AllowedModifiers =
        HotKeyModifiers.Alt
        | HotKeyModifiers.Control
        | HotKeyModifiers.Shift
        | HotKeyModifiers.Win;

    /// <summary>未设置状态：不注册热键。</summary>
    public static BoxesHotKey None { get; } = new(HotKeyModifiers.None, 0);

    public bool IsEmpty => VirtualKey == 0 && Modifiers == HotKeyModifiers.None;

    public HotKeyModifiers RegistrationModifiers => Modifiers | HotKeyModifiers.NoRepeat;

    public string DisplayText
    {
        get
        {
            if (IsEmpty)
            {
                return "未设置";
            }

            var parts = new List<string>(5);
            if (Modifiers.HasFlag(HotKeyModifiers.Control))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Shift))
            {
                parts.Add("Shift");
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Win))
            {
                parts.Add("Win");
            }

            parts.Add(FormatKey(VirtualKey));
            return string.Join(" + ", parts);
        }
    }

    /// <summary>
    /// 空值合法（表示不注册）；非空则必须含 Ctrl/Alt/Win 且按键码在有效范围内。
    /// </summary>
    public bool IsValid =>
        IsEmpty
        || (VirtualKey is > 0 and <= 0xFE
            && (Modifiers & ~AllowedModifiers) == 0
            && (Modifiers & (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Win)) != 0);

    public string Serialize() => IsEmpty ? string.Empty : $"{(uint)Modifiers:X}:{VirtualKey:X}";

    /// <summary>
    /// 解析持久化值。<paramref name="value"/> 为空/null 时得到 <see cref="None"/> 并返回 true；
    /// 格式非法时返回 false（调用方应保留上一次的有效值）。
    /// </summary>
    public static bool TryParse(string? value, out BoxesHotKey hotKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            hotKey = None;
            return true;
        }

        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var modifiers)
            || !uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var virtualKey))
        {
            hotKey = None;
            return false;
        }

        var candidate = new BoxesHotKey((HotKeyModifiers)modifiers, virtualKey);
        if (!candidate.IsValid)
        {
            hotKey = None;
            return false;
        }

        hotKey = candidate;
        return true;
    }

    private static string FormatKey(uint virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return "Num " + ((int)(key - Key.NumPad0)).ToString(CultureInfo.InvariantCulture);
        }

        return key switch
        {
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.Next => "Page Down",
            Key.Prior => "Page Up",
            _ => key.ToString()
        };
    }
}

/// <summary>热键唤出后，盒子何时回落到最底层。</summary>
public enum BoxesHotKeyRevertMode
{
    /// <summary>用户切到别的窗口（失焦）就回落。</summary>
    OnFocusLoss = 0,

    /// <summary>保持到用户再按一次同一个热键。</summary>
    OnSecondPress = 1,
}

public static class BoxesHotKeyRevertModeExtensions
{
    public static string ToSettingValue(this BoxesHotKeyRevertMode mode) =>
        mode == BoxesHotKeyRevertMode.OnSecondPress ? "SecondPress" : "FocusLoss";

    public static BoxesHotKeyRevertMode ParseRevertMode(string? value) =>
        string.Equals(value, "SecondPress", StringComparison.OrdinalIgnoreCase)
            ? BoxesHotKeyRevertMode.OnSecondPress
            : BoxesHotKeyRevertMode.OnFocusLoss;
}