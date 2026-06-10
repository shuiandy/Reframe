using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reframe.Interop;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Reframe.Services;

/// <summary>
/// 专注模式幕布:开启后把"被接管窗口(或前台窗口)以外"的所有屏幕区域遮暗,
/// 夜间打游戏时游戏区外的浏览器/桌面不刺眼。点击穿透——遮暗区域仍可正常操作,只是视觉变暗。
/// <para>
/// 实现(方案 A,边栏铺暗):不在整屏窗口上"挖洞"(WinUI 窗口背景默认不透明,挖洞露白底)。
/// 而是把"整块屏 减去 洞矩形"切成若干互不重叠的暗矩形,每个暗矩形放一个半透明黑窗口。
/// 洞那块根本不放窗口 → 真实内容原样透出;暗区是半透明黑 → 透出"变暗的桌面"。
/// 无洞的屏整块铺一个暗窗口;有多个洞时按矩形差集铺,洞之间不会重复变暗、也不会盖到任何洞。
/// </para>
/// <para>
/// 每个暗矩形一个窗口:无边框 / 置顶 / 点击穿透(姿势复用 <see cref="UI.SnapOverlayWindow"/>)。
/// 半透明黑由 WinUI 内容刷的 alpha 合成到桌面(与 RegionPicker 的 #80000000 同机制,实测可靠)。
/// 开启期间 1s DispatcherTimer 重算洞与铺暗,跟随窗口移动 / 接管变化;关闭即停、清干净所有窗口。
/// </para>
/// <para>
/// 行为:有被接管窗口 → 洞 = 这些窗口的实时矩形(可多个,跟随移动)。
/// 无被接管窗口 → 洞 = <b>开启那一刻</b>的前台窗口,并冻结该句柄,直到关闭(不每秒追前台乱跳)。
/// </para>
/// <para>
/// 全部状态仅在 UI 线程访问。<see cref="Toggle"/>/<see cref="Off"/> 由 UI 线程调用
/// (托盘 / 热键线程经 DispatcherQueue 切回 UI 线程后调,满足契约)。
/// </para>
/// </summary>
public static class CurtainService
{
    // 当前在用的暗矩形窗口池。仅 UI 线程访问。开启期间按需增减、复用。
    private static readonly List<CurtainPanel> _panels = new();
    private static DispatcherTimer? _timer;
    private static Action? _changedHandler;

    // 开启那一刻冻结的前台窗口句柄(仅当无被接管窗口时作洞用)。Off 时清零。
    private static IntPtr _frozenForeground = IntPtr.Zero;

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

        // 冻结开启那一刻的前台窗口(无被接管窗口时作洞)。此后不再每秒追前台,避免洞乱跳。
        _frozenForeground = NativeMethods.GetForegroundWindow();

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
            CloseAllPanels();
            return;
        }
        IsOn = false;
        _frozenForeground = IntPtr.Zero;

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

        CloseAllPanels();
    }

    private static void CloseAllPanels()
    {
        foreach (var p in _panels)
        {
            try { p.Close(); } catch { /* ignore */ }
        }
        _panels.Clear();
    }

    /// <summary>
    /// 按当前显示器布局 + 被接管窗口快照重建铺暗:对每块屏,用"整屏 减去 洞"切出若干暗矩形,
    /// 每个暗矩形要一个半透明黑窗口。复用现有窗口池(多退少补),把每块暗矩形定位/上色。
    /// </summary>
    private static void Refresh()
    {
        if (!IsOn) return;

        double opacity = ClampOpacity(ConfigService.Instance.Config.CurtainOpacity);
        var monitors = MonitorService.GetMonitors();

        // 被接管窗口的实时矩形(虚拟桌面物理像素)。没有接管窗口时退回"冻结的前台窗口"矩形。
        var holes = CollectHoleRects();

        // 为每块屏算出暗矩形(虚拟桌面物理像素),汇总成一份总清单。
        var darkRects = new List<NativeMethods.RECT>();
        foreach (var mon in monitors)
        {
            var monRect = new NativeMethods.RECT
            {
                Left = mon.X, Top = mon.Y, Right = mon.X + mon.Width, Bottom = mon.Y + mon.Height,
            };
            // 该屏减去所有洞 → 互不重叠的暗矩形集合。
            SubtractHoles(monRect, holes, darkRects);
        }

        // 多退少补:复用窗口池,把第 i 个暗矩形定位/上色;多出的窗口关掉。
        for (int i = 0; i < darkRects.Count; i++)
        {
            CurtainPanel panel;
            if (i < _panels.Count)
            {
                panel = _panels[i];
            }
            else
            {
                panel = new CurtainPanel();
                _panels.Add(panel);
            }
            panel.Apply(darkRects[i], opacity);
        }
        for (int i = _panels.Count - 1; i >= darkRects.Count; i--)
        {
            try { _panels[i].Close(); } catch { /* ignore */ }
            _panels.RemoveAt(i);
        }
    }

    /// <summary>
    /// 把 <paramref name="area"/>(单块屏矩形)减去所有与之相交的洞,结果切成若干互不重叠的矩形,
    /// 追加到 <paramref name="output"/>。算法:维护一个"当前剩余矩形"列表,逐个洞去切每块剩余矩形
    /// (上/下/左/右四条带,左右带按洞的纵向范围裁剪,保证不重叠)。无洞相交则整块屏即一块暗区。
    /// </summary>
    private static void SubtractHoles(NativeMethods.RECT area,
        IReadOnlyList<NativeMethods.RECT> holes, List<NativeMethods.RECT> output)
    {
        var remaining = new List<NativeMethods.RECT> { area };

        foreach (var hole in holes)
        {
            // 洞与本屏的相交矩形;不相交则跳过(不影响本屏)。
            int hl = Math.Max(hole.Left, area.Left);
            int ht = Math.Max(hole.Top, area.Top);
            int hr = Math.Min(hole.Right, area.Right);
            int hb = Math.Min(hole.Bottom, area.Bottom);
            if (hr <= hl || hb <= ht) continue;

            var clippedHole = new NativeMethods.RECT { Left = hl, Top = ht, Right = hr, Bottom = hb };

            var next = new List<NativeMethods.RECT>(remaining.Count + 4);
            foreach (var r in remaining)
                SubtractOne(r, clippedHole, next);
            remaining = next;
        }

        output.AddRange(remaining);
    }

    /// <summary>
    /// 从矩形 <paramref name="r"/> 减去洞 <paramref name="h"/>(h 已裁到 r 所在屏内),
    /// 把剩余部分切成至多 4 块不重叠矩形追加到 <paramref name="output"/>。无相交则原样保留 r。
    /// 切法:上带(整宽) / 下带(整宽) / 左带(洞纵向范围内) / 右带(洞纵向范围内)。
    /// </summary>
    private static void SubtractOne(NativeMethods.RECT r, NativeMethods.RECT h, List<NativeMethods.RECT> output)
    {
        // r 与 h 的相交;不相交则 r 整块保留。
        int il = Math.Max(r.Left, h.Left);
        int it = Math.Max(r.Top, h.Top);
        int ir = Math.Min(r.Right, h.Right);
        int ib = Math.Min(r.Bottom, h.Bottom);
        if (ir <= il || ib <= it)
        {
            output.Add(r);
            return;
        }

        // 上带:r.Top .. it(整宽)
        if (it > r.Top)
            output.Add(new NativeMethods.RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = it });
        // 下带:ib .. r.Bottom(整宽)
        if (ib < r.Bottom)
            output.Add(new NativeMethods.RECT { Left = r.Left, Top = ib, Right = r.Right, Bottom = r.Bottom });
        // 左带:r.Left .. il(限定在洞纵向范围 it..ib,避免与上下带重叠)
        if (il > r.Left)
            output.Add(new NativeMethods.RECT { Left = r.Left, Top = it, Right = il, Bottom = ib });
        // 右带:ir .. r.Right(同上,限定纵向)
        if (ir < r.Right)
            output.Add(new NativeMethods.RECT { Left = ir, Top = it, Right = r.Right, Bottom = ib });
    }

    /// <summary>
    /// 收集要避开的洞矩形(虚拟桌面物理像素)。优先用被接管窗口的实时矩形;
    /// 一个都没有时退回"开启那一刻冻结的前台窗口"矩形(若仍有效)。已最小化 / 无效的窗口跳过。
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

        // 无被接管窗口:用冻结的前台窗口(开启那一刻定下,不追前台)。
        if (rects.Count == 0 && _frozenForeground != IntPtr.Zero)
        {
            if (TryGetVisibleRect(_frozenForeground, out var r))
                rects.Add(r);
        }

        return rects;
    }

    /// <summary>取窗口矩形;窗口无效 / 不可见 / 最小化 / 空矩形则返回 false(不作洞)。</summary>
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
    /// 一块暗矩形的覆盖窗口(代码构建,无 XAML):无边框 / 置顶 / 点击穿透。
    /// 内容是整窗填满的半透明黑(无透明区,故不会露 WinUI 白底)。透明度跟随刷新更新。
    /// </summary>
    private sealed class CurtainPanel : Window
    {
        private readonly Grid _root;
        private readonly SolidColorBrush _fill = new(Color.FromArgb(0xFF, 0, 0, 0));

        public CurtainPanel()
        {
            _root = new Grid { Background = _fill };
            Content = _root;
            ConfigureWindow();
        }

        // 点击穿透 + 不激活 + 不进 Alt-Tab;置顶、无边框。姿势与 SnapOverlayWindow 一致。
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

        /// <summary>把窗口定位到给定暗矩形(虚拟桌面物理像素),并设遮暗透明度。</summary>
        public void Apply(NativeMethods.RECT rect, double opacity)
        {
            _fill.Opacity = opacity;

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0)
            {
                // 退化矩形:藏起来(不应发生,SubtractOne 已保证正向尺寸,防御性处理)。
                AppWindow.Hide();
                return;
            }

            // 先定位再显示,避免闪到旧位置;不激活。坐标为物理像素(进程 PerMonitorV2)。
            AppWindow.MoveAndResize(new RectInt32(rect.Left, rect.Top, w, h));
            AppWindow.Show(activateWindow: false);
        }
    }
}
