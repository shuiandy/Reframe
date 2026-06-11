using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Reframe.Core;
using Reframe.Services;
using Windows.Foundation;
using Windows.Graphics;

namespace Reframe.UI;

/// <summary>
/// Screenshot-style region picker: opens a full-screen, borderless, always-on-top scrim over a given
/// monitor and lets the user drag out a rectangle. Returns a physical-pixel rect relative to that
/// monitor's top-left corner; Esc / right-click to cancel -> null.
/// </summary>
public sealed partial class RegionPickerWindow : Window
{
    private readonly MonitorDesc _monitor;
    private readonly TaskCompletionSource<RectPx?> _tcs = new();

    private bool _dragging;
    private Point _startDip;     // start point (DIP, relative to Root)
    private Point _currentDip;

    private RegionPickerWindow(MonitorDesc monitor)
    {
        InitializeComponent();
        _monitor = monitor;

        ConfigureWindow();

        // Crosshair cursor.
        Root.SetCursor(InputSystemCursor.Create(InputSystemCursorShape.Cross));

        // Keyboard: Esc cancels. Needs focus, so set it after Activated.
        Root.KeyDown += Root_KeyDown;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    /// <summary>Drag out a region on a full-screen scrim over the given monitor. Returns a physical-pixel rect; cancel -> null.</summary>
    public static Task<RectPx?> PickAsync(MonitorDesc monitor)
    {
        var win = new RegionPickerWindow(monitor);
        win.Activate();
        return win._tcs.Task;
    }

    private void ConfigureWindow()
    {
        var appWindow = AppWindow;

        // Borderless + always-on-top + non-resizable.
        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }

        // Cover the whole monitor (physical pixels = the MonitorDesc's rcMonitor).
        appWindow.MoveAndResize(new RectInt32(_monitor.X, _monitor.Y, _monitor.Width, _monitor.Height));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Ensure it can receive keyboard events.
        Root.Focus(FocusState.Programmatic);
    }

    // Current DPI scale (DIP -> physical pixels). The window covers the whole monitor and Root is the
    // same size as the window, so a DIP offset within Root x scale = the physical-pixel offset on screen.
    private double Scale => Root.XamlRoot?.RasterizationScale is double s and > 0 ? s : 1.0;

    // ---------- Pointer ----------
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(Root);
        // Only the left button starts a selection (the right button is handled by RightTapped to cancel).
        if (pt.Properties.IsRightButtonPressed) return;

        _dragging = true;
        _startDip = pt.Position;
        _currentDip = pt.Position;
        Root.CapturePointer(e.Pointer);
        UpdateSelectionVisual();
        SelectionRect.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _currentDip = e.GetCurrentPoint(Root).Position;
        UpdateSelectionVisual();
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);
        _currentDip = e.GetCurrentPoint(Root).Position;

        var result = ToPhysicalRect();
        // Too small is treated as a mis-click -> cancel.
        if (result is null || result.W < 2 || result.H < 2)
            Finish(null);
        else
            Finish(result);
    }

    private void Root_RightTapped(object sender, RightTappedRoutedEventArgs e) => Finish(null);

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Finish(null);
        }
    }

    // ---------- Visuals ----------
    private void UpdateSelectionVisual()
    {
        double x = Math.Min(_startDip.X, _currentDip.X);
        double y = Math.Min(_startDip.Y, _currentDip.Y);
        double w = Math.Abs(_currentDip.X - _startDip.X);
        double h = Math.Abs(_currentDip.Y - _startDip.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;

        // The badge shows the physical-pixel size.
        double scale = Scale;
        int pw = (int)Math.Round(w * scale);
        int ph = (int)Math.Round(h * scale);
        SizeText.Text = Loc.T("RegionPickerWindow/SizeBadgeFormat", pw, ph);

        // The badge follows the selection's bottom-right corner (without going off-screen).
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bx = x + w + 8;
        double by = y + h + 8;
        double rootW = Root.ActualWidth, rootH = Root.ActualHeight;
        if (bx + SizeBadge.DesiredSize.Width > rootW) bx = x + w - SizeBadge.DesiredSize.Width;
        if (by + SizeBadge.DesiredSize.Height > rootH) by = y + h - SizeBadge.DesiredSize.Height - 4;
        Canvas.SetLeft(SizeBadge, Math.Max(0, bx));
        Canvas.SetTop(SizeBadge, Math.Max(0, by));
    }

    // DIP selection -> physical-pixel rect (relative to the monitor's top-left corner).
    private RectPx? ToPhysicalRect()
    {
        double scale = Scale;
        double x = Math.Min(_startDip.X, _currentDip.X);
        double y = Math.Min(_startDip.Y, _currentDip.Y);
        double w = Math.Abs(_currentDip.X - _startDip.X);
        double h = Math.Abs(_currentDip.Y - _startDip.Y);

        // Clamp within the screen (physical pixels).
        int px = (int)Math.Round(x * scale);
        int py = (int)Math.Round(y * scale);
        int pw = (int)Math.Round(w * scale);
        int ph = (int)Math.Round(h * scale);

        px = Math.Clamp(px, 0, _monitor.Width);
        py = Math.Clamp(py, 0, _monitor.Height);
        pw = Math.Clamp(pw, 0, _monitor.Width - px);
        ph = Math.Clamp(ph, 0, _monitor.Height - py);

        return new RectPx { X = px, Y = py, W = pw, H = ph };
    }

    // ---------- Teardown ----------
    private void Finish(RectPx? result)
    {
        // Guard against double-fire (release and keypress can race).
        if (_tcs.Task.IsCompleted) { Close(); return; }
        _tcs.TrySetResult(result);
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // If closed without a result (e.g. an external Close), treat it as a cancel.
        _tcs.TrySetResult(null);
    }
}
