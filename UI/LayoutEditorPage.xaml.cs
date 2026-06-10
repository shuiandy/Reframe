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
    // ---- 画布尺寸:用满容器可用宽度,高按 Ref 宽高比;过高时再按可用高度回退,始终居中 ----
    private const double SnapPx = 8;            // 吸附阈值(画布 DIP)
    private const double FallbackCanvasWidth = 900;  // 容器尚未测量出宽度时的兜底宽

    private string? _layoutId;
    private Layout _work = new();               // 工作副本,保存时才写回真实配置
    private double _canvasW = FallbackCanvasWidth;
    private double _canvasH = FallbackCanvasWidth * 9 / 16;

    private readonly List<ZoneVisual> _visuals = new();
    private Zone? _selected;
    private bool _suppressSync;                 // 抑制属性面板回写与 UI 互相触发

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
            // 容错:找不到就回退。
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
        // 对称退订:OnNavigatedTo 里每次都 += OnPageKeyDown。若将来开启 NavigationCacheMode
        // 复用本页实例,不退订会令同一处理器叠加注册,Delete 键一次删多个分区。
        KeyDown -= OnPageKeyDown;
    }

    // ---------- 克隆 ----------
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

    // ---------- 参考分辨率 ----------
    // 末位"自定义"项,当 Ref 不匹配任何显示器时回显当前分辨率。
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
                MonitorCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{m.Width}×{m.Height}{(m.IsPrimary ? " (主)" : "")}",
                    Tag = new int[] { m.Width, m.Height }
                });
            }
        }
        catch { /* 服务尚未就绪时静默 */ }
        _suppressSync = false;

        // 进入时回显当前 Ref:匹配显示器则选中,否则补一项"自定义"。
        SyncMonitorCombo();
    }

    // 让 ComboBox 显示当前 _work 的参考分辨率:命中某显示器则选中该项,
    // 否则维护一个"WxH(自定义)"项并选中,避免停留在占位符。
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
            // 命中显示器:移除可能残留的自定义项,选中匹配项。
            if (_customItem is not null)
            {
                MonitorCombo.Items.Remove(_customItem);
                _customItem = null;
            }
            MonitorCombo.SelectedItem = match;
        }
        else
        {
            // 未命中:复用或新建末位自定义项,回显当前分辨率。
            string label = $"{w}×{h}(自定义)";
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
        SyncPropPanel();        // 像素显示随 Ref 变化
        SyncMonitorCombo();     // 参考分辨率回显随 Ref 变化(手输/匹配显示器)
    }

    // 容器尺寸变化时(窗口拉伸、属性面板宽度恒定)重算画布并重新布局 zone。
    private void CanvasHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecomputeCanvasSize();
        LayoutVisuals();
    }

    // 画布占满容器可用宽度,高按参考宽高比;若高度超过容器可用高度则按高度回退、宽度随之收窄。
    private void RecomputeCanvasSize()
    {
        double aspect = (double)_work.RefWidth / _work.RefHeight;

        // CanvasHost.Padding = 16(四周),可用区域要扣掉左右/上下各 16。
        double padW = CanvasHost.Padding.Left + CanvasHost.Padding.Right;
        double padH = CanvasHost.Padding.Top + CanvasHost.Padding.Bottom;
        double availW = CanvasHost.ActualWidth - padW;
        double availH = CanvasHost.ActualHeight - padH;

        // 容器尚未测量出尺寸时用兜底宽,稍后 SizeChanged 会再次触发精确布局。
        double w = availW > 1 ? availW : FallbackCanvasWidth;
        double h = w / aspect;

        // 高度超出可用高度(如 16:9、竖向布局)时改按高度约束,避免画布溢出滚动。
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

    // ---------- 预设模板 ----------
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;
        _work.Zones = tag switch
        {
            "half" => new()
            {
                new Zone { Name = "左区", X = 0,   Y = 0, W = 0.5, H = 1 },
                new Zone { Name = "右区", X = 0.5, Y = 0, W = 0.5, H = 1 },
            },
            "thirds" => new()
            {
                new Zone { Name = "左区", X = 0,       Y = 0, W = 1.0/3, H = 1 },
                new Zone { Name = "中区", X = 1.0/3,   Y = 0, W = 1.0/3, H = 1 },
                new Zone { Name = "右区", X = 2.0/3,   Y = 0, W = 1.0/3, H = 1 },
            },
            "twoThirds" => new()
            {
                new Zone { Name = "游戏区", X = 0,     Y = 0, W = 2.0/3, H = 1 },
                new Zone { Name = "副屏区", X = 2.0/3, Y = 0, W = 1.0/3, H = 1 },
            },
            "center169" => Centered(16.0 / 9),
            "left219"   => LeftAligned(21.0 / 9),
            _ => _work.Zones
        };
        RebuildCanvas();
        SelectZone(null);
    }

    // 16:9 居中:在 Ref 区内 letterbox 出一块 16:9,居中,命名"游戏区"。
    private List<Zone> Centered(double targetAspect)
    {
        double refAspect = (double)_work.RefWidth / _work.RefHeight;
        double w, h;
        if (targetAspect >= refAspect) { w = 1; h = refAspect / targetAspect; }
        else { h = 1; w = targetAspect / refAspect; }
        return new()
        {
            new Zone { Name = "游戏区", X = (1 - w) / 2, Y = (1 - h) / 2, W = w, H = h }
        };
    }

    // 21:9 居左:同样比例,但贴左、垂直居中,余下右侧作"副屏区"(若有横向余量)。
    private List<Zone> LeftAligned(double targetAspect)
    {
        double refAspect = (double)_work.RefWidth / _work.RefHeight;
        double w, h;
        if (targetAspect >= refAspect) { w = 1; h = refAspect / targetAspect; }
        else { h = 1; w = targetAspect / refAspect; }

        var zones = new List<Zone>
        {
            new Zone { Name = "游戏区", X = 0, Y = (1 - h) / 2, W = w, H = h }
        };
        if (w < 0.999)
            zones.Add(new Zone { Name = "副屏区", X = w, Y = 0, W = 1 - w, H = 1 });
        return zones;
    }

    private void AddZone_Click(object sender, RoutedEventArgs e)
    {
        var z = new Zone { Name = $"分区{_work.Zones.Count + 1}", X = 0.3, Y = 0.3, W = 0.4, H = 0.4 };
        _work.Zones.Add(z);
        RebuildCanvas();
        SelectZone(z);
    }

    // ---------- 画布重建 ----------
    private void RebuildCanvas()
    {
        // 保留 GuideCanvas,清掉其余 zone 视觉。
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

        // 参考线层置于最上,且后加保证 z 序在 zone 之上。
        ZoneCanvas.Children.Add(keep);
        Canvas.SetZIndex(keep, 100);

        LayoutVisuals();
        HighlightSelection();
    }

    // 把每个 zone 的比例坐标渲染到画布像素。
    private void LayoutVisuals()
    {
        foreach (var v in _visuals) v.Layout(_canvasW, _canvasH);
    }

    // ---------- 选中 ----------
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
        ZoneRatioText.Text =
            $"比例 X={_selected.X:0.###} Y={_selected.Y:0.###} W={_selected.W:0.###} H={_selected.H:0.###}";
        _suppressSync = false;
    }

    private void ZoneNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSync || _selected is null) return;
        _selected.Name = ZoneNameBox.Text;
        VisualFor(_selected)?.UpdateLabels(_canvasW, _canvasH);
    }

    // 属性面板像素 → 比例回写。取整只发生在显示,这里反算回 0..1。
    private void ZonePx_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSync || _selected is null) return;

        double px(NumberBox b, double fallback) => double.IsNaN(b.Value) ? fallback : b.Value;
        double x = px(ZoneXBox, _selected.X * _work.RefWidth);
        double y = px(ZoneYBox, _selected.Y * _work.RefHeight);
        double w = Math.Max(1, px(ZoneWBox, _selected.W * _work.RefWidth));
        double h = Math.Max(1, px(ZoneHBox, _selected.H * _work.RefHeight));

        // 钳制到 Ref 范围内。
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
        ZoneRatioText.Text =
            $"比例 X={_selected.X:0.###} Y={_selected.Y:0.###} W={_selected.W:0.###} H={_selected.H:0.###}";
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

    // ---------- 拖拽/缩放结束后:写回比例 + 刷新面板 ----------
    // 由 ZoneVisual 在交互结束/进行中调用。
    internal void OnZoneGeometryChanged(Zone z)
    {
        if (z == _selected) SyncPropPanel();
    }

    // ---------- 吸附:给定画布 px 候选值,返回吸附后的值;edges = 其它 zone 的同向边 ----------
    // dimMax = 该轴画布尺寸(_canvasW 或 _canvasH)。
    internal double Snap(double value, double dimMax, IEnumerable<double> otherEdges, out double? guideLine)
    {
        guideLine = null;
        double best = value;
        double bestDist = SnapPx;

        // 等分线候选:0, 1/3, 1/2, 2/3, 1
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

    // 收集除 self 外所有 zone 在某轴上的边(画布 px)。horizontal=true 取 X 向边(左/右)。
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

    // ---------- 对齐参考线 ----------
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

    // ---------- 键盘 ----------
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

    // ---------- 保存 / 取消 ----------
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var real = ConfigService.Instance.Config.Layouts.FirstOrDefault(l => l.Id == _layoutId);
        if (real is not null)
        {
            real.Name = _work.Name;
            real.RefWidth = _work.RefWidth;
            real.RefHeight = _work.RefHeight;
            // 复用现有 Zone 对象身份不重要(LayoutId/ZoneId 是字符串),按工作副本整体替换。
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
