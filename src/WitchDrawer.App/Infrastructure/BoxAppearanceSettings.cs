namespace WitchDrawer.App.Infrastructure;

/// <summary>
/// Live global values for desktop box appearance settings.
/// Kept in a static holder so newly created boxes can read the current
/// value even if the change message was published before they existed.
/// </summary>
public static class BoxAppearanceSettings
{
    public const double DefaultCornerRadius = 20;
    public const double MinCornerRadius = 0;
    public const double MaxCornerRadius = 40;

    public static double CornerRadius { get; set; } = DefaultCornerRadius;
}
