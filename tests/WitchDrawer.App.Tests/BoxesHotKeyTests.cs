using System.IO;
using WitchDrawer.App.Infrastructure;
using WitchDrawer.Core;
using WitchDrawer.Core.Services;
using WitchDrawer.Core.Storage;
using WitchDrawer.Native.HotKeys;

namespace WitchDrawer.App.Tests;

/// <summary>
/// 「唤起收纳盒」热键：允许为空（未设置=不注册）、序列化往返、以及回落时机的持久化。
/// </summary>
public sealed class BoxesHotKeyTests
{
    [Fact]
    public void None_IsEmptyAndValid()
    {
        Assert.True(BoxesHotKey.None.IsEmpty);
        Assert.True(BoxesHotKey.None.IsValid);
        Assert.Equal("未设置", BoxesHotKey.None.DisplayText);
        Assert.Equal(string.Empty, BoxesHotKey.None.Serialize());
    }

    [Fact]
    public void SerializeAndParse_RoundTripsConfiguredCombination()
    {
        var original = new BoxesHotKey(
            HotKeyModifiers.Control | HotKeyModifiers.Alt,
            0x44);

        Assert.True(BoxesHotKey.TryParse(original.Serialize(), out var parsed));

        Assert.Equal(original, parsed);
        Assert.False(parsed.IsEmpty);
        Assert.Equal("Ctrl + Alt + D", parsed.DisplayText);
        Assert.Equal(
            HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.NoRepeat,
            parsed.RegistrationModifiers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_TreatsBlankAsClearedState(string? value)
    {
        Assert.True(BoxesHotKey.TryParse(value, out var hotKey));
        Assert.True(hotKey.IsEmpty);
        Assert.Equal(BoxesHotKey.None, hotKey);
    }

    [Theory]
    [InlineData("not-a-hotkey")]
    [InlineData("4:41")]
    [InlineData("2:0")]
    [InlineData("4002:57")]
    public void TryParse_RejectsMalformedValues(string value)
    {
        Assert.False(BoxesHotKey.TryParse(value, out var hotKey));
        Assert.Equal(BoxesHotKey.None, hotKey);
    }

    [Theory]
    // 必须含 Ctrl/Alt/Win 之一
    [InlineData(0x00, 0x44, false)]
    [InlineData(0x04, 0x44, false)]
    [InlineData(0x02, 0x44, true)]
    [InlineData(0x01, 0x44, true)]
    [InlineData(0x08, 0x44, true)]
    // 按键码范围
    // 有修饰键但按键码为 0：既不是"空"也不是有效组合，必须拒绝
    [InlineData(0x02, 0x00, false)]
    // 既无修饰键又无按键码 = 合法的"未设置"状态
    [InlineData(0x00, 0x00, true)]
    [InlineData(0x02, 0xFF, false)]
    public void IsValid_RequiresAModifierAndASaneVirtualKey(
        uint modifiers,
        uint virtualKey,
        bool expected)
    {
        var hotKey = new BoxesHotKey((HotKeyModifiers)modifiers, virtualKey);

        Assert.Equal(expected, hotKey.IsValid);
    }

    [Theory]
    [InlineData("FocusLoss", BoxesHotKeyRevertMode.OnFocusLoss)]
    [InlineData("SecondPress", BoxesHotKeyRevertMode.OnSecondPress)]
    [InlineData(null, BoxesHotKeyRevertMode.OnFocusLoss)]
    [InlineData("garbage", BoxesHotKeyRevertMode.OnFocusLoss)]
    public void RevertMode_ParsesSafely(string? value, BoxesHotKeyRevertMode expected)
    {
        Assert.Equal(expected, BoxesHotKeyRevertModeExtensions.ParseRevertMode(value));
    }

    [Fact]
    public void RevertMode_SettingValuesRoundTrip()
    {
        Assert.Equal(
            BoxesHotKeyRevertMode.OnFocusLoss,
            BoxesHotKeyRevertModeExtensions.ParseRevertMode(
                BoxesHotKeyRevertMode.OnFocusLoss.ToSettingValue()));
        Assert.Equal(
            BoxesHotKeyRevertMode.OnSecondPress,
            BoxesHotKeyRevertModeExtensions.ParseRevertMode(
                BoxesHotKeyRevertMode.OnSecondPress.ToSettingValue()));
    }

    [Fact]
    public async Task SettingsStore_PersistsHotKeyAndRevertMode_AndDefaultsToNone()
    {
        var root = Path.Combine(Path.GetTempPath(), "WitchDrawerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var repository = new DrawerRepository(paths.DatabasePath);
            var drawerService = new DrawerService(paths, repository);
            await drawerService.InitializeAsync();
            var store = new BoxesHotKeySettingsStore(drawerService);

            // 未配置过：热键为空（不注册），回落时机默认「切换窗口时回落」。
            Assert.True((await store.LoadHotKeyAsync()).IsEmpty);
            Assert.Equal(BoxesHotKeyRevertMode.OnFocusLoss, await store.LoadRevertModeAsync());

            var configured = new BoxesHotKey(
                HotKeyModifiers.Control | HotKeyModifiers.Shift,
                0x42);
            await store.SaveHotKeyAsync(configured);
            await store.SaveRevertModeAsync(BoxesHotKeyRevertMode.OnSecondPress);

            Assert.Equal(configured, await store.LoadHotKeyAsync());
            Assert.Equal(BoxesHotKeyRevertMode.OnSecondPress, await store.LoadRevertModeAsync());

            // 清空：再次读取必须回到「未设置」。
            await store.SaveHotKeyAsync(BoxesHotKey.None);
            Assert.True((await store.LoadHotKeyAsync()).IsEmpty);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}