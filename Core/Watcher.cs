using System.Collections.Concurrent;
using System.Diagnostics;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>
/// Background watcher (see DESIGN.md §3): primarily WinEvent-hook-driven, with a low-frequency
/// fallback poll. Matched windows are stripped of their border and positioned; if the game reverts
/// them, they are reapplied (subject to an anti-thrash cap).
/// </summary>
public sealed class Watcher : IDisposable
{
    private readonly Func<AppConfig> _getConfig;

    private WinEventHook? _hook;
    private CancellationTokenSource? _cts;
    private Task? _pollLoop;
    // The debounce Timer is created once and re-armed via Change(); not new/Dispose'd per event (see ScheduleTick).
    private Timer? _debounce;
    // Separate short debounce (50ms) for foreground Clip/Mute: the hook pump thread only re-arms it;
    // the COM/ClipCursor work is never done synchronously inside the hook callback.
    private Timer? _fgDebounce;
    private readonly object _gate = new();

    // Tick re-entrancy guard: the poll thread / debounce Timer / Poke may race; if one is already
    // running, just skip (no queueing).
    private int _ticking;

    /// <summary>
    /// Log callback for the UI (raised on a background thread; the UI marshals to its own thread).
    /// The string already carries an <c>[HH:mm:ss]</c> timestamp prefix (added uniformly by
    /// <see cref="Emit"/>), so the UI can display it as-is without adding another timestamp.
    /// </summary>
    public event Action<string>? Log;

    // ---- Log ring buffer: the most recent N lines (timestamped), thread-safe ----
    // The engine starts in App.OnLaunched and takes over already-running games, so "Engine started" /
    // "Managing ..." all fire before DashboardPage subscribes to Log; a missed event is lost. The
    // buffer lets the page replay recent history when it subscribes.
    private const int LogBufferCapacity = 100;
    private readonly object _logGate = new();
    private readonly LinkedList<string> _logBuffer = new();

    /// <summary>
    /// Unified log sink: prepends an <c>[HH:mm:ss]</c> timestamp, pushes into the ring buffer (at most
    /// <see cref="LogBufferCapacity"/> lines), then raises the <see cref="Log"/> event. Callable from any
    /// thread; buffer reads/writes are serialized under <see cref="_logGate"/>.
    /// </summary>
    private void Emit(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_logGate)
        {
            _logBuffer.AddLast(line);
            while (_logBuffer.Count > LogBufferCapacity)
                _logBuffer.RemoveFirst();
        }
        Log?.Invoke(line);
    }

    /// <summary>
    /// Lets an external component (e.g. <see cref="Services.HotkeyService"/>) push a message to the
    /// dashboard log. Goes through the same sink as the engine's own logs (timestamp + ring buffer +
    /// <see cref="Log"/> event), so messages emitted before the UI subscribes (e.g. a startup hotkey
    /// registration failure) are still replayable by DashboardPage. Callable from any thread.
    /// </summary>
    public void LogExternal(string message) => Emit(message);

    /// <summary>
    /// Snapshot of recent log lines (oldest→newest, already timestamped). The UI calls this to replay
    /// history before subscribing to <see cref="Log"/>, covering events that already happened (e.g.
    /// startup takeovers). Returns an independent copy the caller can safely retain.
    /// </summary>
    public IReadOnlyList<string> GetRecentLog()
    {
        lock (_logGate)
            return _logBuffer.ToArray();
    }

    private volatile bool _running;
    public bool Running => _running;

    // ---- Event debounce: coalesce multiple WinEvents into a single Tick ----
    private const int DebounceMs = 300;
    // Fallback poll floor: event-driven covers the vast majority; polling only catches what slips
    // through, so throttle it to 5s minimum.
    private const int MinPollMs = 5000;

    // Anti-thrash parameters (10s window / cap of 3 / 2 warnings / 5min decay) live in Core/ThrashPolicy.cs.

    public Watcher(Func<AppConfig> getConfig) => _getConfig = getConfig;

    // ---- M4 dashboard contract: snapshot of managed windows + on-demand reschedule ----

    /// <summary>One currently-managed window: its handle + the profileId that manages it.</summary>
    public sealed record TakenWindow(IntPtr Handle, string ProfileId);

    /// <summary>Snapshot of currently-managed windows (reads the _takeover map, filtering out destroyed handles).</summary>
    public IReadOnlyList<TakenWindow> GetTakenWindows()
    {
        var list = new List<TakenWindow>();
        foreach (var kv in _takeover)
            if (NativeMethods.IsWindow(kv.Key))
                list.Add(new TakenWindow(kv.Key, kv.Value));
        return list;
    }

    /// <summary>Schedule an immediate scan (reapply). No-op when the engine is not running.</summary>
    public void Poke() => ScheduleTick();

    public void Start()
    {
        if (_running) return;
        _running = true;

        _hook = new WinEventHook();
        _hook.WindowEvent += OnWindowEvent;
        if (!_hook.Start())
        {
            // Hook failed to install: event-driven detection is lost, but the fallback poll remains,
            // so the engine runs in degraded mode (just slower to react).
            Emit("Event hook failed to start; falling back to periodic polling (responsiveness may degrade)");
            _hook.WindowEvent -= OnWindowEvent;
            _hook.Dispose();
            _hook = null;
        }

        _cts = new CancellationTokenSource();
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));

        // Create the debounce Timers once; from here on only re-arm via Change() (see ScheduleTick / ScheduleForegroundRefresh).
        lock (_gate)
        {
            _debounce ??= new Timer(_ => SafeTick(), null, Timeout.Infinite, Timeout.Infinite);
            _fgDebounce ??= new Timer(_ => ForegroundRefreshTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        Emit("Engine started");

        // On startup, write every enabled Unity resolution preset once (regardless of MatchKind): the
        // game is usually not running yet, so the write takes effect immediately.
        ApplyResolutionPresets(forceAllKinds: true);

        ScheduleTick(); // On startup, do an initial scan of existing windows
    }

    /// <summary>
    /// Called on config changes (App subscribes to ConfigService.Changed): writes every enabled Unity
    /// resolution preset once. Same policy as startup, ignoring MatchKind — the user just edited config
    /// and the game is usually not running, so the write takes effect immediately.
    /// <para>Also schedules a Tick so that an EngineEnabled true↔false edge is detected promptly by
    /// <see cref="SafeTick"/> (~300ms debounce, instead of waiting for the next 5s fallback poll) —
    /// turning the engine off restores immediately, turning it on re-takes-over immediately.</para>
    /// </summary>
    public void OnConfigChanged()
    {
        if (!_running) return;
        ApplyResolutionPresets(forceAllKinds: true);
        ScheduleTick(); // Promptly detect the EngineEnabled edge (pause→restore / resume→re-take-over)
    }

    /// <param name="restoreWindows">Whether to restore the windows we took over when stopping.</param>
    public void Stop(bool restoreWindows = true)
    {
        if (!_running) return;
        _running = false;

        // Stop the hook thread
        if (_hook != null)
        {
            _hook.WindowEvent -= OnWindowEvent;
            _hook.Dispose();
            _hook = null;
        }

        // Stop the poll loop
        _cts?.Cancel();
        try { _pollLoop?.Wait(2000); } catch { /* ignore */ }
        _pollLoop = null;
        _cts?.Dispose();
        _cts = null;

        List<uint> toUnmute;
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
            _fgDebounce?.Dispose();
            _fgDebounce = null;
            ReleaseClipLocked();            // Stopping the engine must release the cursor clip
            toUnmute = DrainMutedLocked();  // Under the lock, only take the diff; do the COM work after releasing it
        }
        UnmuteOutsideLock(toUnmute);        // Stopping the engine must unmute everything (COM not under the lock)

        if (restoreWindows)
        {
            WindowOps.RestoreAll();
            _takeover.Clear();
            Emit("All managed windows restored");
        }
        Emit("Engine stopped");
    }

    // The hook callback only debounces; the actual scan is left to the timer, to avoid heavy work in a
    // system callback. Foreground-switch Clip/Mute (which involves COM) is not done synchronously on the
    // hook pump thread; instead a 50ms short debounce is armed and the work happens after the callback returns.
    private void OnWindowEvent(uint eventType, IntPtr hwnd)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
            ScheduleForegroundRefresh();
        ScheduleTick();
    }

    // Foreground Clip/Mute debounce interval: foreground switches arrive in bursts, so coalesce briefly;
    // but it must stay responsive (clipping the cursor / restoring sound).
    private const int ForegroundDebounceMs = 50;

    private void ScheduleTick()
    {
        lock (_gate)
        {
            if (!_running || _debounce is null) return;
            // Reuse the same Timer, re-arming its due time (no new/Dispose per event).
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>Schedule a foreground Clip/Mute refresh (50ms debounce). The hook pump thread only calls this; the COM/ClipCursor work is left to the Timer thread.</summary>
    private void ScheduleForegroundRefresh()
    {
        lock (_gate)
        {
            if (!_running || _fgDebounce is null) return;
            _fgDebounce.Change(ForegroundDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>Foreground debounce fires: read the current foreground window and refresh the cursor clip and background mute (all on the Timer thread, not the hook callback).</summary>
    private void ForegroundRefreshTick()
    {
        if (!_running) return;

        // While the engine is paused (EngineEnabled=false), foreground events must not apply new Clip/Mute —
        // only release existing ones. (SafeTick's edge detection already called ReleaseAll once when it
        // flipped to false; this catches foreground events that arrive during the paused period.)
        if (!_getConfig().EngineEnabled)
        {
            List<uint> toUnmute;
            lock (_gate)
            {
                ReleaseClipLocked();
                toUnmute = DrainMutedLocked();
            }
            UnmuteOutsideLock(toUnmute);
            return;
        }

        var fg = NativeMethods.GetForegroundWindow();
        UpdateClip(fg);
        UpdateMute(fg);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _getConfig();
            int interval = Math.Max(MinPollMs, cfg.PollIntervalMs);
            try { await Task.Delay(interval, ct); }
            catch (TaskCanceledException) { break; }

            SafeTick();
        }
    }

    /// <summary>
    /// The EngineEnabled value observed by the previous SafeTick. Used to detect the true→false edge: the
    /// moment it flips false, call <see cref="ReleaseAll"/> to restore every managed window. Only read/written
    /// inside the _ticking guard (SafeTick is serialized), so no volatile needed.
    /// Initial value true: the engine defaults to EngineEnabled=true, so if the user turned it off before
    /// startup, the first tick treats it as a true→false edge and restores (a no-op).
    /// </summary>
    private bool _lastEngineEnabled = true;

    private void SafeTick()
    {
        if (!_running) return;
        // Re-entrancy guard: the poll thread / debounce Timer / Poke may fire concurrently; if one is
        // already running, just skip (no queueing, no pileup).
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
        try
        {
            if (!_running) return;
            var cfg = _getConfig();

            // Engine toggle edge detection: the moment it goes true→false, release every takeover
            // (restore border/topmost/Clip/Mute) and then early-out as usual. The engine is merely
            // paused — hook/poll stay alive — so when EngineEnabled is turned back on the next tick re-takes-over.
            bool enabled = cfg.EngineEnabled;
            if (_lastEngineEnabled && !enabled)
            {
                _lastEngineEnabled = false;
                try { ReleaseAll(); }
                catch (Exception ex) { Emit("Error restoring windows on pause: " + ex.Message); }
                Emit("Engine paused; all managed windows restored");
                return;
            }
            _lastEngineEnabled = enabled;

            if (!enabled) return;
            try { Tick(cfg); }
            catch (Exception ex) { Emit("Scan error: " + ex.Message); }
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>Time each (window, profile) was first seen, used for DelayMs (windows get rebuilt during a game's startup).</summary>
    private readonly ConcurrentDictionary<(IntPtr, string), DateTime> _firstSeen = new();

    /// <summary>
    /// Takeover map: window handle → the profileId that manages it. Doubles as the "announce once"
    /// dedupe set, and serves ReleaseProfile (to find a profile's windows) and ClipCursor (to tell
    /// whether the foreground window belongs to a ClipCursor profile).
    /// </summary>
    private readonly ConcurrentDictionary<IntPtr, string> _takeover = new();

    /// <summary>Dedupe set of (window, profile) pairs already reported as "no permission" (value is a placeholder), to avoid spamming every tick. Pruned alongside dead-window cleanup.</summary>
    private readonly ConcurrentDictionary<(IntPtr, string), byte> _noPermLogged = new();

    /// <summary>Handle of the window whose cursor is currently clipped; IntPtr.Zero = none. Read/written only under _gate.</summary>
    private IntPtr _clippedHwnd = IntPtr.Zero;

    /// <summary>Set of process PIDs we currently mute (MuteInBackground). Read/written only under _gate.</summary>
    private readonly HashSet<uint> _mutedPids = new();

    /// <summary>Anti-thrash: per-managed-window throttle state (decision logic lives in <see cref="ThrashPolicy"/>).</summary>
    private readonly ConcurrentDictionary<IntPtr, ThrashState> _thrash = new();

    private void Tick(AppConfig cfg)
    {
        // Correct the registry presets while the process is not running (writing while it runs is
        // pointless — the game writes the current values back on exit).
        ApplyResolutionPresets(forceAllKinds: false);

        var windows = WindowScanner.EnumerateTopLevel();
        foreach (var w in windows)
        {
            foreach (var p in cfg.Profiles)
            {
                if (!MatchEngine.Matches(w, p)) continue;

                // Borderless delay: record the time on first sight, only act once it elapses.
                var key = (w.Handle, p.Id);
                var first = _firstSeen.GetOrAdd(key, DateTime.UtcNow);
                if ((DateTime.UtcNow - first).TotalMilliseconds < p.DelayMs) break;

                // Already-managed window: cap reapplies per unit time to avoid a fight-to-the-death loop
                // with the game's own window management.
                if (WindowOps.IsTracked(w.Handle) && !AllowReapply(w.Handle, p))
                    break;

                var target = PlacementResolver.Resolve(w, p, cfg);
                var outcome = WindowOps.Apply(w.Handle, in target);

                if (outcome == ApplyOutcome.Failed)
                {
                    // Protected/UWP window we can't touch: don't register a takeover; report "no permission"
                    // explicitly per the DESIGN §8 promise. Report once per (window, profile) — dedupe via the
                    // key (re-reporting would spam the log).
                    if (_noPermLogged.TryAdd((w.Handle, p.Id), 0))
                        Emit($"No permission to manage \"{p.Name}\": {w.Title}");
                    break;
                }

                // Record the takeover map (for ReleaseProfile / ClipCursor); announce once on first record.
                if (_takeover.TryAdd(w.Handle, p.Id))
                {
                    if (outcome == ApplyOutcome.Changed) Emit($"Managing \"{p.Name}\": {w.Title}");
                }

                break; // A window only takes the first matching profile
            }
        }

        CleanupDeadWindows();

        // If the current foreground window is one managed by a ClipCursor profile (just placed), clip it
        // immediately; otherwise keep/release.
        var fg = NativeMethods.GetForegroundWindow();
        UpdateClip(fg);
        UpdateMute(fg);
    }

    /// <summary>
    /// Reapply throttle for an already-managed window: a thin shell — all the logic lives in
    /// <see cref="ThrashPolicy.Evaluate"/> (10s window / cap of 3 / 2 warnings / 5min decay). Here we
    /// only turn a "should warn" signal into a log line.
    /// </summary>
    private bool AllowReapply(IntPtr hWnd, Profile p)
    {
        var s = _thrash.GetOrAdd(hWnd, _ => new ThrashState { WindowStartUtc = DateTime.UtcNow });
        bool allow, warn;
        bool finalWarn;
        lock (s)
        {
            allow = ThrashPolicy.Evaluate(s, DateTime.UtcNow, out warn);
            finalWarn = s.TotalWarns >= ThrashPolicy.MaxWarns;
        }
        if (warn)
        {
            string tail = finalWarn ? " (further identical conflicts will be silenced until the window is rebuilt or the engine restarts)" : "";
            Emit($"\"{p.Name}\" keeps reverting; backing off this round (likely fighting the game's own window management){tail}");
        }
        return allow;
    }

    /// <summary>
    /// Apply Unity startup resolution presets (see <see cref="UnityPreset"/>).
    /// <para><paramref name="forceAllKinds"/>=true (engine start / config change): write every enabled preset,
    /// regardless of MatchKind and without any process check.</para>
    /// <para>false (every Tick): correct only profiles with MatchKind=Process whose process is **not running**
    /// and whose current registry value ≠ target (the read-compare is cheap; skip if the process is running,
    /// since the game writes the current values back on exit). Other MatchKinds aren't corrected on tick
    /// (no reliable process check).</para>
    /// </summary>
    private void ApplyResolutionPresets(bool forceAllKinds)
    {
        var cfg = _getConfig();
        foreach (var p in cfg.Profiles)
        {
            var preset = p.ResolutionPreset;
            if (preset is not { Enabled: true }) continue;
            if (string.IsNullOrWhiteSpace(preset.RegistryPath)) continue;

            if (!forceAllKinds)
            {
                // Tick correction: only handle process matches, and only while the process is not running.
                if (p.MatchKind != MatchKind.Process) continue;
                if (IsProcessRunning(p.MatchValue)) continue;
                // Don't rewrite if it's already the target value.
                if (UnityPreset.AlreadyMatches(preset.RegistryPath, preset.Width, preset.Height, preset.Windowed))
                    continue;
            }

            try
            {
                bool wrote = UnityPreset.Write(preset.RegistryPath, preset.Width, preset.Height, preset.Windowed);
                if (wrote)
                    Emit($"Wrote resolution preset {preset.Width}×{preset.Height} → {preset.RegistryPath}");
                else
                    Emit($"Resolution preset not written: registry path or values not found → HKCU\\{preset.RegistryPath} (game not installed or wrong path?)");
            }
            catch (Exception ex)
            {
                Emit("Error writing resolution preset: " + ex.Message);
            }
        }
    }

    /// <summary>Whether a process with the given name is running (ignores .exe and case).</summary>
    private static bool IsProcessRunning(string matchValue)
    {
        if (string.IsNullOrWhiteSpace(matchValue)) return false;
        string name = matchValue.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? matchValue[..^4]
            : matchValue;
        try
        {
            var procs = Process.GetProcessesByName(name);
            try { return procs.Length > 0; }
            finally { foreach (var pr in procs) pr.Dispose(); }
        }
        catch { return false; }
    }

    /// <summary>Prune entries with dead handles from every dictionary, to prevent unbounded growth and to stop a stale snapshot from polluting a new window when a handle is reused.</summary>
    private void CleanupDeadWindows()
    {
        foreach (var key in _firstSeen.Keys)
            if (!NativeMethods.IsWindow(key.Item1))
                _firstSeen.TryRemove(key, out _);

        foreach (var h in _takeover.Keys)
            if (!NativeMethods.IsWindow(h))
                _takeover.TryRemove(h, out _);

        foreach (var h in _thrash.Keys)
            if (!NativeMethods.IsWindow(h))
                _thrash.TryRemove(h, out _);

        foreach (var key in _noPermLogged.Keys)
            if (!NativeMethods.IsWindow(key.Item1))
                _noPermLogged.TryRemove(key, out _);

        // Prune WindowOps' raw snapshots in lockstep: handles get reused, so if a destroyed window's
        // snapshot isn't cleared, a new window grabbing the same HWND would have its EnsureSnapshot blocked
        // by the stale value, and restore would write back the previous window's style/position.
        WindowOps.ForgetDead(NativeMethods.IsWindow);

        // If the clipped window has been destroyed, release the clip immediately.
        lock (_gate)
        {
            if (_clippedHwnd != IntPtr.Zero && !NativeMethods.IsWindow(_clippedHwnd))
                ReleaseClipLocked();
        }
    }

    // ---- ClipCursor lifecycle: clip only when foreground; release on blur/destroy/stop ----

    /// <summary>
    /// Refresh the cursor clip for the current foreground window. If the foreground window is managed by a
    /// ClipCursor=true profile → clip to its target rect; otherwise release. Called from several places
    /// (foreground events, end of Tick), serialized under _gate.
    /// </summary>
    private void UpdateClip(IntPtr foreground)
    {
        lock (_gate)
        {
            if (!_running) { ReleaseClipLocked(); return; }

            if (foreground != IntPtr.Zero &&
                _takeover.TryGetValue(foreground, out var profileId))
            {
                var cfg = _getConfig();
                var p = FindProfile(cfg, profileId);
                if (p is { ClipCursor: true })
                {
                    var rect = ResolveClipRect(foreground, p, cfg);
                    if (rect is { } r)
                    {
                        NativeMethods.ClipCursor(in r);
                        _clippedHwnd = foreground;
                        return;
                    }
                }
            }

            // Foreground isn't a clip window (or no rect could be resolved): release if we were clipping.
            ReleaseClipLocked();
        }
    }

    /// <summary>Get this profile's target rect for this window; if none resolves (e.g. Kind=None), fall back to the window's current rect.</summary>
    private static NativeMethods.RECT? ResolveClipRect(IntPtr hwnd, Profile p, AppConfig cfg)
    {
        var w = new WindowInfo { Handle = hwnd };
        var rect = PlacementResolver.Resolve(w, p, cfg).Rect;
        if (rect is { } r) return r;
        return NativeMethods.GetWindowRect(hwnd, out var cur) ? cur : null;
    }

    /// <summary>Release the cursor clip (if any). Must be called under _gate.</summary>
    private void ReleaseClipLocked()
    {
        if (_clippedHwnd == IntPtr.Zero) return;
        NativeMethods.ClipCursorRelease(IntPtr.Zero);
        _clippedHwnd = IntPtr.Zero;
    }

    private static Profile? FindProfile(AppConfig cfg, string id)
        => cfg.Profiles.FirstOrDefault(p => p.Id == id);

    // ---- MuteInBackground lifecycle: foreground==it → unmute, foreground!=it and switch on → mute; destroy/stop → unmute ----

    /// <summary>
    /// Refresh "background mute" for the current foreground window: walk all managed windows whose profile
    /// has MuteInBackground on — that window in the foreground → unmute; not in the foreground → mute.
    /// <para>Thread model (fixed in review): COM (AudioMute) must not run under the <see cref="_gate"/> lock,
    /// nor on the hook pump thread. So under the lock we only compute the diff of pids to mute/unmute and
    /// update <see cref="_mutedPids"/> immediately (serialized decision, no duplicate dispatch); the actual
    /// COM calls happen after releasing the lock. This method's callers (Tick / 50ms foreground debounce /
    /// ReleaseProfile) are all off the hook callback.</para>
    /// </summary>
    private void UpdateMute(IntPtr foreground)
    {
        var cfg = _getConfig();
        // Aggregate hwnd → should-mute; for one pid with multiple windows, any window in the foreground counts as foreground (= don't mute).
        var want = new Dictionary<uint, bool>(); // pid → should mute?
        foreach (var kv in _takeover)
        {
            var p = FindProfile(cfg, kv.Value);
            if (p is not { MuteInBackground: true }) continue;
            if (!NativeMethods.IsWindow(kv.Key)) continue;

            NativeMethods.GetWindowThreadProcessId(kv.Key, out uint pid);
            if (pid == 0) continue;

            bool isForeground = kv.Key == foreground;
            if (want.TryGetValue(pid, out bool prevMute))
                want[pid] = prevMute && !isForeground;
            else
                want[pid] = !isForeground;
        }

        // Under the lock: only compute the diff + update _mutedPids; leave COM until after releasing it.
        var toMute = new List<uint>();
        var toUnmute = new List<uint>();
        lock (_gate)
        {
            if (!_running) { toUnmute = DrainMutedLocked(); }
            else
            {
                foreach (var (pid, shouldMute) in want)
                {
                    bool currentlyMuted = _mutedPids.Contains(pid);
                    if (shouldMute && !currentlyMuted) { toMute.Add(pid); _mutedPids.Add(pid); }
                    else if (!shouldMute && currentlyMuted) { toUnmute.Add(pid); _mutedPids.Remove(pid); }
                }
                // Pids no longer tracked by any MuteInBackground window (window destroyed / profile released): unmute.
                foreach (var pid in _mutedPids.Where(p => !want.ContainsKey(p)).ToList())
                {
                    toUnmute.Add(pid);
                    _mutedPids.Remove(pid);
                }
            }
        }

        foreach (var pid in toMute) AudioMute.SetMuteByPid(pid, true);
        foreach (var pid in toUnmute) AudioMute.SetMuteByPid(pid, false);
    }

    /// <summary>Take all pids we currently mute and clear _mutedPids (must be called under _gate); the caller does the COM unmute after releasing the lock.</summary>
    private List<uint> DrainMutedLocked()
    {
        var list = _mutedPids.ToList();
        _mutedPids.Clear();
        return list;
    }

    /// <summary>Unmute a set of pids (COM, no lock held).</summary>
    private static void UnmuteOutsideLock(List<uint> pids)
    {
        foreach (var pid in pids) AudioMute.SetMuteByPid(pid, false);
    }

    /// <summary>
    /// Restore every window managed by this profile and drop tracking (called when the UI disables a profile).
    /// Restore each window + prune it from every dictionary; if the clipped window belongs to it, release the clip.
    /// </summary>
    public void ReleaseProfile(string profileId)
    {
        // Find the windows managed by this profile.
        var hwnds = _takeover.Where(kv => kv.Value == profileId).Select(kv => kv.Key).ToList();

        foreach (var h in hwnds)
        {
            WindowOps.Restore(h);
            _takeover.TryRemove(h, out _);
            _thrash.TryRemove(h, out _);

            // _firstSeen / _noPermLogged are keyed by (hwnd, profileId); prune exactly this pair.
            _firstSeen.TryRemove((h, profileId), out _);
            _noPermLogged.TryRemove((h, profileId), out _);

            lock (_gate)
            {
                if (_clippedHwnd == h) ReleaseClipLocked();
            }
        }

        if (hwnds.Count > 0) Emit($"Released {hwnds.Count} window(s) of profile ({profileId})");

        // These windows are no longer tracked: refresh mute state, unmuting any pid no longer belonging to a MuteInBackground window.
        UpdateMute(NativeMethods.GetForegroundWindow());
    }

    /// <summary>
    /// Release all takeover state: restore every managed window (border/topmost restored), release ClipCursor,
    /// unmute everything, and clear _takeover/_firstSeen/_thrash/_noPermLogged.
    /// <para><b>Does not stop</b> the hook thread / poll loop — this is the "engine paused" semantics
    /// (EngineEnabled=false), distinct from <see cref="Stop"/> (which truly stops the service and tears down
    /// the hook). After it's re-enabled (EngineEnabled=true), the next tick re-takes-over any still-matching
    /// windows per the rules. Called by SafeTick when it detects the engine was turned off.</para>
    /// </summary>
    public void ReleaseAll()
    {
        // Restore every engine-managed window (only those under _takeover; manual quick-borderless is not included).
        var hwnds = _takeover.Keys.ToList();
        foreach (var h in hwnds)
            WindowOps.Restore(h);

        _takeover.Clear();
        _firstSeen.Clear();
        _thrash.Clear();
        _noPermLogged.Clear();

        // Release the cursor clip + collect the pids to unmute (COM done after releasing the lock).
        List<uint> toUnmute;
        lock (_gate)
        {
            ReleaseClipLocked();
            toUnmute = DrainMutedLocked();
        }
        UnmuteOutsideLock(toUnmute);

        // Note: don't touch _hook / _pollLoop / _running — pausing the engine is not stopping the service.
    }

    public void Dispose() => Stop();
}
