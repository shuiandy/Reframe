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
/// 实时屏幕小地图:把所有显示器按虚拟桌面相对位置/比例画成圆角矩形,叠加
/// (1) 相关分区(启用 profile 的 Zone 规则命中该屏分辨率)半透明虚线框,
/// (2) 被接管窗口的实时矩形实心色块。纯代码绘制,页面定时调 <see cref="Refresh"/> 驱动。
/// 坐标:显示器/窗口均为虚拟桌面物理像素;先求所有屏的并集包围盒,等比缩放铺满控件宽,
/// 同一 WorldToCanvas 变换同时作用于屏、分区、窗口,保证三者对齐。
/// </summary>
public sealed class LiveMonitorMap : ContentControl
{
    private readonly Canvas _canvas = new();
    private readonly Border _frame;

    // 上次 Refresh 传入的快照,SizeChanged 时用其重绘。
    private IReadOnlyList<MonitorDesc> _monitors = Array.Empty<MonitorDesc>();
    private IReadOnlyList<(IntPtr Handle, string ProfileId)> _taken =
        Array.Empty<(IntPtr, string)>();
    private AppConfig? _cfg;

    public LiveMonitorMap()
    {
        _frame = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.18 },
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
        MinHeight = 160;

        _canvas.SizeChanged += (_, _) => Render();
    }

    /// <summary>页面定时调用:传入最新显示器/接管窗口快照与配置后重绘。轻量,读快照即可。</summary>
    public void Refresh(IReadOnlyList<MonitorDesc> monitors,
        IReadOnlyList<(IntPtr Handle, string ProfileId)> taken, AppConfig cfg)
    {
        _monitors = monitors;
        _taken = taken;
        _cfg = cfg;
        Render();
    }

    private void Render()
    {
        _canvas.Children.Clear();

        double cw = _canvas.ActualWidth, ch = _canvas.ActualHeight;
        var mons = _monitors;
        if (cw <= 0 || ch <= 0 || mons.Count == 0) return;

        // 1) 虚拟桌面并集包围盒(物理像素,可含负坐标)。
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

        // 2) 等比 letterbox 进画布(留 6px 内边距防边框贴边裁切)。
        const double pad = 6;
        double availW = Math.Max(1, cw - pad * 2);
        double availH = Math.Max(1, ch - pad * 2);
        double scale = Math.Min(availW / worldW, availH / worldH);
        double drawW = worldW * scale, drawH = worldH * scale;
        double ox = (cw - drawW) / 2, oy = (ch - drawH) / 2;

        // 世界(虚拟桌面物理像素)→ 画布 DIP。
        double WX(double x) => ox + (x - minX) * scale;
        double WY(double y) => oy + (y - minY) * scale;

        // 3) 画每块显示器。
        for (int mi = 0; mi < mons.Count; mi++)
        {
            var m = mons[mi];
            double left = WX(m.X), top = WY(m.Y);
            double w = Math.Max(1, m.Width * scale), h = Math.Max(1, m.Height * scale);

            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x20, 0x20, 0x20)),
                Stroke = new SolidColorBrush(m.IsPrimary
                    ? Color.FromArgb(0xFF, 0x9E, 0xC8, 0xFF)
                    : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = m.IsPrimary ? 1.5 : 1,
                RadiusX = 4,
                RadiusY = 4,
            };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, top);
            _canvas.Children.Add(rect);

            // 分辨率标注(放左上角,空间不足则省略)。
            if (w > 56 && h > 22)
            {
                var resLabel = new TextBlock
                {
                    Text = $"{m.Width}×{m.Height}" + (m.IsPrimary ? " (主)" : ""),
                    FontSize = 11,
                    Opacity = 0.75,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                };
                Canvas.SetLeft(resLabel, left + 4);
                Canvas.SetTop(resLabel, top + 3);
                _canvas.Children.Add(resLabel);
            }

            // 4) 相关分区:启用 profile 的规则中 Monitor 命中本屏分辨率且 Kind=Zone 的,画虚线框。
            DrawRelevantZones(m, WX, WY, scale);
        }

        // 5) 被接管窗口(在所有屏之上,实心色块)。
        DrawTakenWindows(WX, WY, scale);
    }

    /// <summary>该屏分辨率下,所有启用 profile 命中的 Zone(同一 zone 去重),画半透明虚线框 + 名字。</summary>
    private void DrawRelevantZones(MonitorDesc m,
        Func<double, double> WX, Func<double, double> WY, double scale)
    {
        var cfg = _cfg;
        if (cfg is null) return;

        // 同一 (layoutId, zoneId) 去重;调色板索引按出现顺序稳定。
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

                // Zone 比例相对该屏 rcMonitor / rcWork。基准与引擎一致(UseWorkArea)。
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

    /// <summary>被接管窗口:实时 GetWindowRect → 同一变换映射 → 实心半透明色块 + profile 名。</summary>
    private void DrawTakenWindows(Func<double, double> WX, Func<double, double> WY, double scale)
    {
        var cfg = _cfg;
        if (cfg is null) return;

        // profile 名按出现顺序取一个稳定颜色;profileId → (颜色索引, 名字)。
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

            string name = cfg.Profiles.FirstOrDefault(p => p.Id == t.ProfileId)?.Name ?? "?";
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
