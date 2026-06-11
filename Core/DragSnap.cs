using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Reframe.Interop;
using Reframe.Services;
using Reframe.UI;
using Windows.Graphics;

namespace Reframe.Core;

/// <summary>
/// FancyZones-style drag snapping: hold Shift and drag any window → the zone overlay lights up → on release,
/// the window snaps into the zone under the cursor.
///
/// <para>Thread model (mirrors <see cref="WinEventHook"/>'s approach):</para>
/// <list type="bullet">
/// <item>A dedicated background thread installs the two OUT_OF_CONTEXT MOVESIZESTART/END hooks and runs a
///       message pump; Stop uses PostThreadMessage(WM_QUIT) to exit the pump, then unhooks.</item>
/// <item>The hook thread only raises events, reads cursor/key state, computes geometry and calls SetWindowPos
///       (all thread-agnostic Win32); every overlay (WinUI window) operation is marshaled back to the UI
///       thread via <see cref="_ui"/>.</item>
/// <item>During the drag, a 100ms thread-pool Timer polls the cursor to tell the overlay what to highlight;
///       releasing Shift mid-drag exits snapping.</item>
/// </list>
///
/// <para>Zone source: take all zones of <c>Config.Layouts[0]</c> and project them by ratio onto each
/// monitor's work area (snapping always uses rcWork, same logic as PlacementResolver). TODO: multi-layout
/// support (currently fixed to the first).</para>
/// </summary>
public static class DragSnapService
{
    private static Func<AppConfig>? _getConfig;
    private static DispatcherQueue? _ui;

    // The delegate is kept in a field: the OUT_OF_CONTEXT callback is invoked cross-thread, and a local would be GC'd (same pitfall as WinEventHook).
    private static NativeMethods.WinEventProc? _proc;
    private static Thread? _thread;
    // volatile: written by the hook thread, read by Start/Stop; timeout cleanup and re-entry must read the latest value.
    private static volatile uint _threadId;
    private static IntPtr _hook;
    // Static, reused across Start/Stop: Stop() must Reset it, otherwise a second Start's Wait would return immediately (seeing the previous round's Set).
    private static readonly ManualResetEventSlim _ready = new(false);

    private static System.Threading.Timer? _pollTimer;
    private static readonly object _gate = new();

    // ---- Snap-session state (protected only by _gate) ----
    private static bool _snapping;
    private static IntPtr _targetHwnd;
    // The virtual-desktop physical rects + names of each monitor's zones, for MOVESIZEEND hit-testing and positioning.
    private static List<SessionZone> _sessionZones = new();

    private sealed record SessionZone(NativeMethods.RECT VirtualRect);

    public static void Start(Func<AppConfig> getConfig)
    {
        _getConfig = getConfig;
        // Called on the UI thread (the App.xaml.cs integration point), which gives us the queue to marshal overlay operations back to.
        _ui = DispatcherQueue.GetForCurrentThread();

        if (_thread != null) return;
        _proc = OnWinEvent;
        _ready.Reset(); // Reused static event: clear the previous round's Set before each start, so Wait really waits for this round's readiness.
        var t = new Thread(ThreadProc) { IsBackground = true, Name = "Reframe.DragSnap" };
        _thread = t;
        t.Start();

        if (!_ready.Wait(2000))
        {
            // Timeout: make a best effort to collect the thread and clear state (same path as Stop's thread cleanup); don't leave a zombie.
            uint tid = _threadId;
            if (tid != 0)
                NativeMethods.PostThreadMessage(tid, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            try { t.Join(1000); } catch { /* ignore */ }
            _thread = null;
            _threadId = 0;
            _hook = IntPtr.Zero;
        }
    }

    public static void Stop()
    {
        // Idempotent: no thread → just return.
        var t = _thread;
        if (t != null)
        {
            if (_threadId != 0)
                NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            try { t.Join(2000); } catch { /* ignore */ }
            _thread = null;
        }

        // Zero out all reused static state to keep Stop→Start re-entrant (otherwise _ready keeps a stale Set, _threadId/_hook keep stale values).
        _ready.Reset();
        _threadId = 0;
        _hook = IntPtr.Zero;

        StopPoll();
        lock (_gate) { _snapping = false; _targetHwnd = IntPtr.Zero; _sessionZones = new(); }

        // The overlay is destroyed on the UI thread.
        _ui?.TryEnqueue(SnapOverlayWindow.CloseAll);
    }

    private static void ThreadProc()
    {
        _threadId = NativeMethods.GetCurrentThreadId();

        // One hook covers the contiguous MOVESIZESTART (0x000A)..MOVESIZEEND (0x000B) range.
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

    // ---- Drag start: decide whether to enter snap mode ----
    private static void OnMoveSizeStart(IntPtr hwnd)
    {
        var cfg = _getConfig?.Invoke();
        if (cfg is null || !cfg.DragSnapEnabled) return;
        if (!ShiftDown()) return;

        var zones = BuildSessionZones(cfg, out var sets);
        if (zones.Count == 0) return; // No usable layout/zones, don't enter

        lock (_gate)
        {
            _snapping = true;
            _targetHwnd = hwnd;
            _sessionZones = zones;
        }

        _ui?.TryEnqueue(() => SnapOverlayWindow.ShowForMonitors(sets));
        StartPoll();
    }

    // ---- Drag end: snap if there's a hit ----
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

        // Find the zone under the cursor (virtual coordinates).
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

    // Plain snap: first restore from maximized/minimized, then SetWindowPos it over. No border strip, no
    // snapshot, no topmost (unrelated to engine takeover; purely manual placement).
    private static void SnapWindowTo(IntPtr hwnd, NativeMethods.RECT r)
    {
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    // ---- Poll the cursor → highlight ----
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

        // Shift released mid-drag → exit snapping, hide the overlay (the system keeps dragging the window; we just stop snapping).
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

    // ---- Geometry: project Layouts[0]'s zones onto each monitor's work area ----
    // Returns the list of virtual rects for the session; also produces the per-monitor sets the overlay draws (out sets).
    private static List<SessionZone> BuildSessionZones(AppConfig cfg, out List<MonitorZoneSet> sets)
    {
        sets = new List<MonitorZoneSet>();
        var session = new List<SessionZone>();

        // TODO: multi-layout support. Currently fixed to the first layout (simple and predictable).
        var layout = cfg.Layouts.Count > 0 ? cfg.Layouts[0] : null;
        if (layout is null || layout.Zones.Count == 0) return session;

        foreach (var mon in MonitorService.GetMonitors())
        {
            // Snapping always uses the work area (rcWork); build the basis rect and hand it to
            // PlacementResolver.ZoneToRect, so the rounding matches engine takeover (ResolveRect's Zone branch) exactly.
            var basis = new NativeMethods.RECT
            {
                Left = mon.WorkX, Top = mon.WorkY,
                Right = mon.WorkX + mon.WorkW, Bottom = mon.WorkY + mon.WorkH
            };

            var localRects = new List<RectInt32>();
            var names = new List<string>();

            foreach (var z in layout.Zones)
            {
                var r = PlacementResolver.ZoneToRect(z, basis);

                session.Add(new SessionZone(r));

                // The overlay wants physical pixels "relative to that monitor's top-left" (rcMonitor origin).
                localRects.Add(new RectInt32(r.Left - mon.X, r.Top - mon.Y, r.Right - r.Left, r.Bottom - r.Top));
                names.Add(z.Name);
            }

            sets.Add(new MonitorZoneSet(mon, localRects, names));
        }

        return session;
    }
}
