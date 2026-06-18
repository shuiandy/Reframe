using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>
/// Remembers where ordinary top-level windows sit per monitor configuration and puts them back when the
/// display topology changes (resolution / monitor add-remove / streaming VDD return) scrambles them — the
/// PersistentWindows-style capability, P1 scope: in-memory (this session), keyed by window handle.
///
/// <para><b>Single-threaded actor.</b> All state (the <see cref="LayoutMemory"/>, the state machine, the
/// current key) is owned by one worker thread that drains a serial mailbox. The display-change listener,
/// the periodic capture timer, and the settle/grace timers only <i>post</i> messages; nothing touches state
/// off the worker. This is what makes the freeze-during-transition invariant race-free (see DESIGN /
/// docs/window-persistence-plan.md §3.2, §8).</para>
///
/// <para><b>Coordination with the borderless engine:</b> capture and restore skip windows the engine owns
/// (<c>getEngineOwned</c>) and windows it just moved (<see cref="WindowOps.WasRecentlyMutatedByEngine"/>),
/// and defer a restore while the engine is mid-tick (<c>isEngineBusy</c>). Per-window write suppression
/// closes the "_takeover not recorded yet" lag (the suppression flag is set inside WindowOps.Apply, before
/// the takeover map is updated).</para>
/// </summary>
public sealed class PersistenceEngine : IDisposable
{
    /// <summary>Injectable last-resort error sink for the worker / listener threads (App wires it to CrashLog.Write; Core stays Services-free). Static: process-wide diagnostics.</summary>
    public static Action<string, Exception?>? OnThreadError;

    /// <summary>Optional log sink (dashboard). Raised on the worker thread.</summary>
    public event Action<string>? Log;

    // ---- Tunables ----
    private const int CaptureIntervalMs = 2000; // periodic snapshot of the current layout
    private const int SettleMs = 800;           // quiet period after a display change before restoring
    private const int GraceMs = 1000;           // after a restore, keep capture frozen to absorb late scrambling
    private const int EngineBusyRetryMs = 200;  // if the borderless engine is mid-tick at settle, retry shortly
    private const int RestorePasses = 3;        // some windows bounce back; re-apply a few times
    private const int RestorePassDelayMs = 250;

    // ---- Injected collaborators (keep Core free of a Services dependency) ----
    private readonly Func<IReadOnlyList<MonitorDesc>> _getMonitors;
    private readonly Func<ISet<IntPtr>> _getEngineOwned;
    private readonly Func<bool> _isEngineBusy;
    private readonly Func<bool> _isEnabled;

    private readonly LayoutMemory _memory = new();
    private readonly DisplayChangeListener _listener = new();

    // ---- Actor mailbox + worker ----
    private enum Msg { Capture, DisplayChanged, Settle, ResumeCapture }
    private BlockingCollection<Msg> _mailbox = new();
    private Thread? _worker;
    private Timer? _captureTimer;
    private Timer? _settleTimer;
    private Timer? _graceTimer;
    private volatile bool _started;

    // ---- Worker-thread-owned state (no locks: single-thread ownership) ----
    private enum State { Idle, Frozen, Restoring, Settling }
    private State _state = State.Idle;
    private string _currentKey = LayoutKey.None;

    public PersistenceEngine(
        Func<IReadOnlyList<MonitorDesc>> getMonitors,
        Func<ISet<IntPtr>> getEngineOwned,
        Func<bool> isEngineBusy,
        Func<bool> isEnabled)
    {
        _getMonitors = getMonitors;
        _getEngineOwned = getEngineOwned;
        _isEngineBusy = isEngineBusy;
        _isEnabled = isEnabled;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        // A prior Stop() completes the mailbox (terminal); a fresh Start() needs one that accepts posts again.
        if (_mailbox.IsAddingCompleted) _mailbox = new BlockingCollection<Msg>();

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Reframe.Persistence" };
        _worker.Start();

        _listener.DisplayChanged += OnDisplayChangedSignal;
        _listener.Start();

        // Timers fire on the thread pool and only post to the mailbox; all work happens on the worker.
        _settleTimer = new Timer(_ => Post(Msg.Settle), null, Timeout.Infinite, Timeout.Infinite);
        _graceTimer = new Timer(_ => Post(Msg.ResumeCapture), null, Timeout.Infinite, Timeout.Infinite);
        _captureTimer = new Timer(_ => Post(Msg.Capture), null, CaptureIntervalMs, CaptureIntervalMs);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        _listener.DisplayChanged -= OnDisplayChangedSignal;
        try { _listener.Stop(); } catch { /* ignore */ }

        _captureTimer?.Dispose(); _captureTimer = null;
        _settleTimer?.Dispose(); _settleTimer = null;
        _graceTimer?.Dispose(); _graceTimer = null;

        try { _mailbox.CompleteAdding(); } catch { /* already completed */ }
        try { _worker?.Join(3000); } catch { /* ignore */ }
        _worker = null;
    }

    public void Dispose()
    {
        Stop();
        try { _mailbox.Dispose(); } catch { /* ignore */ }
    }

    private void OnDisplayChangedSignal() => Post(Msg.DisplayChanged);

    private void Post(Msg m)
    {
        try { _mailbox.Add(m); }
        catch { /* mailbox completed during shutdown: drop */ }
    }

    private void WorkerLoop()
    {
        try
        {
            // Establish the baseline key so the first periodic capture doesn't read a phantom topology change.
            _currentKey = ComputeKey();

            foreach (var m in _mailbox.GetConsumingEnumerable())
            {
                try { Handle(m); }
                catch (Exception ex) { OnThreadError?.Invoke($"PersistenceEngine handle {m}", ex); }
            }
        }
        catch (Exception ex) { OnThreadError?.Invoke("PersistenceEngine worker", ex); }
    }

    private void Handle(Msg m)
    {
        switch (m)
        {
            case Msg.DisplayChanged: if (_isEnabled()) EnterFreeze(); break;
            case Msg.Capture: OnCapture(); break;
            case Msg.Settle: OnSettle(); break;
            case Msg.ResumeCapture: if (_state == State.Settling) _state = State.Idle; break;
        }
    }

    private void EnterFreeze()
    {
        _state = State.Frozen;
        // (Re)arm the settle timer; re-arming on each display-change event debounces a burst into one restore.
        _settleTimer?.Change(SettleMs, Timeout.Infinite);
    }

    private void OnCapture()
    {
        if (!_isEnabled()) return;
        if (_state != State.Idle) return; // frozen / restoring / settling: never capture (the core invariant)

        string liveKey = ComputeKey();
        if (liveKey != _currentKey)
        {
            // The topology changed and we noticed via capture — either no WM_DISPLAYCHANGE arrived, or a
            // window event raced ahead of it. Freeze now and let the settle path restore; do NOT capture
            // (that would write the half-scrambled layout over the good snapshot). Closes the
            // "WinEvent-before-display-message" race.
            EnterFreeze();
            return;
        }

        _memory.PruneDead(NativeMethods.IsWindow);
        var snap = CaptureEligibleWindows();
        if (snap.Count > 0) _memory.Capture(_currentKey, snap);
    }

    private void OnSettle()
    {
        if (_state != State.Frozen) return; // superseded by a later transition

        // Disabled mid-freeze (user turned persistence off after the display changed): abort without restoring.
        if (!_isEnabled()) { _state = State.Idle; return; }

        // If the borderless engine is mid-tick (it also reacts to the display change), let it finish first so
        // we don't race its re-placement of game windows; retry shortly.
        if (_isEngineBusy()) { _settleTimer?.Change(EngineBusyRetryMs, Timeout.Infinite); return; }

        string newKey = ComputeKey();
        bool topologyChanged = newKey != _currentKey;
        _currentKey = newKey;

        // Only restore when the configuration actually changed AND we have a remembered layout for it. A
        // spurious / same-key WM_DISPLAYCHANGE must not pull windows back over the user's recent moves.
        if (topologyChanged && _memory.HasSnapshot(newKey))
        {
            _state = State.Restoring;
            int restored = DoRestore(newKey);
            Log?.Invoke($"Restored {restored} window(s) for display {newKey}");
            _state = State.Settling;
            _graceTimer?.Change(GraceMs, Timeout.Infinite); // absorb late scrambling, then resume capture
        }
        else
        {
            // Same config, or one we have no memory of yet: just (resume) capturing it.
            _state = State.Idle;
        }
    }

    private int DoRestore(string key)
    {
        _memory.PruneDead(NativeMethods.IsWindow); // fresh liveness before matching handles to records
        int restoredCount = 0;
        for (int pass = 0; pass < RestorePasses; pass++)
        {
            var owned = _getEngineOwned();
            var live = EligibleHandles(owned);
            var plan = _memory.GetRestorePlan(key, live);
            if (plan.Count == 0) break;

            int movedThisPass = 0;
            foreach (var (h, t) in plan)
            {
                if (IsAtTarget(h, t)) continue; // already in place: verify-then-retry-only-misses
                WindowOps.RestorePlacement(h, t.Left, t.Top, t.Right, t.Bottom, t.ShowCmd);
                movedThisPass++;
            }
            if (pass == 0) restoredCount = movedThisPass;
            if (movedThisPass == 0) break; // everything reached its target
            if (pass < RestorePasses - 1) Thread.Sleep(RestorePassDelayMs);
        }
        return restoredCount;
    }

    /// <summary>Whether the window already sits within tolerance of its remembered rect (a maximized record always re-asserts).</summary>
    private static bool IsAtTarget(IntPtr h, WindowRecord t)
    {
        if (t.ShowCmd == NativeMethods.SW_SHOWMAXIMIZED) return false;
        if (!NativeMethods.GetWindowRect(h, out var r)) return false;
        const int tol = 2;
        return Math.Abs(r.Left - t.Left) <= tol && Math.Abs(r.Top - t.Top) <= tol &&
               Math.Abs(r.Right - t.Right) <= tol && Math.Abs(r.Bottom - t.Bottom) <= tol;
    }

    private string ComputeKey() => LayoutKey.Compute(_getMonitors());

    // ---- Window eligibility (shared by capture and restore) ----

    /// <summary>Handles of windows persistence may manage right now: real app windows, not engine-owned, not just engine-moved, not minimized.</summary>
    private List<IntPtr> EligibleHandles(ISet<IntPtr> owned)
    {
        var list = new List<IntPtr>();
        foreach (var w in WindowScanner.EnumerateCandidates())
        {
            var h = w.Handle;
            if (owned.Contains(h)) continue;
            if (WindowOps.WasRecentlyMutatedByEngine(h)) continue;
            if (NativeMethods.IsIconic(h)) continue;
            list.Add(h);
        }
        return list;
    }

    private List<(IntPtr Handle, WindowRecord Record)> CaptureEligibleWindows()
    {
        var owned = _getEngineOwned();
        var result = new List<(IntPtr, WindowRecord)>();
        foreach (var h in EligibleHandles(owned))
        {
            var wp = new NativeMethods.WINDOWPLACEMENT { length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            int showCmd = NativeMethods.GetWindowPlacement(h, ref wp) ? wp.showCmd : NativeMethods.SW_SHOWNORMAL;
            if (showCmd == NativeMethods.SW_SHOWMINIMIZED) continue; // already filtered by IsIconic, belt-and-suspenders
            if (!NativeMethods.GetWindowRect(h, out var r)) continue;
            result.Add((h, new WindowRecord(r.Left, r.Top, r.Right, r.Bottom, showCmd)));
        }
        return result;
    }
}
