using WitchDrawer.App.Infrastructure;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 快捷面板拼音首字母检索：输入 wx 命中「微信」。
/// </summary>
public sealed class PinyinInitialsTests
{
    [Theory]
    [InlineData("微信", "wx")]
    [InlineData("微信.lnk", "wxlnk")]
    [InlineData("火绒安全软件", "hraqrj")]
    [InlineData("NVIDIA App", "nvidiaapp")]
    [InlineData("Microsoft Edge", "microsoftedge")]
    [InlineData("游戏", "yx")]
    [InlineData("待办", "db")]
    public void Build_ProducesInitialsForChineseAndKeepsAscii(string text, string expected)
    {
        Assert.Equal(expected, PinyinInitials.Build(text));
    }

    [Fact]
    public void Build_SkipsWhitespaceAndSeparators()
    {
        Assert.Equal("wx", PinyinInitials.Build("微 信"));
        Assert.Equal("wx", PinyinInitials.Build("微-信"));
        Assert.Equal("wx", PinyinInitials.Build("微(信)"));
        Assert.Equal(string.Empty, PinyinInitials.Build(null));
        Assert.Equal(string.Empty, PinyinInitials.Build("   "));
    }

    [Theory]
    [InlineData("wx", true)]
    [InlineData("WX", true)]
    [InlineData("hraqrj", true)]
    // 含非 ASCII 字母：走普通子串匹配，不当首字母查询
    [InlineData("微", false)]
    [InlineData("微x", false)]
    [InlineData("w1", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void IsInitialsQuery_OnlyAcceptsPureAsciiLetters(string? query, bool expected)
    {
        Assert.Equal(expected, PinyinInitials.IsInitialsQuery(query));
    }

    [Fact]
    public void Build_SupportsCaseInsensitiveMatchingWhenLowered()
    {
        var initials = PinyinInitials.Build("微信");

        Assert.Equal("wx", initials);
        Assert.StartsWith("wx", initials, StringComparison.Ordinal);
    }
}