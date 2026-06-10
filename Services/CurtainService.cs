using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Reframe.Interop;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Reframe.Services;

/// <summary>
/// 专注模式幕布:开启后把"被接管窗口(或前台窗口)以外"的所有屏幕区域遮暗,
/// 夜间打游戏时游戏区外的浏览器/桌面不刺眼。点击穿透——遮暗区域仍可正常操作,只是视觉变暗。
/// <para>
/// 每块显示器一个幕布窗口(无边框 / 置顶 / 点击穿透,姿势复用 <see cref="UI.SnapOverlayWindow"/>)。
/// 窗口内容用 Path + GeometryGroup(EvenOdd):外圈整屏矩形,内圈挖掉本屏上每个被接管窗口的实时矩形,
/// 形成"洞"。开启期间 1s DispatcherTimer 重算洞,跟随窗口移动 / 接管变化;关闭即停。
/// </para>
/// <para>
/// 全部状态仅在 UI 线程访问。<see cref="Toggle"/>/<see cref="Off"/> 由 UI 线程调用
/// (托盘 / 热键线程经 DispatcherQueue 切回 UI 线程后调,满足契约)。
/// </para>
/// </summary>
public static class CurtainService
{
    // 设备名 → 该屏幕的幕布窗口。仅 UI 线程访问。
    private static readonly Dictionary<string, CurtainWindow> _windows = new();
    private static DispatcherTimer? _timer;
    private static Action? _changedHandler;

    /// <summary>幕布是否开启。</summary>
    public static bool IsOn { get; private set; }

    /// <summary>切换幕布开关(UI 线程调用)。</summary>
    public static void Toggle()
    {
        if (IsOn) Off();
        else On();
    }

    private static void On()
    {
        if (IsOn) return;
        IsOn = true;

        // 配置变化(不透明度被滑块改 / 外部改 config.json)时,开着的话立即刷新填充透明度与几何。
        _changedHandler = () =>
        {
            var ui = App.Main?.DispatcherQueue;
            if (ui is null) { if (IsOn) Refresh(); return; }
            ui.TryEnqueue(() => { if (IsOn) Refresh(); });
        };
        ConfigService.Instance.Changed += _changedHandler;

        // 跟随:1s 重算洞(窗口移动 / 接管变化跟着走)。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    /// <summary>关闭幕布。幂等;应用退出路径会调。</summary>
    public static void Off()
    {
        if (!IsOn)
        {
            // 幂等:即便从未开启也确保无残留窗口 / 订阅。
            CloseAllWindows();
            return;
        }
        IsOn = false;

        if (_timer is not null)
        {
            _timer.Stop();
            _timer = null;
        }
        if (_changedHandler is not null)
        {
            ConfigService.Instance.Changed -= _changedHandler;
            _changedHandler = null;
        }

        CloseAllWindows();
    }

    private static void CloseAllWindows()
    {
        foreach (var win in _windows.Values)
        {
            try { win.Close(); } catch { /* ignore */ }
        }
        _windows.Clear();
    }

    /// <summary>
    /// 按当前显示器布局 + 被接管窗口快照重建每屏幕的幕布:
    /// 未涉及的屏幕关掉,涉及的复用并重画几何。
    /// </summary>
    private static void Refresh()
    {
        if (!IsOn) return;

        double opacity = ClampOpacity(ConfigService.Instance.Config.CurtainOpacity);
        var monitors = MonitorService.GetMonitors();

        // 被接管窗口的实时矩形(虚拟桌面物理像素)。没有接管窗口时退回前台窗口矩形。
        var holes = CollectHoleRects();

        var keep = new HashSet<string>();
        foreach (var mon in monitors)
        {
            keep.Add(mon.DeviceName);
            if (!_windows.TryGetValue(mon.DeviceName, out var win))
            {
                win = new CurtainWindow();
                _windows[mon.DeviceName] = win;
            }
            win.Apply(mon, holes, opacity);
        }

        // 本批未涉及的屏幕(热插拔 / 分辨率变)→ 关闭并移除。
        var stale = new List<string>();
        foreach (var kv in _windows)
            if (!keep.Contains(kv.Key))
                stale.Add(kv.Key);
        foreach (var name in stale)
        {
            try { _windows[name].Close(); } catch { /* ignore */ }
            _windows.Remove(name);
        }
    }

    /// <summary>
    /// 收集要挖的洞矩形(虚拟桌面物理像素)。优先用被接管窗口的实时矩形;
    /// 一个都没有时退回前台窗口矩形(若有效)。已最小化 / 无效的窗口跳过。
    /// </summary>
    private static List<NativeMethods.RECT> CollectHoleRects()
    {
        var rects = new List<NativeMethods.RECT>();

        var taken = App.Engine?.GetTakenWindows();
        if (taken is not null)
        {
            foreach (var t in taken)
            {
                if (TryGetVisibleRect(t.Handle, out var r))
                    rects.Add(r);
            }
        }

        if (rects.Count == 0)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && TryGetVisibleRect(fg, out var r))
                rects.Add(r);
        }

        return rects;
    }

    /// <summary>取窗口矩形;窗口无效 / 不可见 / 最小化 / 空矩形则返回 false(不挖洞)。</summary>
    private static bool TryGetVisibleRect(IntPtr hwnd, out NativeMethods.RECT rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero) return false;
        if (!NativeMethods.IsWindow(hwnd)) return false;
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;
        if (NativeMethods.IsIconic(hwnd)) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out rect)) return false;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;
        return true;
    }

    private static double ClampOpacity(double v)
    {
        if (double.IsNaN(v)) return 0.7;
        if (v < 0.0) return 0.0;
        if (v > 1.0) return 1.0;
        return v;
    }

    /// <summary>
    /// 单块显示器的幕布窗口(代码构建,无 XAML):无边框 / 置顶 / 点击穿透。
    /// 内容为一个填满全屏的黑色 Path,用 EvenOdd 规则挖掉本屏上的窗口洞。
    /// </summary>
    private sealed class CurtainWindow : Window
    {
        private readonly Grid _root;
        private readonly Microsoft.UI.Xaml.Shapes.Path _path;
        private readonly SolidColorBrush _fill = new(Color.FromArgb(0xFF, 0, 0, 0));

        public CurtainWindow()
        {
            _path = new Microsoft.UI.Xaml.Shapes.Path
            {
                Fill = _fill,
                IsHitTestVisible = false,
            };
            _root = new Grid
            {
                Background = new SolidColorBrush(Colors.Transparent),
                Children = { _path },
            };
            Content = _root;
            ConfigureWindow();
        }

        // 点击穿透 + 不激活 + 不进 Alt-Tab；置顶、无边框。姿势与 SnapOverlayWindow 一致。
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

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            long ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED |
                  NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
        }

        // 当前几何缓存,Apply 时据此重画(透明度也存,跟随刷新时一并应用)。
        private MonitorDesc _monitor = null!;
        private IReadOnlyList<NativeMethods.RECT> _holes = Array.Empty<NativeMethods.RECT>();
        private double _opacity = 0.7;

        /// <summary>定位整块屏(物理像素)、设置遮暗透明度、用全屏减去窗口洞重画 Path。</summary>
        public void Apply(MonitorDesc monitor, IReadOnlyList<NativeMethods.RECT> holes, double opacity)
        {
            _monitor = monitor;
            _holes = holes;
            _opacity = opacity;

            // 覆盖整块屏(物理像素)。先定位再显示,避免闪到旧位置;不激活。
            AppWindow.MoveAndResize(new RectInt32(monitor.X, monitor.Y, monitor.Width, monitor.Height));
            AppWindow.Show(activateWindow: false);

            Redraw();
        }

        private void Redraw()
        {
            _fill.Opacity = _opacity;

            // DIP = 物理像素 / scale。窗口覆盖整屏,Path 与窗口同尺寸,坐标系用 DIP。
            double scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;

            double wDip = _monitor.Width / scale;
            double hDip = _monitor.Height / scale;

            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };

            // 外圈:整屏矩形(DIP)。
            group.Children.Add(new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, wDip, hDip),
            });

            // 内圈:每个落在本屏的窗口洞。虚拟桌面物理像素 → 本屏左上为原点的物理像素 → DIP。
            // 与本屏相交才挖;裁剪到屏内,避免跨屏窗口在本屏画出越界洞。
            foreach (var r in _holes)
            {
                int interLeft = Math.Max(r.Left, _monitor.X);
                int interTop = Math.Max(r.Top, _monitor.Y);
                int interRight = Math.Min(r.Right, _monitor.X + _monitor.Width);
                int interBottom = Math.Min(r.Bottom, _monitor.Y + _monitor.Height);
                if (interRight <= interLeft || interBottom <= interTop) continue; // 不在本屏

                double localX = (interLeft - _monitor.X) / scale;
                double localY = (interTop - _monitor.Y) / scale;
                double localW = (interRight - interLeft) / scale;
                double localH = (interBottom - interTop) / scale;

                group.Children.Add(new RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(localX, localY, localW, localH),
                });
            }

            _path.Data = group;
        }
    }
}
