namespace WitchDrawer.App.Messages;

/// <summary>
/// Published when the global "hover to auto-expand rolled-up box" setting changes,
/// so every desktop box view model can update its live flag without a full refresh.
/// </summary>
public sealed record RollUpHoverExpandChangedMessage(bool Enabled);