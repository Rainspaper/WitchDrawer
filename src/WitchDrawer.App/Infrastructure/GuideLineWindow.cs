using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using WitchDrawer.Native.Windows;

namespace WitchDrawer.App.Infrastructure;

public sealed class GuideLineWindow : Window
{
    private const double OverlayThickness = 4;
    private static readonly nint WindowPositionTopmost = -1;
    private const uint SetWindowPosNoActivate = 0x0010;

    private readonly bool _isVertical;
    private readonly Canvas _canvas;
    private readonly List<Line> _lines = [];
    private HwndSource? _source;

    public GuideLineWindow(bool isVertical)
    {
        _isVertical = isVertical;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Width = OverlayThickness;
        Height = OverlayThickness;

        _canvas = new Canvas();
        Content = _canvas;
    }

    public void UpdateLine(double x1, double y1, double x2, double y2)
    {
        UpdateLines([(x1, y1, x2, y2)]);
    }

    public void UpdateLines(IReadOnlyList<(double X1, double Y1, double X2, double Y2)> segments)
    {
        if (segments.Count == 0)
        {
            Hide();
            return;
        }

        var bounds = CalculateOverlayBounds(_isVertical, segments[0].X1, segments[0].Y1, segments[0].X2, segments[0].Y2);
        for (var i = 1; i < segments.Count; i++)
        {
            var segment = segments[i];
            var segmentBounds = CalculateOverlayBounds(_isVertical, segment.X1, segment.Y1, segment.X2, segment.Y2);
            bounds.Union(segmentBounds);
        }

        var handle = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(
            handle,
            WindowPositionTopmost,
            ToNativeCoordinate(bounds.Left),
            ToNativeCoordinate(bounds.Top),
            Math.Max(1, ToNativeCoordinate(bounds.Width)),
            Math.Max(1, ToNativeCoordinate(bounds.Height)),
            SetWindowPosNoActivate);

        var dpi = VisualTreeHelper.GetDpi(this);
        var widthDip = bounds.Width / dpi.DpiScaleX;
        var heightDip = bounds.Height / dpi.DpiScaleY;
        Width = widthDip;
        Height = heightDip;

        _canvas.Children.Clear();
        _lines.Clear();

        foreach (var segment in segments)
        {
            var line = new Line
            {
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection(new double[] { 4, 3 }),
                SnapsToDevicePixels = true
            };
            line.SetResourceReference(Shape.StrokeProperty, "AccentBrush");

            if (_isVertical)
            {
                line.X1 = (segment.X1 - bounds.Left) / dpi.DpiScaleX;
                line.X2 = (segment.X2 - bounds.Left) / dpi.DpiScaleX;
                line.Y1 = (Math.Min(segment.Y1, segment.Y2) - bounds.Top) / dpi.DpiScaleY;
                line.Y2 = (Math.Max(segment.Y1, segment.Y2) - bounds.Top) / dpi.DpiScaleY;
            }
            else
            {
                line.X1 = (Math.Min(segment.X1, segment.X2) - bounds.Left) / dpi.DpiScaleX;
                line.X2 = (Math.Max(segment.X1, segment.X2) - bounds.Left) / dpi.DpiScaleX;
                line.Y1 = (segment.Y1 - bounds.Top) / dpi.DpiScaleY;
                line.Y2 = (segment.Y2 - bounds.Top) / dpi.DpiScaleY;
            }

            _lines.Add(line);
            _canvas.Children.Add(line);
        }
    }

    internal static Rect CalculateOverlayBounds(
        bool isVertical,
        double x1,
        double y1,
        double x2,
        double y2)
    {
        if (isVertical)
        {
            return new Rect(
                x1 - (OverlayThickness / 2),
                Math.Min(y1, y2),
                OverlayThickness,
                Math.Max(1, Math.Abs(y2 - y1)));
        }

        return new Rect(
            Math.Min(x1, x2),
            y1 - (OverlayThickness / 2),
            Math.Max(1, Math.Abs(x2 - x1)),
            OverlayThickness);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        NonActivatingOverlayWindow.Configure(handle);
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessageHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        base.OnClosed(e);
    }

    private static nint WindowMessageHook(
        nint windowHandle,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (NonActivatingOverlayWindow.IsNonClientHitTestMessage(message))
        {
            handled = true;
            return NonActivatingOverlayWindow.TransparentHitTestResult;
        }

        return nint.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    private static int ToNativeCoordinate(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
}