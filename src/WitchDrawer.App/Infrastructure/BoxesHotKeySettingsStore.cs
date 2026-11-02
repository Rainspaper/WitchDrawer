using WitchDrawer.Core.Services;

namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// 「唤起收纳盒」热键与其回落时机的持久化。两项都与快捷面板热键独立，
/// 热键允许为空（表示不注册）。
/// </summary>
internal sealed class BoxesHotKeySettingsStore(DrawerService drawerService)
{
    internal const string HotKeySettingKey = "BoxesHotKey";

    internal const string RevertModeSettingKey = "BoxesHotKeyRevertMode";

    public async Task<BoxesHotKey> LoadHotKeyAsync(CancellationToken cancellationToken = default)
    {
        var savedValue = await drawerService.GetSettingAsync(HotKeySettingKey, cancellationToken);
        return BoxesHotKey.TryParse(savedValue, out var hotKey) ? hotKey : BoxesHotKey.None;
    }

    public Task SaveHotKeyAsync(BoxesHotKey hotKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hotKey);
        if (!hotKey.IsValid)
        {
            throw new ArgumentException("快捷键组合无效。", nameof(hotKey));
        }

        return drawerService.SetSettingAsync(HotKeySettingKey, hotKey.Serialize(), cancellationToken);
    }

    public async Task<BoxesHotKeyRevertMode> LoadRevertModeAsync(
        CancellationToken cancellationToken = default)
    {
        var savedValue = await drawerService.GetSettingAsync(RevertModeSettingKey, cancellationToken);
        return BoxesHotKeyRevertModeExtensions.ParseRevertMode(savedValue);
    }

    public Task SaveRevertModeAsync(
        BoxesHotKeyRevertMode mode,
        CancellationToken cancellationToken = default) =>
        drawerService.SetSettingAsync(
            RevertModeSettingKey,
            mode.ToSettingValue(),
            cancellationToken);
}