using System.Globalization;
using System.Text;

namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 把中文串转成「拼音首字母」串，用于快捷面板的模糊搜索（输入 wx 命中「微信」）。
/// <para>
/// 实现是纯排序键比较，不引入任何第三方拼音库、也不加载词库：
/// 用 zh-CN 的 <see cref="CompareInfo"/> 把每个汉字与 26 个拼音锚点汉字做一次比较，
/// 落在哪个区间就取哪个字母。代价是 26 次比较/汉字，但只在加载索引时算一次，
/// 之后是按首字母串做普通字符串匹配，因此空闲 CPU 与内存占用都不受影响。
/// </para>
/// <para>
/// 已知取舍：多音字只取字典序区间对应的那个首字母（如「重」可能得到 c），
/// 因此首字母匹配适合"能想起来"的检索；精确汉字仍走原有的子串匹配，两者是并集。
/// </para>
/// </summary>
internal static class PinyinInitials
{
    private const string AnchorChars = "阿八擦搭蛾发噶哈击喀垃妈拿哦啪期然撒塌挖昔压匝";

    private static readonly CompareInfo ZhCnCompare = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;

    private static readonly char[] Anchors = AnchorChars.ToCharArray();

    private static readonly char[] AnchorLetters =
        ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'w', 'x', 'y', 'z'];

    /// <summary>
    /// 生成首字母串（小写）。非汉字原样保留（转小写），便于 "wx" 命中 "微 信"、"WX" 也命中。
    /// </summary>
    public static string Build(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || IsSeparator(ch))
            {
                continue;
            }

            if (TryGetInitial(ch, out var letter))
            {
                builder.Append(letter);
            }
            else
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 是否可以用首字母串匹配该查询：查询全是 ASCII 字母（不与汉字输入冲突）。
    /// </summary>
    public static bool IsInitialsQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        foreach (var ch in query)
        {
            if (!char.IsAsciiLetter(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSeparator(char ch) =>
        ch is '_' or '-' or '.' or '(' or ')' or '[' or ']' or '（' or '）' or '【' or '】' or '《' or '》';

    private static bool TryGetInitial(char ch, out char initial)
    {
        initial = '\0';
        // 只有 CJK 统一表意文字（含扩展 A 基本区）才走比对，其余（ASCII/数字/符号）走原样保留。
        if (ch is < '\u3400' or > '\u9FFF')
        {
            return false;
        }

        var band = -1;
        for (var index = 0; index < Anchors.Length; index++)
        {
            if (ZhCnCompare.Compare(ch.ToString(), Anchors[index].ToString()) >= 0)
            {
                band = index;
            }
            else
            {
                break;
            }
        }

        if (band < 0)
        {
            // 比 "阿" 还靠前（极少见）：放弃首字母，交给原样保留。
            return false;
        }

        initial = AnchorLetters[Math.Min(band, AnchorLetters.Length - 1)];
        return true;
    }
}