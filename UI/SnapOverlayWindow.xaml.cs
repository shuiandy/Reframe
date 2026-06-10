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
/// 拖拽吸附会话中,某块显示器上要画出的一组分区。
/// <para>Rects 为相对该显示器左上角的物理像素矩形(与 zone 同序),Names 为对应名称。</para>
/// </summary>
public sealed record MonitorZoneSet(MonitorDesc Monitor, IReadOnlyList<RectInt32> Rects, IReadOnlyList<string> Names);

/// <summary>
/// 单块显示器的吸附覆盖层:无边框、置顶、点击穿透(绝不抢拖拽中的鼠标)。
/// 半透明画出各 zone(淡填充 + 边框 + 名称),光标所在 zone 高亮。
/// <para>
/// 生命周期:吸附会话期间每块屏一个,复用(隐藏而非销毁)。所有静态方法必须在 UI 线程调用
/// (DragSnapService 经 DispatcherQueue 切回)。
/// </para>
/// </summary>
public sealed partial class SnapOverlayWindow : Window
{
    // 设备名 → 复用的覆盖层实例。仅 UI 线程访问。
    private static readonly Dictionary<string, SnapOverlayWindow> _overlays = new();

    private MonitorDesc _monitor = null!;   // ShowForMonitors → Apply 必先赋值
    private IReadOnlyList<RectInt32> _rects = System.Array.Empty<RectInt32>();
    private IReadOnlyList<string> _names = System.Array.Empty<string>();
    private int _highlighted = -1;   // 当前高亮的 zone 索引(本屏内),-1 = 无

    // 配色:常态淡填充 / 高亮加深;边框同理。统一冷蓝,避免与游戏内容混淆。
    private static readonly Color FillNormal = Color.FromArgb(0x33, 0x4F, 0xC3, 0xF7);
    private static readonly Color FillHi = Color.FromArgb(0x66, 0x4F, 0xC3, 0xF7);
    private static readonly Color StrokeNormal = Color.FromArgb(0xAA, 0x4F, 0xC3, 0xF7);
    private static readonly Color StrokeHi = Color.FromArgb(0xFF, 0x9A, 0xE0, 0xFF);

    private SnapOverlayWindow()
    {
        InitializeComponent();
        ConfigureWindow();
    }

    // ---------- 公共静态 API(UI 线程) ----------

    /// <summary>显示/刷新各屏覆盖层。已存在的复用,不在本批的隐藏。会清掉之前的高亮。</summary>
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

        // 本批未涉及的屏 → 隐藏(配置/显示器变了)。
        foreach (var kv in _overlays)
            if (!keep.Contains(kv.Key))
                kv.Value.AppWindow.Hide();
    }

    /// <summary>按虚拟桌面物理像素坐标高亮所在 zone(逐屏自行判断;落在本屏外则本屏清高亮)。</summary>
    public static void HighlightAt(int virtualX, int virtualY)
    {
        foreach (var win in _overlays.Values)
            win.HighlightLocal(virtualX, virtualY);
    }

    /// <summary>隐藏全部覆盖层(会话结束)。窗口保留以便复用。</summary>
    public static void HideAll()
    {
        foreach (var win in _overlays.Values)
        {
            win._highlighted = -1;
            win.AppWindow.Hide();
        }
    }

    /// <summary>彻底销毁全部覆盖层(Stop 时)。</summary>
    public static void CloseAll()
    {
        foreach (var win in _overlays.Values)
        {
            try { win.Close(); } catch { /* ignore */ }
        }
        _overlays.Clear();
    }

    // ---------- 实例 ----------

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

        // 点击穿透 + 不激活 + 不进 Alt-Tab。AppWindow 建好后追加扩展样式
        // (OverlappedPresenter 本身不暴露这些位)。
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

        // 覆盖整块屏(物理像素)。先定位再显示,避免闪到旧位置。
        AppWindow.MoveAndResize(new RectInt32(_monitor.X, _monitor.Y, _monitor.Width, _monitor.Height));
        AppWindow.Show(activateWindow: false);

        Redraw();
    }

    // 虚拟坐标 → 本屏 zone 索引;变了才重画。
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

    // 重画整张 Canvas。zone 不多(布局级),整体重建简单可靠。
    private void Redraw()
    {
        ZoneCanvas.Children.Clear();

        // DIP = 物理像素 / scale。窗口覆盖整屏,Canvas 与窗口同尺寸。
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
