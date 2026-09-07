namespace WitchDrawer.App.Messages;

/// <summary>
/// Published when the global desktop box corner radius setting changes,
/// so every open desktop box can update its border radius live.
/// </summary>
public sealed record BoxCornerRadiusChangedMessage(double CornerRadius);
