using System;
using System.Windows;
using System.Windows.Threading;

namespace WitchDrawer.App.Views;

// Edge-dock auto-hide behavior for desktop boxes.
// DISABLED: This feature has been temporarily commented out per user request.
// When a box is dragged to a screen/work-area edge it docks there and hides,
// leaving only a thin visible strip. Moving the pointer over the strip slides
// the box into view; moving away slides it back out.
//
// All geometry here uses the VISIBLE bounds (the glass card, excluding the
// transparent shadow margin) in physical pixels, so the dock decision matches
// what the user actually sees on screen.
public partial class DesktopBoxWindow
{
    // DISABLED: Dock/auto-hide feature temporarily disabled
    // private static readonly TimeSpan DockEntryGrace = TimeSpan.FromMilliseconds(500);
    // private DateTime _dockEntryUtc = DateTime.MinValue;
    // private static readonly TimeSpan DockHoverInterval = TimeSpan.FromMilliseconds(100);
    // private static readonly TimeSpan DockLeaveDelay = TimeSpan.FromMilliseconds(300);
    // private const double DockThresholdPixels = 60;
    // private const double DockVisibleStripPixels = 12;
    // private DockEdge _dockEdge;
    // private double _dockNormalLeft;
    // private double _dockNormalTop;
    // private bool _dockHidden;
    // private DateTime _dockEntryUtc = DateTime.MinValue;
    // private DateTime _dockPointerLeftUtc = DateTime.MinValue;
    // private System.Windows.Threading.DispatcherTimer? _dockHoverTimer;
    // private enum DockEdge { None, Left, Right, Top, Bottom }

    /// <summary>
    /// DISABLED: Dock/auto-hide feature temporarily disabled.
    /// </summary>
    public void UpdateDockAfterDrag() { }

    /// <summary>
    /// DISABLED: Dock/auto-hide feature temporarily disabled.
    /// </summary>
    public void TryEnterDockIfAtEdge() { }

    /// <summary>
    /// DISABLED: Dock/auto-hide feature temporarily disabled.
    /// </summary>
    public void ExitDock() { }

    /// <summary>
    /// DISABLED: Dock/auto-hide feature temporarily disabled.
    /// </summary>
    public void StopDockTimer() { }

    /// <summary>
    /// DISABLED: Dock/auto-hide feature temporarily disabled.
    /// </summary>
    internal string GetDockState() => "None";

    /// <summary>
    /// DISABLED: Dock/auto-hide feature temporarily disabled.
    /// </summary>
    internal void RestoreDockState(string? dockState) { }

    /// <summary>
    /// DISABLED: Dock/auto-hide feature temporarily disabled.
    /// Returns the current window position.
    /// </summary>
    internal bool GetStoredPositionPixels(out double left, out double top)
    {
        if (TryGetWindowBoundsPixels(out var bounds))
        {
            left = bounds.Left;
            top = bounds.Top;
            return true;
        }
        left = 0;
        top = 0;
        return false;
    }

    // DISABLED: All private dock methods commented out
    // private void OnDockHoverTick(object? sender, EventArgs e) { }
    // private void SetDockHidden(bool hidden) { }
    // private Rect GetDockWindowRect() => Rect.Empty;
    // private void SnapDockNormalToEdge(Rect visible, Rect workArea) { }
    // private static bool IsNearEdge(Rect visible, Rect workArea) => false;
    // private static DockEdge GetNearestEdge(Rect visible, Rect workArea) => DockEdge.None;
}