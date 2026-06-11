using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Reframe.Core;
using Layout = Reframe.Core.Layout;
using Reframe.Services;
using Reframe.UI.Controls;
using Windows.UI;

namespace Reframe.UI;

public sealed partial class LayoutEditorPage : Page
{
    // ---- Canvas sizing: fill the host's available width, height from the Ref aspect ratio; if too
    //      tall, fall back to the available height instead, always centered ----
    private const double SnapPx = 8;            // snap threshold (canvas DIP)
    private const double FallbackCanvasWidth = 900;  // fallback width before the host has been measured

    private string? _layoutId;
    private Layout _work = new();               // working copy; written back to the real config only on save
    private double _canvasW = FallbackCanvasWidth;
    private double _canvasH = FallbackCanvasWidth * 9 / 16;

    private readonly List<ZoneVisual> _visuals = new();
    private Zone? _selected;
    private bool _suppressSync;                 // suppress the property panel write-back and UI from re-triggering each other

    public LayoutEditorPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _layoutId = e.Parameter as string;

        var real = ConfigService.Instance.Config.Layouts.FirstOrDefault(l => l.Id == _layoutId);
        if (real is null)
        {
            // Defensive: if not found, go back.
            if (Frame.CanGoBack) Frame.GoBack();
            return;
        }

        _work = Clone(real);
        InitMonitorCombo();

        _suppressSync = true;
        NameBox.Text = _work.Name;
        RefWidthBox.Value = _work.RefWidth;
        RefHeightBox.Value = _work.RefHeight;
        _suppressSync = false;

        RecomputeCanvasSize();
        RebuildCanvas();
        SelectZone(null);

        KeyDown += OnPageKeyDown;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Symmetric unsubscribe: OnNavigatedTo does += OnPageKeyDown every time. If NavigationCacheMode
        // is ever enabled and this page instance is reused, failing to unsubscribe would stack the same
        // handler, so a single Delete keypress would remove several zones.
        KeyDown -= OnPageKeyDown;
    }

    // ---------- Clone ----------
    private static Layout Clone(Layout src) => new()
    {
        Id = src.Id,
        Name = src.Name,
        RefWidth = src.RefWidth,
        RefHeight = src.RefHeight,
        Zones = src.Zones
            .Select(z => new Zone { Id = z.Id, Name = z.Name, X = z.X, Y = z.Y, W = z.W, H = z.H })
            .ToList()
    };

    // ---------- Reference resolution ----------
    // Trailing "custom" item that echoes the current resolution when Ref matches no monitor.
    private ComboBoxItem? _customItem;

    private void InitMonitorCombo()
    {
        _suppressSync = true;
        MonitorCombo.Items.Clear();
        _customItem = null;
        try
        {
            foreach (var m in MonitorService.GetMonitors())
            {
                string label = Loc.T("LayoutEditorPage/MonitorItemFormat", m.Width, m.Height);
                if (m.IsPrimary) label += Loc.T("LayoutEditorPage/MonitorPrimarySuffix");
                MonitorCombo.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    Tag = new int[] { m.Width, m.Height }
                });
            }
        }
        catch { /* stay silent if the service is not ready yet */ }
        _suppressSync = false;

        // Echo the current Ref on entry: select the matching monitor, or append a "custom" item.
        SyncMonitorCombo();
    }

    // Make the ComboBox show _work's current reference resolution: if it matches a monitor, select that
    // item; otherwise maintain a "WxH (custom)" item and select it, so it never sits on the placeholder.
    private void SyncMonitorCombo()
    {
        int w = _work.RefWidth, h = _work.RefHeight;
        _suppressSync = true;

        ComboBoxItem? match = null;
        foreach (var obj in MonitorCombo.Items)
        {
            if (obj is ComboBoxItem { Tag: int[] wh } item && wh.Length == 2 && wh[0] == w && wh[1] == h)
            {
                match = item;
                break;
            }
        }

        if (match is not null)
        {
            // Monitor matched: remove any leftover custom item and select the match.
            if (_customItem is not null)
            {
                MonitorCombo.Items.Remove(_customItem);
                _customItem = null;
            }
            MonitorCombo.SelectedItem = match;
        }
        else
        {
            // No match: reuse or create the trailing custom item, echoing the current resolution.
            string label = Loc.T("LayoutEditorPage/MonitorCustomFormat", w, h);
            if (_customItem is null)
            {
                _customItem = new ComboBoxItem { Content = label, Tag = new int[] { w, h } };
                MonitorCombo.Items.Add(_customItem);
            }
            else
            {
                _customItem.Content = label;
                _customItem.Tag = new int[] { w, h };
            }
            MonitorCombo.SelectedItem = _customItem;
        }

        _suppressSync = false;
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSync) return;
        if (MonitorCombo.SelectedItem is ComboBoxItem { Tag: int[] wh } && wh.Length == 2)
        {
            _suppressSync = true;
            RefWidthBox.Value = wh[0];
            RefHeightBox.Value = wh[1];
            _suppressSync = false;
            ApplyRefSize(wh[0], wh[1]);
        }
    }

    private void RefSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSync) return;
        int w = (int)(double.IsNaN(RefWidthBox.Value) ? _work.RefWidth : RefWidthBox.Value);
        int h = (int)(double.IsNaN(RefHeightBox.Value) ? _work.RefHeight : RefHeightBox.Value);
        if (w < 1 || h < 1) return;
        ApplyRefSize(w, h);
    }

    private void ApplyRefSize(int w, int h)
    {
        _work.RefWidth = w;
        _work.RefHeight = h;
        RecomputeCanvasSize();
        RebuildCanvas();
        SyncPropPanel();        // pixel display tracks the Ref change
        SyncMonitorCombo();     // reference-resolution echo tracks the Ref change (typed in / matched monitor)
    }

    // When the host resizes (window stretch; the property panel width is fixed), recompute the canvas
    // and re-lay-out the zones.
    private void CanvasHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecomputeCanvasSize();
        LayoutVisuals();
    }

    // The canvas fills the host's available width, height from the reference aspect ratio; if the height
    // exceeds the available height, constrain by height instead and narrow the width accordingly.
    private void RecomputeCanvasSize()
    {
        double aspect = (double)_work.RefWidth / _work.RefHeight;

        // CanvasHost.Padding = 16 (all sides), so subtract 16 on each axis from the available area.
        double padW = CanvasHost.Padding.Left + CanvasHost.Padding.Right;
        double padH = CanvasHost.Padding.Top + CanvasHost.Padding.Bottom;
        double availW = CanvasHost.ActualWidth - padW;
        double availH = CanvasHost.ActualHeight - padH;

        // Use the fallback width before the host has been measured; SizeChanged will re-trigger an exact layout later.
        double w = availW > 1 ? availW : FallbackCanvasWidth;
        double h = w / aspect;

        // If the height overflows the available height (e.g. 16:9 or portrait layouts), constrain by
        // height instead to avoid the canvas overflowing into scroll.
        if (availH > 1 && h > availH)
        {
            h = availH;
            w = h * aspect;
        }

        _canvasW = w;
        _canvasH = h;
        CanvasFrame.Width = _canvasW;
        CanvasFrame.Height = _canvasH;
        ZoneCanvas.Width = _canvasW;
        ZoneCanvas.Height = _canvasH;
        GuideCanvas.Width = _canvasW;
        GuideCanvas.Height = _canvasH;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSync) return;
        _work.Name = NameBox.Text;
    }

    // ---------- Preset templates ----------
    // Preset zone names are localized at generation time and written into user data as-is; existing
    // data is never re-translated (see docs/dev/I18N.md section 8).
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;
        string left = Loc.T("LayoutEditorPage/ZonePresetLeft");
        string right = Loc.T("LayoutEditorPage/ZonePresetRight");
        string center = Loc.T("LayoutEditorPage/ZonePresetCenter");
        string game = Loc.T("LayoutEditorPage/ZonePresetGame");
        string secondary = Loc.T("LayoutEditorPage/ZonePresetSecondary");
        _work.Zones = tag switch
        {
            "half" => new()
            {
                new Zone { Name = left,  X = 0,   Y = 0, W = 0.5, H = 1 },
                new Zone { Name = right, X = 0.5, Y = 0, W = 0.5, H = 1 },
            },
            "thirds" => new()
            {
                new Zone { Name = left,   X = 0,       Y = 0, W = 1.0/3, H = 1 },
                new Zone { Name = center, X = 1.0/3,   Y = 0, W = 1.0/3, H = 1 },
                new Zone { Name = right,  X = 2.0/3,   Y = 0, W = 1.0/3, H = 1 },
            },
            "twoThirds" => new()
            {
                new Zone { Name = game,      X = 0,     Y = 0, W = 2.0/3, H = 1 },
                new Zone { Name = secondary, X = 2.0/3, Y = 0, W = 1.0/3, H = 1 },
            },
            "center169" => Centered(16.0 / 9),
            "left219"   => LeftAligned(21.0 / 9),
            _ => _work.Zones
        };
        RebuildCanvas();
        SelectZone(null);
    }

    // 16:9 centered: letterbox a 16:9 region inside the Ref area, centered, named "Game".
    private List<Zone> Centered(double targetAspect)
    {
        double refAspect = (double)_work.RefWidth / _work.RefHeight;
        double w, h;
        if (targetAspect >= refAspect) { w = 1; h = refAspect / targetAspect; }
        else { h = 1; w = targetAspect / refAspect; }
        return new()
        {
            new Zone { Name = Loc.T("LayoutEditorPage/ZonePresetGame"), X = (1 - w) / 2, Y = (1 - h) / 2, W = w, H = h }
        };
    }

    // 21:9 left: same ratio but flush left and vertically centered, leaving the right side as
    // "Secondary" (if there is any horizontal slack).
    private List<Zone> LeftAligned(double targetAspect)
    {
        double refAspect = (double)_work.RefWidth / _work.RefHeight;
        double w, h;
        if (targetAspect >= refAspect) { w = 1; h = refAspect / targetAspect; }
        else { h = 1; w = targetAspect / refAspect; }

        var zones = new List<Zone>
        {
            new Zone { Name = Loc.T("LayoutEditorPage/ZonePresetGame"), X = 0, Y = (1 - h) / 2, W = w, H = h }
        };
        if (w < 0.999)
            zones.Add(new Zone { Name = Loc.T("LayoutEditorPage/ZonePresetSecondary"), X = w, Y = 0, W = 1 - w, H = 1 });
        return zones;
    }

    private void AddZone_Click(object sender, RoutedEventArgs e)
    {
        var z = new Zone
        {
            Name = Loc.T("LayoutEditorPage/ZoneNameNew", _work.Zones.Count + 1),
            X = 0.3, Y = 0.3, W = 0.4, H = 0.4
        };
        _work.Zones.Add(z);
        RebuildCanvas();
        SelectZone(z);
    }

    // ---------- Canvas rebuild ----------
    private void RebuildCanvas()
    {
        // Keep GuideCanvas, clear the rest of the zone visuals.
        var keep = GuideCanvas;
        ZoneCanvas.Children.Clear();
        GuideCanvas.Children.Clear();
        GuideCanvas.Width = _canvasW;
        GuideCanvas.Height = _canvasH;
        _visuals.Clear();

        for (int i = 0; i < _work.Zones.Count; i++)
        {
            var vis = new ZoneVisual(this, _work.Zones[i], i);
            _visuals.Add(vis);
            vis.AddTo(ZoneCanvas);
        }

        // Put the guide layer on top; adding it last also guarantees its z-order is above the zones.
        ZoneCanvas.Children.Add(keep);
        Canvas.SetZIndex(keep, 100);

        LayoutVisuals();
        HighlightSelection();
    }

    // Render each zone's ratio coordinates to canvas pixels.
    private void LayoutVisuals()
    {
        foreach (var v in _visuals) v.Layout(_canvasW, _canvasH);
    }

    // ---------- Selection ----------
    internal void SelectZone(Zone? z)
    {
        _selected = z;
        HighlightSelection();
        SyncPropPanel();
    }

    private void HighlightSelection()
    {
        foreach (var v in _visuals) v.SetSelected(v.Zone == _selected);
    }

    private void SyncPropPanel()
    {
        if (_selected is null)
        {
            PropPanel.Visibility = Visibility.Collapsed;
            NoSelHint.Visibility = Visibility.Visible;
            return;
        }
        PropPanel.Visibility = Visibility.Visible;
        NoSelHint.Visibility = Visibility.Collapsed;

        _suppressSync = true;
        ZoneNameBox.Text = _selected.Name;
        ZoneXBox.Value = Math.Round(_selected.X * _work.RefWidth);
        ZoneYBox.Value = Math.Round(_selected.Y * _work.RefHeight);
        ZoneWBox.Value = Math.Round(_selected.W * _work.RefWidth);
        ZoneHBox.Value = Math.Round(_selected.H * _work.RefHeight);
        ZoneRatioText.Text = FormatRatio(_selected);
        _suppressSync = false;
    }

    // Property-panel ratio readout: the four 0..1 ratios at 3 decimals.
    private static string FormatRatio(Zone z) => Loc.T(
        "LayoutEditorPage/ZoneRatioFormat",
        z.X.ToString("0.###"), z.Y.ToString("0.###"),
        z.W.ToString("0.###"), z.H.ToString("0.###"));

    private void ZoneNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSync || _selected is null) return;
        _selected.Name = ZoneNameBox.Text;
        VisualFor(_selected)?.UpdateLabels(_canvasW, _canvasH);
    }

    // Property-panel pixels -> ratio write-back. Rounding only happens for display; here we convert back to 0..1.
    private void ZonePx_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSync || _selected is null) return;

        double px(NumberBox b, double fallback) => double.IsNaN(b.Value) ? fallback : b.Value;
        double x = px(ZoneXBox, _selected.X * _work.RefWidth);
        double y = px(ZoneYBox, _selected.Y * _work.RefHeight);
        double w = Math.Max(1, px(ZoneWBox, _selected.W * _work.RefWidth));
        double h = Math.Max(1, px(ZoneHBox, _selected.H * _work.RefHeight));

        // Clamp within the Ref range.
        x = Math.Clamp(x, 0, _work.RefWidth - 1);
        y = Math.Clamp(y, 0, _work.RefHeight - 1);
        w = Math.Min(w, _work.RefWidth - x);
        h = Math.Min(h, _work.RefHeight - y);

        _selected.X = x / _work.RefWidth;
        _selected.Y = y / _work.RefHeight;
        _selected.W = w / _work.RefWidth;
        _selected.H = h / _work.RefHeight;

        VisualFor(_selected)?.Layout(_canvasW, _canvasH);
        _suppressSync = true;
        ZoneRatioText.Text = FormatRatio(_selected);
        _suppressSync = false;
    }

    private void DeleteZone_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _work.Zones.Remove(_selected);
        RebuildCanvas();
        SelectZone(null);
    }

    private ZoneVisual? VisualFor(Zone z) => _visuals.FirstOrDefault(v => v.Zone == z);

    // ---------- After a drag/resize: write the ratios back + refresh the panel ----------
    // Called by ZoneVisual during and at the end of an interaction.
    internal void OnZoneGeometryChanged(Zone z)
    {
        if (z == _selected) SyncPropPanel();
    }

    // ---------- Snapping: given a candidate value in canvas px, return the snapped value; edges = the
    //            same-axis edges of the other zones ----------
    // dimMax = the canvas size on that axis (_canvasW or _canvasH).
    internal double Snap(double value, double dimMax, IEnumerable<double> otherEdges, out double? guideLine)
    {
        guideLine = null;
        double best = value;
        double bestDist = SnapPx;

        // Division-line candidates: 0, 1/3, 1/2, 2/3, 1
        double[] fractions = { 0, 1.0 / 3, 0.5, 2.0 / 3, 1.0 };
        foreach (double f in fractions)
        {
            double target = f * dimMax;
            double d = Math.Abs(value - target);
            if (d < bestDist) { bestDist = d; best = target; guideLine = target; }
        }
        foreach (double target in otherEdges)
        {
            double d = Math.Abs(value - target);
            if (d < bestDist) { bestDist = d; best = target; guideLine = target; }
        }
        return best;
    }

    // Collect every zone's edges on one axis except self (canvas px). horizontal=true takes the X-axis edges (left/right).
    internal List<double> OtherEdges(Zone self, bool horizontal)
    {
        var list = new List<double>();
        foreach (var z in _work.Zones)
        {
            if (z == self) continue;
            if (horizontal)
            {
                list.Add(z.X * _canvasW);
                list.Add((z.X + z.W) * _canvasW);
            }
            else
            {
                list.Add(z.Y * _canvasH);
                list.Add((z.Y + z.H) * _canvasH);
            }
        }
        return list;
    }

    internal double CanvasW => _canvasW;
    internal double CanvasH => _canvasH;
    internal int RefWidthOf() => _work.RefWidth;
    internal int RefHeightOf() => _work.RefHeight;

    // ---------- Alignment guides ----------
    internal void ShowGuides(double? vx, double? hy)
    {
        GuideCanvas.Children.Clear();
        if (vx is double x)
            GuideCanvas.Children.Add(MakeGuide(x, 0, x, _canvasH));
        if (hy is double y)
            GuideCanvas.Children.Add(MakeGuide(0, y, _canvasW, y));
    }

    internal void ClearGuides() => GuideCanvas.Children.Clear();

    private static Line MakeGuide(double x1, double y1, double x2, double y2) => new()
    {
        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
        Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x4F, 0xC3, 0xF7)),
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 4, 3 },
    };

    // ---------- Keyboard ----------
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && _selected is not null)
        {
            DeleteZone_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            SelectZone(null);
            e.Handled = true;
        }
    }

    // ---------- Save / Cancel ----------
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var real = ConfigService.Instance.Config.Layouts.FirstOrDefault(l => l.Id == _layoutId);
        if (real is not null)
        {
            real.Name = _work.Name;
            real.RefWidth = _work.RefWidth;
            real.RefHeight = _work.RefHeight;
            // Reusing existing Zone object identity does not matter (LayoutId/ZoneId are strings), so
            // replace the whole collection from the working copy.
            real.Zones = _work.Zones
                .Select(z => new Zone { Id = z.Id, Name = z.Name, X = z.X, Y = z.Y, W = z.W, H = z.H })
                .ToList();
            ConfigService.Instance.Save();
        }
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }
}
