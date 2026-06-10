using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Reframe.Interop;
using Reframe.Services;
using Reframe.UI;
using Windows.Graphics;

namespace Reframe.Core;

/// <summary>
/// FancyZones 式拖拽吸附:按住 Shift 拖任意窗口 → 分区覆盖层亮起 → 松手把窗口吸进光标所在分区。
///
/// <para>线程模型(照搬 <see cref="WinEventHook"/> 的手法):</para>
/// <list type="bullet">
/// <item>独占一个后台线程装 MOVESIZESTART/END 两个 OUT_OF_CONTEXT 钩子并跑消息泵;
///       Stop 用 PostThreadMessage(WM_QUIT) 退泵后解钩。</item>
/// <item>钩子线程只发事件、读光标/键状态、算几何、调 SetWindowPos(都是线程无关的 Win32);
///       一切覆盖层(WinUI 窗口)操作都经 <see cref="_ui"/> 切回 UI 线程。</item>
/// <item>拖动期间一个 100ms 线程池 Timer 轮询光标,告诉覆盖层高亮;Shift 中途松开即退出吸附。</item>
/// </list>
///
/// <para>zone 来源:取 <c>Config.Layouts[0]</c> 的全部 zone,按比例投到每块显示器的工作区
/// (吸附一律用 rcWork,与 PlacementResolver 同逻辑)。TODO: 多布局支持(目前固定第一个)。</para>
/// </summary>
public static class DragSnapService
{
    private static Func<AppConfig>? _getConfig;
    private static DispatcherQueue? _ui;

    // 委托保存为字段:OUT_OF_CONTEXT 回调跨线程调用,局部会被 GC(同 WinEventHook 的坑)。
    private static NativeMethods.WinEventProc? _proc;
    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _hook;
    private static readonly ManualResetEventSlim _ready = new(false);

    private static System.Threading.Timer? _pollTimer;
    private static readonly object _gate = new();

    // ---- 吸附会话状态(仅 _gate 保护) ----
    private static bool _snapping;
    private static IntPtr _targetHwnd;
    // 各屏 zone 的虚拟桌面物理矩形 + 名称,供 MOVESIZEEND 命中判定与定位。
    private static List<SessionZone> _sessionZones = new();

    private sealed record SessionZone(NativeMethods.RECT VirtualRect);

    public static void Start(Func<AppConfig> getConfig)
    {
        _getConfig = getConfig;
        // 在 UI 线程调用(App.xaml.cs 集成点),据此拿覆盖层操作要切回的队列。
        _ui = DispatcherQueue.GetForCurrentThread();

        if (_thread != null) return;
        _proc = OnWinEvent;
        _thread = new Thread(ThreadProc) { IsBackground = true, Name = "Reframe.DragSnap" };
        _thread.Start();
        _ready.Wait(2000);
    }

    public static void Stop()
    {
        // 幂等:无线程直接退。
        var t = _thread;
        if (t != null)
        {
            if (_threadId != 0)
                NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            try { t.Join(2000); } catch { /* ignore */ }
            _thread = null;
        }

        StopPoll();
        lock (_gate) { _snapping = false; _targetHwnd = IntPtr.Zero; _sessionZones = new(); }

        // 覆盖层在 UI 线程销毁。
        _ui?.TryEnqueue(SnapOverlayWindow.CloseAll);
    }

    private static void ThreadProc()
    {
        _threadId = NativeMethods.GetCurrentThreadId();

        // MOVESIZESTART(0x000A)..MOVESIZEEND(0x000B)连续区间,一个钩子覆盖。
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MOVESIZESTART, NativeMethods.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _proc!, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        _ready.Set();

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(in msg);
            NativeMethods.DispatchMessage(in msg);
        }

        if (_hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private static void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;

        if (eventType == NativeMethods.EVENT_SYSTEM_MOVESIZESTART)
            OnMoveSizeStart(hwnd);
        else if (eventType == NativeMethods.EVENT_SYSTEM_MOVESIZEEND)
            OnMoveSizeEnd(hwnd);
    }

    // ---- 开始拖拽:决定是否进入吸附模式 ----
    private static void OnMoveSizeStart(IntPtr hwnd)
    {
        var cfg = _getConfig?.Invoke();
        if (cfg is null || !cfg.DragSnapEnabled) return;
        if (!ShiftDown()) return;

        var zones = BuildSessionZones(cfg, out var sets);
        if (zones.Count == 0) return; // 没有可用布局/分区,不进入

        lock (_gate)
        {
            _snapping = true;
            _targetHwnd = hwnd;
            _sessionZones = zones;
        }

        _ui?.TryEnqueue(() => SnapOverlayWindow.ShowForMonitors(sets));
        StartPoll();
    }

    // ---- 结束拖拽:命中则吸附 ----
    private static void OnMoveSizeEnd(IntPtr hwnd)
    {
        bool snapping;
        IntPtr target;
        List<SessionZone> zones;
        lock (_gate)
        {
            snapping = _snapping;
            target = _targetHwnd;
            zones = _sessionZones;
            _snapping = false;
            _targetHwnd = IntPtr.Zero;
        }

        StopPoll();
        _ui?.TryEnqueue(SnapOverlayWindow.HideAll);

        if (!snapping || hwnd != target) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;

        // 命中光标所在 zone(虚拟坐标)。
        foreach (var z in zones)
        {
            var r = z.VirtualRect;
            if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
            {
                SnapWindowTo(hwnd, r);
                break;
            }
        }
    }

    // 普通吸附:从最大化/最小化先还原,再 SetWindowPos 移过去。不去边框、不快照、不置顶
    //(与引擎接管无关,纯手动摆放)。
    private static void SnapWindowTo(IntPtr hwnd, NativeMethods.RECT r)
    {
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    // ---- 轮询光标 → 高亮 ----
    private static void StartPoll()
    {
        StopPoll();
        _pollTimer = new System.Threading.Timer(_ => PollTick(), null, 0, 100);
    }

    private static void StopPoll()
    {
        var t = _pollTimer;
        _pollTimer = null;
        t?.Dispose();
    }

    private static void PollTick()
    {
        lock (_gate) { if (!_snapping) return; }

        // Shift 中途松开 → 退出吸附,隐藏覆盖层(窗口照常被系统继续拖,我们不再吸附)。
        if (!ShiftDown())
        {
            lock (_gate) { _snapping = false; _targetHwnd = IntPtr.Zero; }
            StopPoll();
            _ui?.TryEnqueue(SnapOverlayWindow.HideAll);
            return;
        }

        if (!NativeMethods.GetCursorPos(out var pt)) return;
        int x = pt.X, y = pt.Y;
        _ui?.TryEnqueue(() => SnapOverlayWindow.HighlightAt(x, y));
    }

    private static bool ShiftDown()
        => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;

    // ---- 几何:Layouts[0] 的 zone 投到每块显示器工作区 ----
    // 返回会话用的虚拟矩形列表;同时产出覆盖层要画的 per-monitor 集合(out sets)。
    private static List<SessionZone> BuildSessionZones(AppConfig cfg, out List<MonitorZoneSet> sets)
    {
        sets = new List<MonitorZoneSet>();
        var session = new List<SessionZone>();

        // TODO: 多布局支持。目前固定取第一个布局(简单可预期)。
        var layout = cfg.Layouts.Count > 0 ? cfg.Layouts[0] : null;
        if (layout is null || layout.Zones.Count == 0) return session;

        foreach (var mon in MonitorService.GetMonitors())
        {
            int bw = mon.WorkW, bh = mon.WorkH;          // 吸附一律用工作区(rcWork)
            int baseX = mon.WorkX, baseY = mon.WorkY;

            var localRects = new List<RectInt32>();
            var names = new List<string>();

            foreach (var z in layout.Zones)
            {
                // 同 PlacementResolver:zone 比例 × 工作区。
                int left = baseX + (int)Math.Round(z.X * bw);
                int top = baseY + (int)Math.Round(z.Y * bh);
                int right = baseX + (int)Math.Round((z.X + z.W) * bw);
                int bottom = baseY + (int)Math.Round((z.Y + z.H) * bh);

                session.Add(new SessionZone(new NativeMethods.RECT
                {
                    Left = left, Top = top, Right = right, Bottom = bottom
                }));

                // 覆盖层要的是"相对该屏左上角"(rcMonitor 原点)的物理像素。
                localRects.Add(new RectInt32(left - mon.X, top - mon.Y, right - left, bottom - top));
                names.Add(z.Name);
            }

            sets.Add(new MonitorZoneSet(mon, localRects, names));
        }

        return session;
    }
}
