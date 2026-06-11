using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Reframe.Core;
using Reframe.Interop;
using Reframe.Services;

namespace Reframe.UI.Controls;

/// <summary>
/// Live monitor mini-map: draws every display as a rounded rectangle at its relative virtual-desktop
/// position/scale, overlaid with
/// (1) relevant zones (an enabled profile's Zone rule that matches this monitor's resolution) as
///     semi-transparent dashed boxes, and
/// (2) taken windows as solid live-rect blocks.
/// Pure code drawing; the page drives it by calling <see cref="Refresh"/> on a timer.
/// Coordinates: monitors and windows are both virtual-desktop physical pixels; we first compute the
/// union bounding box of all displays, scale it to <b>fill the control width</b>, and apply the same
/// WorldToCanvas transform to monitors, zones, and windows so all three stay aligned.
/// <para>
/// Height auto-fit (modeled on LayoutEditorPage.RecomputeCanvasSize): the control fills the available
/// width and its <b>height follows the content</b> = width / bounding-box aspect (then clamped to
/// <see cref="MaxHeight"/>). This removes the large letterbox margins around ultra-wide (e.g. 32:9)
/// desktops — otherwise users mistake the control edge for the screen edge and think a window snapped
/// to the far left is "floating in the middle".
/// </para>
/// </summary>
public sealed class LiveMonitorMap : ContentControl
{
    private readonly Canvas _canvas = new();
    private readonly Border _frame;

    // The last snapshot passed to Refresh, reused to redraw on SizeChanged.
    private IReadOnlyList<MonitorDesc> _monitors = Array.Empty<MonitorDesc>();
    private IReadOnlyList<(IntPtr Handle, string ProfileId)> _taken =
        Array.Empty<(IntPtr, string)>();
    private AppConfig? _cfg;

    // Height auto-fit: padding + the height most recently computed from the bounding box (cached to
    // avoid repeatedly setting Height and thrashing layout).
    private const double Pad = 6;          // canvas padding (DIP) so the border isn't clipped at the edge
    private double _appliedHeight = -1;    // the last Height we wrote, for debouncing

    public LiveMonitorMap()
    {
        _frame = new Border
        {
            // Control background (the "non-desktop" area outside the virtual desktop): darker and
            // emptier than the monitor faces, which — together with the monitors' clear borders and
            // rounded corners — lets users tell the control edge from a screen edge at a glance.
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0x0A, 0x0C, 0x10)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = _canvas,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Content = _frame;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        // Height is driven by content (bounding-box aspect) but clamped to a sane range: ultra-wide
        // desktops shouldn't get too flat, stacked-portrait desktops shouldn't get too tall.
        MinHeight = 90;
        MaxHeight = 320;

        _canvas.SizeChanged += (_, _) => Render();
    }

    /// <summary>Called by the page on a timer: redraw after passing the latest monitor/taken-window
    /// snapshot and config. Lightweight — just reads snapshots.</summary>
    public void Refresh(IReadOnlyList<MonitorDesc> monitors,
        IReadOnlyList<(IntPtr Handle, string ProfileId)> taken, AppConfig cfg)
    {
        _monitors = monitors;
        _taken = taken;
        _cfg = cfg;
        Render();
    }

    /// <summary>
    /// From the bounding-box aspect and a given content width, compute the target control height
    /// (= content height + vertical padding + border), clamp to [MinHeight, MaxHeight], and only set
    /// Height when it differs meaningfully from the last value (debounce to avoid a
    /// set -> relayout -> Render -> set loop / jitter).
    /// </summary>
    private void ApplyHeightFor(double worldW, double worldH, double contentWidth)
    {
        if (contentWidth <= 0) return;

        double availW = Math.Max(1, contentWidth - Pad * 2);
        double drawH = availW * worldH / worldW;        // content height once width is filled
        double border = _frame.BorderThickness.Top + _frame.BorderThickness.Bottom;
        double target = drawH + Pad * 2 + border;        // add vertical padding and border
        target = Math.Clamp(target, MinHeight, MaxHeight);

        if (Math.Abs(target - _appliedHeight) < 0.5) return; // already at this height, don't re-set
        _appliedHeight = target;
        Height = target;
    }

    private void Render()
    {
        var mons = _monitors;
        if (mons.Count == 0) return;

        // 1) Union bounding box of the virtual desktop (physical pixels, may include negative coords).
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var m in mons)
        {
            minX = Math.Min(minX, m.X);
            minY = Math.Min(minY, m.Y);
            maxX = Math.Max(maxX, m.X + m.Width);
            maxY = Math.Max(maxY, m.Y + m.Height);
        }
        double worldW = Math.Max(1, maxX - minX);
        double worldH = Math.Max(1, maxY - minY);

        // 2) Height follows content: fill the available width, height = content height + vertical
        //    padding, then clamp to [MinHeight, MaxHeight].
        //    Available width comes from the measured canvas width (the control stretches with its
        //    parent); if the canvas has no width yet, skip this round — setting Height triggers a
        //    relayout and the canvas SizeChanged re-runs Render (with a width by then).
        double cw = _canvas.ActualWidth;
        if (cw <= 0)
        {
            // Control width isn't measured yet: estimate a height from the overall control width to
            // force a layout pass, then redraw precisely later.
            ApplyHeightFor(worldW, worldH, ActualWidth);
            return;
        }

        double availW = Math.Max(1, cw - Pad * 2);
        ApplyHeightFor(worldW, worldH, cw);

        _canvas.Children.Clear();

        double ch = _canvas.ActualHeight;
        if (ch <= 0) return; // height not in effect yet, wait for the next round (SizeChanged from set Height)
        double availH = Math.Max(1, ch - Pad * 2);

        // Fill width first; if the height exceeds the available height (an over-tall desktop clamped by
        // MaxHeight), fall back to the height constraint to avoid overflow.
        double scale = availW / worldW;
        if (worldH * scale > availH) scale = availH / worldH;

        double drawW = worldW * scale, drawH = worldH * scale;
        // Horizontal: when width-filled, ox ≈ Pad; when height-constrained, center. Vertical: center on
        // the actual height (≈ Pad when normally filled).
        double ox = (cw - drawW) / 2, oy = (ch - drawH) / 2;

        // World (virtual-desktop physical pixels) -> canvas DIP.
        double WX(double x) => ox + (x - minX) * scale;
        double WY(double y) => oy + (y - minY) * scale;

        // 3) Draw each display.
        for (int mi = 0; mi < mons.Count; mi++)
        {
            var m = mons[mi];
            double left = WX(m.X), top = WY(m.Y);
            double w = Math.Max(1, m.Width * scale), h = Math.Max(1, m.Height * scale);

            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                // The monitor face is clearly brighter than the "non-desktop" background, with a crisp
                // border and rounded corners, so the desktop area reads at a glance.
                Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x5A, 0x66, 0x78)),
                Stroke = new SolidColorBrush(m.IsPrimary
                    ? Color.FromArgb(0xFF, 0x9E, 0xC8, 0xFF)
                    : Color.FromArgb(0xB0, 0xC8, 0xD0, 0xDC)),
                StrokeThickness = m.IsPrimary ? 1.8 : 1.2,
                RadiusX = 4,
                RadiusY = 4,
            };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, top);
            _canvas.Children.Add(rect);

            // Resolution label (top-left corner, omitted when there isn't room).
            if (w > 56 && h > 22)
            {
                var resLabel = new TextBlock
                {
                    Text = $"{m.Width}×{m.Height}" + (m.IsPrimary ? Loc.T("DashboardPage/PrimaryMonitorSuffix") : ""),
                    FontSize = 11,
                    Opacity = 0.75,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                };
                Canvas.SetLeft(resLabel, left + 4);
                Canvas.SetTop(resLabel, top + 3);
                _canvas.Children.Add(resLabel);
            }

            // 4) Relevant zones: enabled profiles whose rules target this monitor's resolution with
            //    Kind=Zone are drawn as dashed boxes.
            DrawRelevantZones(m, WX, WY, scale);
        }

        // 5) Taken windows (above all displays, as solid blocks).
        DrawTakenWindows(WX, WY, scale);
    }

    /// <summary>For this monitor's resolution, draw every Zone matched by an enabled profile (deduped per
    /// zone) as a semi-transparent dashed box + its name.</summary>
    private void DrawRelevantZones(MonitorDesc m,
        Func<double, double> WX, Func<double, double> WY, double scale)
    {
        var cfg = _cfg;
        if (cfg is null) return;

        // Dedupe by (layoutId, zoneId); the palette index is stable in order of appearance.
        var seen = new HashSet<string>();
        int colorIdx = 0;

        foreach (var p in cfg.Profiles)
        {
            if (!p.Enabled) continue;
            foreach (var r in p.Rules)
            {
                if (r.Kind != PlacementKind.Zone) continue;
                if (!r.Monitor.Matches(m.Width, m.Height)) continue;
                if (r.LayoutId is null || r.ZoneId is null) continue;

                string key = r.LayoutId + "/" + r.ZoneId;
                if (!seen.Add(key)) continue;

                var layout = cfg.Layouts.FirstOrDefault(l => l.Id == r.LayoutId);
                var zone = layout?.Zones.FirstOrDefault(z => z.Id == r.ZoneId);
                if (zone is null) continue;

                // Zone ratios are relative to this monitor's rcMonitor / rcWork. The basis matches the
                // engine (UseWorkArea).
                int bx = r.UseWorkArea ? m.WorkX : m.X;
                int by = r.UseWorkArea ? m.WorkY : m.Y;
                int bw = r.UseWorkArea ? m.WorkW : m.Width;
                int bh = r.UseWorkArea ? m.WorkH : m.Height;

                double zx = bx + zone.X * bw;
                double zy = by + zone.Y * bh;
                double zw = Math.Max(1, zone.W * bw * scale);
                double zh = Math.Max(1, zone.H * bh * scale);

                var box = new Rectangle
                {
                    Width = zw,
                    Height = zh,
                    Fill = new SolidColorBrush(ZoneColors.Fill(colorIdx, 0x22)),
                    Stroke = new SolidColorBrush(ZoneColors.Stroke(colorIdx)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    RadiusX = 2,
                    RadiusY = 2,
                };
                Canvas.SetLeft(box, WX(zx));
                Canvas.SetTop(box, WY(zy));
                _canvas.Children.Add(box);

                if (!string.IsNullOrEmpty(zone.Name) && zw > 30 && zh > 16)
                {
                    var lbl = new TextBlock
                    {
                        Text = zone.Name,
                        FontSize = 10,
                        Opacity = 0.85,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = zw - 4,
                    };
                    lbl.Measure(new Size(zw, zh));
                    Canvas.SetLeft(lbl, WX(zx) + (zw - lbl.DesiredSize.Width) / 2);
                    Canvas.SetTop(lbl, WY(zy) + 2);
                    _canvas.Children.Add(lbl);
                }

                colorIdx++;
            }
        }
    }

    /// <summary>Taken windows: live GetWindowRect -> the same transform -> a solid semi-transparent block
    /// + the profile name.</summary>
    private void DrawTakenWindows(Func<double, double> WX, Func<double, double> WY, double scale)
    {
        var cfg = _cfg;
        if (cfg is null) return;

        // Give each profile name a stable color in order of appearance; profileId -> color index.
        var profileColor = new Dictionary<string, int>();
        int next = 0;

        foreach (var t in _taken)
        {
            if (!NativeMethods.GetWindowRect(t.Handle, out var rc)) continue;

            double left = WX(rc.Left), top = WY(rc.Top);
            double w = Math.Max(1, (rc.Right - rc.Left) * scale);
            double h = Math.Max(1, (rc.Bottom - rc.Top) * scale);

            if (!profileColor.TryGetValue(t.ProfileId, out int ci))
            {
                ci = next++;
                profileColor[t.ProfileId] = ci;
            }

            var block = new Rectangle
            {
                Width = w,
                Height = h,
                Fill = new SolidColorBrush(ZoneColors.Fill(ci, 0x99)),
                Stroke = new SolidColorBrush(ZoneColors.Base(ci)),
                StrokeThickness = 1.5,
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, top);
            _canvas.Children.Add(block);

            string name = cfg.Profiles.FirstOrDefault(p => p.Id == t.ProfileId)?.Name
                ?? Loc.T("DashboardPage/UnknownProfile");
            if (w > 36 && h > 16)
            {
                var lbl = new TextBlock
                {
                    Text = name,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = w - 4,
                };
                lbl.Measure(new Size(w, h));
                Canvas.SetLeft(lbl, left + (w - lbl.DesiredSize.Width) / 2);
                Canvas.SetTop(lbl, top + (h - lbl.DesiredSize.Height) / 2);
                _canvas.Children.Add(lbl);
            }
        }
    }
}
