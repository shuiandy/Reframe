using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Reframe.Interop;
using Reframe.Services;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Reframe.UI;

/// <summary>
/// The set of zones to draw on one monitor during a drag-snap session.
/// <para>Rects are physical-pixel rectangles relative to that monitor's top-left corner (in the same
/// order as the zones); Names are the corresponding names.</para>
/// </summary>
public sealed record MonitorZoneSet(MonitorDesc Monitor, IReadOnlyList<RectInt32> Rects, IReadOnlyList<string> Names);

/// <summary>
/// The snap overlay for a single monitor: borderless, always-on-top, click-through (it never steals
/// the mouse mid-drag). Draws each zone semi-transparently (light fill + border + name) and highlights
/// the zone under the cursor.
/// <para>
/// Lifetime: one per monitor for the duration of a snap session, reused (hidden rather than destroyed).
/// All static methods must be called on the UI thread (DragSnapService marshals back via DispatcherQueue).
/// </para>
/// </summary>
public sealed partial class SnapOverlayWindow : Window
{
    // Device name -> the reused overlay instance. UI-thread access only.
    private static readonly Dictionary<string, SnapOverlayWindow> _overlays = new();

    private MonitorDesc _monitor = null!;   // ShowForMonitors -> Apply always assigns it first
    private IReadOnlyList<RectInt32> _rects = System.Array.Empty<RectInt32>();
    private IReadOnlyList<string> _names = System.Array.Empty<string>();
    private int _highlighted = -1;   // index of the currently highlighted zone (within this monitor), -1 = none

    // Colors: light fill normally / darker when highlighted; the border likewise. A uniform cool blue
    // to avoid being confused with game content.
    private static readonly Color FillNormal = Color.FromArgb(0x33, 0x4F, 0xC3, 0xF7);
    private static readonly Color FillHi = Color.FromArgb(0x66, 0x4F, 0xC3, 0xF7);
    private static readonly Color StrokeNormal = Color.FromArgb(0xAA, 0x4F, 0xC3, 0xF7);
    private static readonly Color StrokeHi = Color.FromArgb(0xFF, 0x9A, 0xE0, 0xFF);

    private SnapOverlayWindow()
    {
        InitializeComponent();
        ConfigureWindow();
    }

    // ---------- Public static API (UI thread) ----------

    /// <summary>Show/refresh the per-monitor overlays. Existing ones are reused; any not in this batch are hidden. Clears the previous highlight.</summary>
    public static void ShowForMonitors(IReadOnlyList<MonitorZoneSet> sets)
    {
        var keep = new HashSet<string>();
        foreach (var set in sets)
        {
            keep.Add(set.Monitor.DeviceName);
            if (!_overlays.TryGetValue(set.Monitor.DeviceName, out var win))
            {
                win = new SnapOverlayWindow();
                _overlays[set.Monitor.DeviceName] = win;
            }
            win.Apply(set);
        }

        // Monitors not in this batch -> hide (config/monitors changed).
        foreach (var kv in _overlays)
            if (!keep.Contains(kv.Key))
                kv.Value.AppWindow.Hide();
    }

    /// <summary>Highlight the zone at the given virtual-desktop physical-pixel coordinates (each monitor decides for itself; if outside this monitor, it clears its own highlight).</summary>
    public static void HighlightAt(int virtualX, int virtualY)
    {
        foreach (var win in _overlays.Values)
            win.HighlightLocal(virtualX, virtualY);
    }

    /// <summary>Hide all overlays (session ended). The windows are kept for reuse.</summary>
    public static void HideAll()
    {
        foreach (var win in _overlays.Values)
        {
            win._highlighted = -1;
            win.AppWindow.Hide();
        }
    }

    /// <summary>Fully destroy all overlays (on Stop).</summary>
    public static void CloseAll()
    {
        foreach (var win in _overlays.Values)
        {
            try { win.Close(); } catch { /* ignore */ }
        }
        _overlays.Clear();
    }

    // ---------- Instance ----------

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }

        // Click-through + no-activate + excluded from Alt-Tab. Append extended styles after the
        // AppWindow is created (OverlappedPresenter does not expose these bits itself).
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        long ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED |
              NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
    }

    private void Apply(MonitorZoneSet set)
    {
        _monitor = set.Monitor;
        _rects = set.Rects;
        _names = set.Names;
        _highlighted = -1;

        // Cover the whole monitor (physical pixels). Position before showing to avoid flashing at the old spot.
        AppWindow.MoveAndResize(new RectInt32(_monitor.X, _monitor.Y, _monitor.Width, _monitor.Height));
        AppWindow.Show(activateWindow: false);

        Redraw();
    }

    // Virtual coordinates -> this monitor's zone index; redraw only when it changes.
    private void HighlightLocal(int vx, int vy)
    {
        int localX = vx - _monitor.X;
        int localY = vy - _monitor.Y;

        int hit = -1;
        for (int i = 0; i < _rects.Count; i++)
        {
            var r = _rects[i];
            if (localX >= r.X && localX < r.X + r.Width &&
                localY >= r.Y && localY < r.Y + r.Height)
            {
                hit = i;
                break;
            }
        }

        if (hit == _highlighted) return;
        _highlighted = hit;
        Redraw();
    }

    // Redraw the whole Canvas. There are few zones (layout-level), so rebuilding wholesale is simple and reliable.
    private void Redraw()
    {
        ZoneCanvas.Children.Clear();

        // DIP = physical pixels / scale. The window covers the whole monitor and the Canvas is the same size as the window.
        double scale = ZoneCanvas.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;

        for (int i = 0; i < _rects.Count; i++)
        {
            var r = _rects[i];
            bool hi = i == _highlighted;

            var rect = new Rectangle
            {
                Width = r.Width / scale,
                Height = r.Height / scale,
                RadiusX = 6,
                RadiusY = 6,
                Fill = new SolidColorBrush(hi ? FillHi : FillNormal),
                Stroke = new SolidColorBrush(hi ? StrokeHi : StrokeNormal),
                StrokeThickness = hi ? 4 : 2,
            };
            Canvas.SetLeft(rect, r.X / scale);
            Canvas.SetTop(rect, r.Y / scale);
            ZoneCanvas.Children.Add(rect);

            string name = i < _names.Count ? _names[i] : "";
            if (!string.IsNullOrEmpty(name))
            {
                var label = new TextBlock
                {
                    Text = name,
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(hi ? Colors.White : Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
                    IsHitTestVisible = false,
                };
                label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, r.X / scale + (r.Width / scale - label.DesiredSize.Width) / 2);
                Canvas.SetTop(label, r.Y / scale + (r.Height / scale - label.DesiredSize.Height) / 2);
                ZoneCanvas.Children.Add(label);
            }
        }
    }
}
