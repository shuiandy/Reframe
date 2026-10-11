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

    /// <summary>
    /// How long the settle path keeps waiting while the desktop sits in a transient fallback mode
    /// (<see cref="LayoutKey.IsTransient"/>), before giving up and returning to Idle.
    ///
    /// <para>The episode this covers — a TV/monitor switched away from the PC and back — lasts a few seconds,
    /// so ~10 s of patience covers it comfortably while keeping the freeze bounded (a permanently frozen engine
    /// would be a worse failure than the one being fixed). Giving up is safe rather than merely tolerable:
    /// <see cref="OnCapture"/> refuses to capture under a transient topology regardless of state, and the
    /// "a fallback episode happened" flag survives, so the eventual return still triggers a restore.</para>
    /// </summary>
    private const int TransientRetryMs = 500;
    private const int MaxTransientSettleRetries = 20;

    // ---- Injected collaborators (keep Core free of a Services dependency) ----
    private readonly Func<IReadOnlyList<MonitorDesc>> _getMonitors;
    private readonly Func<ISet<IntPtr>> _getEngineOwned;
    private readonly Func<bool> _isEngineBusy;
    private readonly Func<bool> _isEnabled;
    private readonly Func<int> _getCaptureSeconds;
    private readonly Func<IEnumerable<string>> _getIgnoredProcesses;

    private readonly LayoutMemory _memory;
    private readonly ProcessLiveness _liveness = new();
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

    /// <summary>
    /// A transient fallback topology has been observed since the last completed settle — i.e. the desktop went
    /// through a "monitor briefly gone, everything squeezed into 1024×768" episode.
    ///
    /// <para>This is what makes the <b>same-key return</b> restorable. The panel leaves and comes back to the
    /// very configuration it left, so <c>liveKey == _currentKey</c> and the ordinary "did the topology change?"
    /// test would conclude nothing happened — while every window on screen has in fact been mangled by the
    /// fallback. A fallback episode therefore counts as a topology change for the purpose of deciding to
    /// restore. Cleared by the settle that acts on it.</para>
    /// </summary>
    private bool _sawTransient;

    /// <summary>Log de-duplication: the "paused" notice has already been emitted for the fallback episode currently in progress.</summary>
    private bool _inTransientEpisode;

    /// <summary>
    /// When the fallback episode currently in progress was first observed. Only meaningful while
    /// <see cref="_inTransientEpisode"/> is set; it is the start of the interval for which the adoption clock
    /// is stopped on the way out (see <see cref="LayoutMemory.ShiftUnboundAnchors"/>).
    /// </summary>
    private DateTime _transientStartedUtc;

    /// <summary>How many settle rounds have already been spent waiting for a fallback episode to end (see <see cref="MaxTransientSettleRetries"/>).</summary>
    private int _transientWaits;
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

        // Adoption restore needs evidence that an app *restarted*, not merely that a window with a familiar
        // caption appeared: the same live process opening another window is the user's doing and must never
        // be moved. See RememberedWindow.ProcessId / ProcessLiveness. Worker-thread only, like the memory.
        _memory = new LayoutMemory { ProcessGone = _liveness.IsGone };
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
            // Starting up *inside* a fallback episode (a reboot with the TV on another input) must not make
            // 1024x768 the baseline: leave the key at None and remember the episode, so the real topology
            // arriving a moment later reads as a change and takes the normal restore path.
            var boot = Probe();
            if (boot.Transient) NoteTransient();
            else _currentKey = boot.Key;

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
            case Msg.DisplayChanged:
                // Look at the topology *now*, before freezing: this message is delivered because the mode just
                // changed, so it is the one moment guaranteed to catch a fallback that is actually on screen.
                // See PersistenceDecision.ForDisplayChanged for why freezing alone is not enough.
                if (_isEnabled())
                {
                    if (PersistenceDecision.ForDisplayChanged(enabled: true, Probe().Transient)
                        == DisplayChangedDecision.NoteTransientThenFreeze) NoteTransient();
                    EnterFreeze();
                }
                break;
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
        var live = Probe();
        if (live.Transient)
        {
            // Nothing here is worth remembering: every window has been squeezed into the fallback desktop.
            NoteTransient(); // also starts the episode clock, so a manual click can't be the observation that misses it
            Log?.Invoke("Capture now ignored: the display is in a transient fallback mode (1024x768)");
            return;
        }
        NoteTransientEnded();
        string key = live.Key;
        if (key == LayoutKey.None) return;
        _currentKey = key;
        // The user has just declared the current geometry to be the truth, which discharges any restore a
        // fallback episode had left owing — otherwise the next tick would freeze and re-restore over it.
        _sawTransient = false;
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
        var live = Probe();
        if (live.Transient)
        {
            // Applying the real layout onto a 1024x768 fallback desktop just gets it re-squeezed; wait for the
            // panel to come back (which restores on its own via the settle path).
            NoteTransient();
            Log?.Invoke("Restore now ignored: the display is in a transient fallback mode (1024x768)");
            return;
        }
        NoteTransientEnded();
        string key = live.Key;
        _currentKey = key;
        _sawTransient = false; // doing the restore by hand discharges the one a fallback episode had left owing
        if (!_memory.HasSnapshot(key)) { Log?.Invoke("No remembered layout for the current display"); return; }
        _state = State.Restoring;
        int n = DoRestore(key);
        Log?.Invoke($"Restored {n} window(s) for display {key}");
        _state = State.Settling;
        _graceTimer?.Change(GraceMs, Timeout.Infinite);
    }

    private void OnCapture()
    {
        // Cheap guards first: don't pay for a monitor enumeration on every tick while persistence is off or
        // the engine is frozen / restoring / settling (never capture then — the core invariant).
        bool enabled = _isEnabled(), idle = _state == State.Idle;
        var live = (enabled && idle) ? Probe() : Topology.Unknown;

        var decision = PersistenceDecision.ForCapture(enabled, idle, live.Transient, _sawTransient,
                                                      live.Key, _currentKey);
        if (decision == CaptureDecision.Skip) return;

        if (decision == CaptureDecision.PauseTransient)
        {
            // Windows' no-display fallback. Not a configuration anyone works on: do not capture (the windows
            // sitting on it are mangled), and above all do not move _currentKey onto it — that key is the
            // anchor for recognising the panel's return.
            NoteTransient();
            return;
        }

        NoteTransientEnded();

        if (decision == CaptureDecision.Freeze)
        {
            // Either the topology changed and we noticed via capture (no WM_DISPLAYCHANGE arrived, or a window
            // event raced ahead of it), or a fallback episode just ended. Freeze now and let the settle path
            // restore; do NOT capture (that would write the half-scrambled layout over the good snapshot).
            // Closes the "WinEvent-before-display-message" race.
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
        bool frozen = _state == State.Frozen, enabled = _isEnabled();
        // Probe before the engine-busy check, not after: a RetryBusy that skipped the probe would blind us for
        // the whole time the borderless engine is mid-tick, which is exactly when a display blackout is likely
        // to be in progress. Observing costs one monitor enumeration; missing the episode costs the layout.
        var live = frozen && enabled ? Probe() : Topology.Unknown;
        if (live.Transient) NoteTransient();
        bool busy = frozen && enabled && _isEngineBusy();
        bool keyChanged = live.Key != _currentKey;

        var decision = PersistenceDecision.ForSettle(frozen, enabled, busy, live.Transient,
                                                    _transientWaits < MaxTransientSettleRetries,
                                                    _sawTransient, keyChanged, _memory.HasSnapshot(live.Key));
        switch (decision)
        {
            case SettleDecision.Ignore:
                return; // superseded by a later transition

            case SettleDecision.Abort:
                // Disabled mid-freeze (user turned persistence off after the display changed): no restore.
                _state = State.Idle;
                return;

            case SettleDecision.RetryBusy:
                // The borderless engine is mid-tick (it also reacts to the display change); let it finish so we
                // don't race its re-placement of game windows.
                _settleTimer?.Change(EngineBusyRetryMs, Timeout.Infinite);
                return;

            case SettleDecision.WaitTransient:
                // Settling *on* the fallback would be the bug: restoring into a 1024x768 desktop just gets the
                // layout re-squeezed, and adopting its key would throw away the anchor we need to recognise the
                // panel's return. Stay frozen and look again — _currentKey is untouched.
                _transientWaits++;
                _settleTimer?.Change(TransientRetryMs, Timeout.Infinite);
                return;

            case SettleDecision.StopWaitingTransient:
                // Out of patience. Idle is safe here: OnCapture still refuses to capture while the topology is
                // transient, and _sawTransient still forces the restore whenever the real topology returns.
                _transientWaits = 0;
                _state = State.Idle;
                return;
        }

        // A real topology: this is the one the engine may act on and adopt — unless the enumeration came back
        // empty (LayoutKey.None, mid-transition). Adopting *that* as current would make the very next capture
        // see a "topology change" back to the real key and fire a full restore that nothing warranted; keep
        // the last real key as the anchor and simply resume.
        NoteTransientEnded();
        _transientWaits = 0;
        _sawTransient = false;
        if (live.Key != LayoutKey.None) _currentKey = live.Key;

        if (decision == SettleDecision.Restore)
        {
            // Restore when the configuration actually changed, or when the desktop just came back from a
            // fallback episode (same key, but every window was squeezed while the panel was away) — and we
            // have a remembered layout for it. A spurious same-key WM_DISPLAYCHANGE with no episode behind it
            // must not pull windows back over the user's recent moves; that falls through to Idle below.
            _state = State.Restoring;
            int restored = DoRestore(live.Key);
            Log?.Invoke($"Restored {restored} window(s) for display {live.Key}");
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
            // Iterate every remembered window we may actually act on (not just currently-visible ones) so
            // tray-hidden / minimized windows get their restore rect fixed too; filter out engine-owned,
            // just-engine-moved, and dead.
            //
            // GetRestorable — not "everything bound". Reclaim above binds records whether or not it can tell
            // which window is which, because binding is what stops duplicate records piling up. Moving on
            // such a binding is the bug: after a reboot the disk layout arrives unbound, the monitor wakes,
            // and QQ's four same-class records would be dealt out by ordinal and then physically applied,
            // reshaping a chat window into the image viewer's box. Same rule as the adoption restore: no
            // confidence, no move — the window is still tracked, just not touched.
            var plan = FilterWritable(_memory.GetRestorable(key), _getEngineOwned());
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

    /// <summary>One look at the live display topology: its DisplayKey plus whether it is a transient fallback.</summary>
    private readonly record struct Topology(string Key, bool Transient)
    {
        /// <summary>Placeholder for the paths that short-circuit before probing (a cheaper guard fired first): behaves as "no monitors".</summary>
        public static readonly Topology Unknown = new(LayoutKey.None, false);
    }

    /// <summary>
    /// Enumerate the monitors <b>once</b> and derive both facts the state machine needs from that single
    /// snapshot. Separate <c>Compute</c> / <c>IsTransient</c> calls would each re-enumerate and could disagree
    /// with each other mid-transition — exactly the moment this feature is about.
    /// </summary>
    private Topology Probe()
    {
        var monitors = _getMonitors();
        return new Topology(LayoutKey.Compute(monitors), LayoutKey.IsTransient(monitors));
    }

    /// <summary>
    /// Record that the live topology is Windows' fallback mode: flag the owed restore, start the episode clock
    /// on the first observation, and say so in the log <b>once per episode</b>. Capture ticks every ~2 s, the
    /// settle path re-checks twice a second, and every display-change message probes too, so an unconditional
    /// log would bury the dashboard in identical lines for the whole episode.
    /// </summary>
    private void NoteTransient()
    {
        _sawTransient = true;
        if (_inTransientEpisode) return;
        _inTransientEpisode = true;
        _transientStartedUtc = DateTime.UtcNow;
        Log?.Invoke("Display is in a transient fallback mode (1024x768); capture/restore paused");
    }

    /// <summary>
    /// Counterpart of <see cref="NoteTransient"/>, called from every path that has just observed a real
    /// topology: closes the episode, logs one "resumed" line, and — the part that matters — <b>hands the
    /// blacked-out interval back to the adoption clock</b>.
    ///
    /// <para>Nothing could be adopted while the desktop was in the fallback (no capture, no restore, no
    /// reclaim), so counting that interval against <see cref="LayoutMemory.AdoptionWindow"/> would expire
    /// records for time in which they had no chance. The failure it produces is silent and total: Reframe
    /// starts with the login while the TV is on the console's input, the layout file anchors every record at
    /// that instant, the user switches back twenty minutes later, and every record is now stale — so the
    /// reclaim binds them all without confidence, not one window is moved, and the squeezed geometry
    /// overwrites the good bucket as soon as the grace period ends. Called before any restore or capture runs
    /// on the returning topology, which is what makes the reclaim that follows see fresh records.</para>
    /// </summary>
    private void NoteTransientEnded()
    {
        if (!_inTransientEpisode) return;
        _inTransientEpisode = false;

        int refreshed = _memory.ShiftUnboundAnchors(_transientStartedUtc, DateTime.UtcNow);
        Log?.Invoke(refreshed > 0
            ? $"Display left the transient fallback mode; capture/restore resumed ({refreshed} record(s) kept adoptable)"
            : "Display left the transient fallback mode; capture/restore resumed");
    }

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

    /// <summary>
    /// Identity of a live window: process name from the scan, class name read here (only the persistence path
    /// pays for it), plus the caption — which is what lets the matcher tell same-class sibling windows apart
    /// (see <see cref="WindowMatcher"/>).
    /// </summary>
    private static LiveWindowRef ToLiveRef(WindowInfo w)
        => new(w.Handle, WindowIdentity.Create(w.ProcessName, WindowScanner.ClassNameOf(w.Handle)), w.Title, w.ProcessId);

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
                new WindowRecord(r.Left, r.Top, r.Right, r.Bottom, n.Left, n.Top, n.Right, n.Bottom, showCmd),
                w.ProcessId));
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
