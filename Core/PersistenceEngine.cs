using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>
/// Remembers where ordinary top-level windows sit per monitor configuration and puts them back when the
/// display topology changes (resolution / monitor add-remove / streaming VDD return) scrambles them — the
/// PersistentWindows-style capability.
///
/// <para><b>Memory is keyed by window identity (process + class), not by HWND, and survives on disk.</b> A
/// record is bound to a handle only for as long as that handle lives; when the app restarts (or the machine
/// reboots) the record is re-claimed by identity and the window is moved <i>to</i> it (adoption restore —
/// <see cref="RestoreAdopted"/>), rather than the record being overwritten with wherever the app happened to
/// reopen — see <see cref="LayoutMemory"/> / <see cref="WindowMatcher"/>.
/// The layout file is loaded once on the worker thread at startup, written back at most every
/// <see cref="SaveDebounceMs"/> ms while dirty, and flushed on <see cref="Stop"/>.</para>
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
    // Capture interval is user-configurable (WindowPersistenceCaptureSeconds, clamped 1–30s) — see CurrentIntervalMs().
    private const int SettleMs = 800;           // quiet period after a display change before restoring
    private const int EngineBusyRetryMs = 200;  // if the borderless engine is mid-tick at settle, retry shortly
    private const int RestorePasses = 3;        // some windows bounce back; re-apply a few times
    private const int RestorePassDelayMs = 250;

    /// <summary>
    /// After a restore, how long capture stays frozen to absorb late scrambling.
    ///
    /// <para><b>Raised 1000 → 5000 ms.</b> Windows' own re-placement of windows after a monitor wakes up
    /// routinely arrives well over a second late (the display comes back, then a second wave of squeezes
    /// lands as apps react to the final resolution). With a 1 s grace the very next 2 s capture tick would
    /// run while those late squeezes were still landing and would write the <i>squashed</i> geometry back
    /// over the good layout — under the same DisplayKey, i.e. straight over the record it had just restored.
    /// Now that the memory is written to disk, such a poisoned record survives reboots and can no longer be
    /// shaken off by restarting; a few extra seconds of frozen capture is a trivial price. The freeze is
    /// still bounded, and the state machine is unchanged.</para>
    /// </summary>
    private const int GraceMs = 5000;

    /// <summary>
    /// Minimum spacing between layout-file writes. Capture runs every ~2 s; writing on every tick would be
    /// pointless disk noise, so a capture only marks the memory dirty and the flush is debounced to this
    /// interval (plus a forced flush on Stop and on an explicit "Capture now").
    /// </summary>
    private const int SaveDebounceMs = 30_000;

    // ---- Injected collaborators (keep Core free of a Services dependency) ----
    private readonly Func<IReadOnlyList<MonitorDesc>> _getMonitors;
    private readonly Func<ISet<IntPtr>> _getEngineOwned;
    private readonly Func<bool> _isEngineBusy;
    private readonly Func<bool> _isEnabled;
    private readonly Func<int> _getCaptureSeconds;
    private readonly Func<IEnumerable<string>> _getIgnoredProcesses;

    private readonly LayoutMemory _memory = new();
    private readonly DisplayChangeListener _listener = new();

    // ---- Actor mailbox + worker ----
    private enum Msg { Capture, CaptureNow, RestoreNow, DisplayChanged, Settle, ResumeCapture }
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
    private bool _dirty;                              // memory changed since the last successful write
    private DateTime _lastSaveUtc = DateTime.MinValue;
    /// <summary>Set once the startup load has run. Guards Stop() from writing an empty file over a good one if the worker never got that far.</summary>
    private volatile bool _diskLoaded;

    public PersistenceEngine(
        Func<IReadOnlyList<MonitorDesc>> getMonitors,
        Func<ISet<IntPtr>> getEngineOwned,
        Func<bool> isEngineBusy,
        Func<bool> isEnabled,
        Func<int> getCaptureSeconds,
        Func<IEnumerable<string>> getIgnoredProcesses)
    {
        _getMonitors = getMonitors;
        _getEngineOwned = getEngineOwned;
        _isEngineBusy = isEngineBusy;
        _isEnabled = isEnabled;
        _getCaptureSeconds = getCaptureSeconds;
        _getIgnoredProcesses = getIgnoredProcesses;
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
        // One-shot, re-armed after each capture with the current configured interval (so changes take effect live).
        _captureTimer = new Timer(_ => Post(Msg.Capture), null, CurrentIntervalMs(), Timeout.Infinite);
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

        // Final flush. Safe to touch worker-owned state from here: the worker has exited (Join above), so
        // single-thread ownership is not violated — this thread is now the only one left. (If the Join had
        // timed out we could race a capture; the whole call is guarded and the file write is atomic, so the
        // worst case is a skipped save, never a corrupt file.)
        try { SaveNow(); } catch (Exception ex) { OnThreadError?.Invoke("PersistenceEngine final save", ex); }
    }

    public void Dispose()
    {
        Stop();
        try { _mailbox.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>Manually snapshot the current layout as the remembered layout for the current display (Settings "Capture now").</summary>
    public void CaptureNow() => Post(Msg.CaptureNow);

    /// <summary>Manually re-apply the remembered layout for the current display (Settings "Restore now").</summary>
    public void RestoreNow() => Post(Msg.RestoreNow);

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

            // Load the remembered layouts before draining the mailbox. Done here rather than in Start() so the
            // memory stays owned by this one thread (Start() runs on the UI thread), and so a slow/failed disk
            // read can never block app startup — LayoutStore.Load never throws and degrades to empty.
            LoadFromDisk();

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
            case Msg.Capture:
                try { OnCapture(); }
                finally { _captureTimer?.Change(CurrentIntervalMs(), Timeout.Infinite); } // re-arm with the current interval
                break;
            case Msg.CaptureNow: OnCaptureNow(); break;
            case Msg.RestoreNow: OnRestoreNow(); break;
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

    private int CurrentIntervalMs() => Math.Clamp(_getCaptureSeconds(), 1, 30) * 1000;

    /// <summary>
    /// Manual capture: snapshot the current layout under the current key, bypassing the Idle guard (explicit
    /// user intent). <b>The opposite of the periodic path on adoption</b>: "Capture now" means "remember where
    /// things are <i>right now</i>", so current geometry wins even for a record that was just re-claimed
    /// (<c>adoptGeometry: false</c>) and nothing is restored as a side effect of the user asking to save.
    /// </summary>
    private void OnCaptureNow()
    {
        if (!_isEnabled()) return;
        string key = ComputeKey();
        if (key == LayoutKey.None) return;
        _currentKey = key;
        _memory.PruneDead(NativeMethods.IsWindow);
        var snap = CaptureEligibleWindows();
        if (snap.Count > 0) { _memory.Capture(key, snap, adoptGeometry: false); _dirty = true; }
        Log?.Invoke($"Captured {snap.Count} window(s) for display {key}");
        SaveNow(); // explicit user action, and rare: skip the debounce so the result is on disk immediately
    }

    /// <summary>Manual restore: re-apply the remembered layout for the current key on demand (e.g. Windows scrambled without an event).</summary>
    private void OnRestoreNow()
    {
        if (!_isEnabled()) return;
        string key = ComputeKey();
        _currentKey = key;
        if (!_memory.HasSnapshot(key)) { Log?.Invoke("No remembered layout for the current display"); return; }
        _state = State.Restoring;
        int n = DoRestore(key);
        Log?.Invoke($"Restored {n} window(s) for display {key}");
        _state = State.Settling;
        _graceTimer?.Change(GraceMs, Timeout.Infinite);
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
        if (snap.Count > 0)
        {
            // adoptGeometry (default): a record claimed by a restarted app keeps its remembered geometry and
            // comes back here as a pending adoption restore instead of being overwritten by the window's
            // current (usually wrong) position.
            var adopted = _memory.Capture(_currentKey, snap);
            _dirty = true;
            if (adopted.Count > 0) RestoreAdopted(adopted);
        }
        MaybeSave();
    }

    /// <summary>
    /// Put windows that just re-claimed a remembered record back where that record says — the "an app
    /// restarted, catch it" path. Reaching here already implies the engine is enabled and
    /// <see cref="State.Idle"/> (both are checked at the top of <see cref="OnCapture"/>, the only caller, and
    /// nothing yields in between), so the freeze invariant still holds.
    ///
    /// <para><b>One attempt per binding.</b> Unlike <see cref="DoRestore"/> this is a single pass with no
    /// retries: the pending list is produced only at the moment a record goes from unbound to bound, and from
    /// the next capture round on that window is tracked normally. A window that springs back to its own idea
    /// of a position therefore wins and is simply recorded — never a capture-by-capture tug of war.</para>
    ///
    /// <para>Guards are the same as the display-change restore's, via the shared
    /// <see cref="FilterWritable"/> / <see cref="ApplyPlacements"/>: dead handles, windows the borderless
    /// engine owns, and windows it just moved are all excluded, and the write itself goes through
    /// <see cref="WindowOps.RestorePlacement"/> (PersistenceRestore suppression + the visible /
    /// minimized-hidden-maximized split).</para>
    /// </summary>
    private void RestoreAdopted(IReadOnlyList<(IntPtr Handle, WindowRecord Target)> adopted)
    {
        var plan = FilterWritable(adopted, _getEngineOwned());
        if (plan.Count == 0) return;
        int moved = ApplyPlacements(plan);
        if (moved > 0) Log?.Invoke($"Adopted and restored {moved} window(s) (app restart)");
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

        // Re-claim records that lost their handle (their app restarted) or never had one (read off disk), by
        // window identity. Without this, a Chrome that was restarted since the last capture — or every window
        // after a reboot — has a record but no binding, and would silently not be restored.
        // (A reclaim only sets session-scoped handle bindings, which are never persisted — no dirty flag.)
        int claimed = _memory.Reclaim(key, ScanClaimCandidates());
        if (claimed > 0) Log?.Invoke($"Matched {claimed} restarted window(s) by identity for display {key}");

        int restoredCount = 0;
        for (int pass = 0; pass < RestorePasses; pass++)
        {
            // Iterate ALL remembered windows (not just currently-visible ones) so tray-hidden / minimized
            // windows get their restore rect fixed too; filter out engine-owned, just-engine-moved, and dead.
            var plan = FilterWritable(_memory.GetAll(key), _getEngineOwned());
            if (plan.Count == 0) break;

            int movedThisPass = ApplyPlacements(plan);
            if (pass == 0) restoredCount = movedThisPass;
            if (movedThisPass == 0) break; // everything reached its target
            if (pass < RestorePasses - 1) Thread.Sleep(RestorePassDelayMs);
        }
        return restoredCount;
    }

    /// <summary>
    /// The windows in <paramref name="items"/> we may write to right now: still alive, not owned by the
    /// borderless engine, and not moved by the engine a moment ago (its own LOCATIONCHANGE is still in flight).
    /// Shared by the display-change restore and the adoption restore so both obey exactly the same
    /// coordination rules.
    /// </summary>
    private static List<(IntPtr Handle, WindowRecord Target)> FilterWritable(
        IEnumerable<(IntPtr Handle, WindowRecord Record)> items, ISet<IntPtr> owned)
    {
        var plan = new List<(IntPtr, WindowRecord)>();
        foreach (var (h, r) in items)
            if (NativeMethods.IsWindow(h) && !owned.Contains(h) && !WindowOps.WasRecentlyMutatedByEngine(h))
                plan.Add((h, r));
        return plan;
    }

    /// <summary>One placement pass over an already-filtered plan; returns how many windows actually had to be moved.</summary>
    private static int ApplyPlacements(List<(IntPtr Handle, WindowRecord Target)> plan)
    {
        int moved = 0;
        foreach (var (h, t) in plan)
        {
            if (IsAtTarget(h, t)) continue; // already in place: verify-then-retry-only-misses
            WindowOps.RestorePlacement(h, t);
            moved++;
        }
        return moved;
    }

    /// <summary>Whether the window already sits within tolerance of its remembered rect (a maximized record always re-asserts).</summary>
    private static bool IsAtTarget(IntPtr h, WindowRecord t)
    {
        // Hidden / minimized / maximized windows are restored via SetWindowPlacement (idempotent) and their
        // on-screen rect isn't meaningful, so always (re)assert them.
        if (t.ShowCmd == NativeMethods.SW_SHOWMAXIMIZED) return false;
        if (!NativeMethods.IsWindowVisible(h) || NativeMethods.IsIconic(h)) return false;
        if (!NativeMethods.GetWindowRect(h, out var r)) return false;
        const int tol = 2;
        return Math.Abs(r.Left - t.Left) <= tol && Math.Abs(r.Top - t.Top) <= tol &&
               Math.Abs(r.Right - t.Right) <= tol && Math.Abs(r.Bottom - t.Bottom) <= tol;
    }

    private string ComputeKey() => LayoutKey.Compute(_getMonitors());

    // ---- Window eligibility (shared by capture and restore) ----

    /// <summary>
    /// Windows persistence may manage right now: real app windows, not engine-owned, not just engine-moved.
    /// Minimized windows are excluded from <b>capture</b> (their geometry isn't meaningful) but included when
    /// only identity is needed (<paramref name="includeIconic"/>) — an app that restarts straight to the
    /// taskbar should still be able to reclaim its record, since the restore path then fixes its
    /// rcNormalPosition via SetWindowPlacement.
    /// </summary>
    private List<WindowInfo> EligibleWindows(ISet<IntPtr> owned, bool includeIconic)
    {
        var list = new List<WindowInfo>();
        foreach (var w in WindowScanner.EnumerateCandidates(_getIgnoredProcesses()))
        {
            var h = w.Handle;
            if (owned.Contains(h)) continue;
            if (WindowOps.WasRecentlyMutatedByEngine(h)) continue;
            if (!includeIconic && NativeMethods.IsIconic(h)) continue;
            list.Add(w);
        }
        return list;
    }

    /// <summary>Identity of a live window: process name from the scan, class name read here (only the persistence path pays for it).</summary>
    private static LiveWindowRef ToLiveRef(WindowInfo w)
        => new(w.Handle, WindowIdentity.Create(w.ProcessName, WindowScanner.ClassNameOf(w.Handle)));

    /// <summary>Live windows offered to <see cref="LayoutMemory.Reclaim"/> as owners for orphaned records.</summary>
    private List<LiveWindowRef> ScanClaimCandidates()
    {
        var owned = _getEngineOwned();
        var list = new List<LiveWindowRef>();
        foreach (var w in EligibleWindows(owned, includeIconic: true))
            list.Add(ToLiveRef(w));
        return list;
    }

    private List<CapturedWindow> CaptureEligibleWindows()
    {
        var owned = _getEngineOwned();
        var result = new List<CapturedWindow>();
        foreach (var w in EligibleWindows(owned, includeIconic: false))
        {
            var h = w.Handle;
            var wp = new NativeMethods.WINDOWPLACEMENT { length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            bool gotWp = NativeMethods.GetWindowPlacement(h, ref wp);
            int showCmd = gotWp ? wp.showCmd : NativeMethods.SW_SHOWNORMAL;
            if (showCmd == NativeMethods.SW_SHOWMINIMIZED) continue; // capture while normal; a tray app's good record is kept after it later hides
            if (!NativeMethods.GetWindowRect(h, out var r)) continue;
            var n = gotWp ? wp.rcNormalPosition : r; // restore rect (workspace); fall back to the screen rect if placement is unavailable
            var identity = WindowIdentity.Create(w.ProcessName, WindowScanner.ClassNameOf(h));
            result.Add(new CapturedWindow(h, identity, w.Title,
                new WindowRecord(r.Left, r.Top, r.Right, r.Bottom, n.Left, n.Top, n.Right, n.Bottom, showCmd)));
        }
        return result;
    }

    // ---- Disk persistence ----

    /// <summary>
    /// Load the remembered layouts (worker thread, once, at startup). Aged/oversized entries are trimmed
    /// immediately so a long-abandoned file doesn't come back in full. Never throws.
    /// </summary>
    private void LoadFromDisk()
    {
        try
        {
            var file = LayoutStore.Load();
            _memory.ImportFromDisk(file); // every imported record starts UNBOUND — last session's HWNDs are meaningless
            int dropped = _memory.Trim(LayoutMemory.DefaultMaxPerKey, LayoutMemory.DefaultMaxAge, DateTime.UtcNow);
            if (dropped > 0) _dirty = true; // the trimmed shape should reach disk on the next flush
            int keys = _memory.Keys.Count;
            if (keys > 0) Log?.Invoke($"Loaded remembered layouts for {keys} display configuration(s)");
        }
        catch (Exception ex) { OnThreadError?.Invoke("PersistenceEngine load layouts", ex); }
        finally { _diskLoaded = true; }
    }

    /// <summary>Debounced flush: at most one write per <see cref="SaveDebounceMs"/>, and only when something changed.</summary>
    private void MaybeSave()
    {
        if (!_dirty) return;
        if ((DateTime.UtcNow - _lastSaveUtc).TotalMilliseconds < SaveDebounceMs) return;
        SaveNow();
    }

    /// <summary>
    /// Write the memory out now. No-op when nothing changed, or when the startup load never ran (never
    /// overwrite a good file with an empty memory). Failures are swallowed by <see cref="LayoutStore.Save"/>;
    /// the dirty flag is only cleared on success, so the next tick retries.
    /// </summary>
    private void SaveNow()
    {
        if (!_dirty || !_diskLoaded) return;
        _memory.Trim(LayoutMemory.DefaultMaxPerKey, LayoutMemory.DefaultMaxAge, DateTime.UtcNow);
        if (LayoutStore.Save(_memory.ExportForDisk()))
        {
            _dirty = false;
            _lastSaveUtc = DateTime.UtcNow;
        }
    }
}
