using System.Collections.Concurrent;
using System.Diagnostics;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>
/// 后台守护(见 DESIGN.md §3):WinEvent 钩子事件驱动为主 + 低频兜底轮询。
/// 命中的窗口去边框/定位;游戏改回去会被重新糊上(带防拉锯上限)。
/// </summary>
public sealed class Watcher : IDisposable
{
    private readonly Func<AppConfig> _getConfig;

    private WinEventHook? _hook;
    private CancellationTokenSource? _cts;
    private Task? _pollLoop;
    private Timer? _debounce;
    private readonly object _gate = new();

    /// <summary>给 UI 的日志回调(后台线程触发,UI 侧自行切线程)。</summary>
    public event Action<string>? Log;

    private volatile bool _running;
    public bool Running => _running;

    // ---- 事件防抖:多个 WinEvent 合并成一次 Tick ----
    private const int DebounceMs = 300;
    // 兜底轮询下限:事件驱动已覆盖绝大多数,轮询只兜底漏网,降频到 5s 起。
    private const int MinPollMs = 5000;

    // ---- 防拉锯:对已接管窗口,10s 内最多重新应用 3 次 ----
    private const int ThrashWindowMs = 10000;
    private const int ThrashMaxApplies = 3;
    // 每个窗口的拉锯告警最多记 2 条,此后永久静默(窗口销毁清理时随字典项一起重置)。
    private const int ThrashMaxWarns = 2;

    public Watcher(Func<AppConfig> getConfig) => _getConfig = getConfig;

    // ---- M4 仪表盘契约:被接管窗口快照 + 主动重新调度 ----

    /// <summary>当前被接管的一个窗口:句柄 + 接管它的 profileId。</summary>
    public sealed record TakenWindow(IntPtr Handle, string ProfileId);

    /// <summary>当前被接管窗口的快照(读 _takeover 字典,过滤已销毁句柄)。</summary>
    public IReadOnlyList<TakenWindow> GetTakenWindows()
    {
        var list = new List<TakenWindow>();
        foreach (var kv in _takeover)
            if (NativeMethods.IsWindow(kv.Key))
                list.Add(new TakenWindow(kv.Key, kv.Value));
        return list;
    }

    /// <summary>立即调度一次扫描(重新应用)。引擎未运行时无副作用。</summary>
    public void Poke() => ScheduleTick();

    public void Start()
    {
        if (_running) return;
        _running = true;

        _hook = new WinEventHook();
        _hook.WindowEvent += OnWindowEvent;
        _hook.Start();

        _cts = new CancellationTokenSource();
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));

        Log?.Invoke("引擎已启动");

        // 启动即把所有启用的 Unity 分辨率预设写一次(不分 MatchKind):游戏多半还没起,写了立即生效。
        ApplyResolutionPresets(forceAllKinds: true);

        ScheduleTick(); // 启动即先扫一遍现有窗口
    }

    /// <summary>
    /// 配置变化时调用(App 订阅 ConfigService.Changed):把所有启用的 Unity 分辨率预设写一次。
    /// 与启动同口径,不分 MatchKind——用户刚改完配置,通常游戏未运行,写了即生效。
    /// </summary>
    public void OnConfigChanged()
    {
        if (!_running) return;
        ApplyResolutionPresets(forceAllKinds: true);
    }

    /// <param name="restoreWindows">停止时是否把接管过的窗口还原。</param>
    public void Stop(bool restoreWindows = true)
    {
        if (!_running) return;
        _running = false;

        // 停钩子线程
        if (_hook != null)
        {
            _hook.WindowEvent -= OnWindowEvent;
            _hook.Dispose();
            _hook = null;
        }

        // 停轮询
        _cts?.Cancel();
        try { _pollLoop?.Wait(2000); } catch { /* ignore */ }
        _pollLoop = null;
        _cts?.Dispose();
        _cts = null;

        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
            ReleaseClipLocked(); // 引擎停止必须解除光标限制
            UnmuteAllLocked();   // 引擎停止必须取消所有静音
        }

        if (restoreWindows)
        {
            WindowOps.RestoreAll();
            _takeover.Clear();
            Log?.Invoke("已还原全部接管窗口");
        }
        Log?.Invoke("引擎已停止");
    }

    // 钩子回调里只做防抖,真正扫描留给计时器,避免在系统回调里干重活。
    // 前台切换额外即时更新光标限制(ClipCursor 必须前台才夹、失焦即放)。
    private void OnWindowEvent(uint eventType, IntPtr hwnd)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            UpdateClip(hwnd);
            UpdateMute(hwnd);
        }
        ScheduleTick();
    }

    private void ScheduleTick()
    {
        lock (_gate)
        {
            if (!_running) return;
            _debounce?.Dispose();
            _debounce = new Timer(_ => SafeTick(), null, DebounceMs, Timeout.Infinite);
        }
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

    private void SafeTick()
    {
        if (!_running) return;
        var cfg = _getConfig();
        if (!cfg.EngineEnabled) return;
        try { Tick(cfg); }
        catch (Exception ex) { Log?.Invoke("扫描异常: " + ex.Message); }
    }

    /// <summary>首次见到 (窗口,profile) 的时间,用于 DelayMs(游戏启动期窗口会重建)。</summary>
    private readonly ConcurrentDictionary<(IntPtr, string), DateTime> _firstSeen = new();

    /// <summary>
    /// 接管映射:窗口句柄 → 接管它的 profileId。既做"宣告一次"去重,也供 ReleaseProfile
    /// 按 profile 找回名下窗口、供 ClipCursor 判断前台是否属于某 ClipCursor profile。
    /// </summary>
    private readonly ConcurrentDictionary<IntPtr, string> _takeover = new();

    /// <summary>当前被夹住光标的窗口句柄;IntPtr.Zero = 未夹。仅 _gate 下读写。</summary>
    private IntPtr _clippedHwnd = IntPtr.Zero;

    /// <summary>当前被我们静音的进程 PID 集合(MuteInBackground)。仅 _gate 下读写。</summary>
    private readonly HashSet<uint> _mutedPids = new();

    /// <summary>防拉锯:每个已接管窗口最近一段时间内的重新应用次数。</summary>
    private readonly ConcurrentDictionary<IntPtr, ThrashGuard> _thrash = new();

    private sealed class ThrashGuard
    {
        public DateTime WindowStart = DateTime.UtcNow;
        public int Count;
        public bool Warned;        // 本 10s 窗口内是否已告警(防同一窗口期重复)
        public int TotalWarns;     // 该窗口累计告警条数,达上限后永久静默
    }

    private void Tick(AppConfig cfg)
    {
        // 进程未运行时纠正注册表预设(进程在跑写了也没用——游戏退出会写回)。
        ApplyResolutionPresets(forceAllKinds: false);

        var windows = WindowScanner.EnumerateTopLevel();
        foreach (var w in windows)
        {
            foreach (var p in cfg.Profiles)
            {
                if (!MatchEngine.Matches(w, p)) continue;

                // 无边框延迟:第一眼先记时间,到点才动手
                var key = (w.Handle, p.Id);
                var first = _firstSeen.GetOrAdd(key, DateTime.UtcNow);
                if ((DateTime.UtcNow - first).TotalMilliseconds < p.DelayMs) break;

                // 已接管窗口:限制单位时间内的重应用次数,防止和游戏自身窗口管理打架死循环
                if (WindowOps.IsTracked(w.Handle) && !AllowReapply(w.Handle, p))
                    break;

                var target = PlacementResolver.Resolve(w, p, cfg);
                bool changed = WindowOps.Apply(w.Handle, in target);

                // 记录接管映射(供 ReleaseProfile / ClipCursor);首次记录时宣告一次
                if (_takeover.TryAdd(w.Handle, p.Id))
                {
                    if (changed) Log?.Invoke($"接管「{p.Name}」: {w.Title}");
                }

                break; // 一个窗口只吃第一个命中的 profile
            }
        }

        CleanupDeadWindows();

        // 当前前台若是某 ClipCursor profile 接管的窗口(刚被摆好),即时夹上;否则维持/解除。
        var fg = NativeMethods.GetForegroundWindow();
        UpdateClip(fg);
        UpdateMute(fg);
    }

    /// <summary>已接管窗口的重应用节流:10s 窗口内超过 3 次则本轮放过并告警一次。</summary>
    private bool AllowReapply(IntPtr hWnd, Profile p)
    {
        var g = _thrash.GetOrAdd(hWnd, _ => new ThrashGuard());
        var now = DateTime.UtcNow;
        lock (g)
        {
            if ((now - g.WindowStart).TotalMilliseconds > ThrashWindowMs)
            {
                g.WindowStart = now;
                g.Count = 0;
                g.Warned = false;
            }
            if (g.Count >= ThrashMaxApplies)
            {
                // 每个 10s 窗口最多告警一次,且整个窗口生命周期累计最多 ThrashMaxWarns 条,之后永久静默。
                if (!g.Warned && g.TotalWarns < ThrashMaxWarns)
                {
                    g.Warned = true;
                    g.TotalWarns++;
                    string tail = g.TotalWarns >= ThrashMaxWarns
                        ? "(后续相同冲突不再提示,直到窗口重建或引擎重启)"
                        : "";
                    Log?.Invoke($"「{p.Name}」反复被改回,本轮放过(疑似与游戏窗口管理冲突){tail}");
                }
                return false;
            }
            g.Count++;
            return true;
        }
    }

    /// <summary>
    /// 应用 Unity 启动分辨率预设(见 <see cref="UnityPreset"/>)。
    /// <para><paramref name="forceAllKinds"/>=true(引擎启动 / 配置变化):写所有启用的预设,不分 MatchKind、不做进程判定。</para>
    /// <para>false(每次 Tick):只对 MatchKind=Process 的 profile,且其进程**不在运行**、注册表当前值≠目标时才纠正
    /// (读比较很便宜;进程在跑就跳过,因为游戏退出时会把当前值写回)。其它 MatchKind 不在 tick 纠正(无可靠进程判定)。</para>
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
                // Tick 纠正:只处理进程匹配,且仅当进程不在运行时
                if (p.MatchKind != MatchKind.Process) continue;
                if (IsProcessRunning(p.MatchValue)) continue;
                // 已经是目标值就别重复写
                if (UnityPreset.AlreadyMatches(preset.RegistryPath, preset.Width, preset.Height, preset.Windowed))
                    continue;
            }

            try
            {
                bool wrote = UnityPreset.Write(preset.RegistryPath, preset.Width, preset.Height, preset.Windowed);
                if (wrote)
                    Log?.Invoke($"已写入分辨率预设 {preset.Width}×{preset.Height} → {preset.RegistryPath}");
                else
                    Log?.Invoke($"分辨率预设未写入:注册表路径或键值不存在 → HKCU\\{preset.RegistryPath}(未安装该游戏或路径填错?)");
            }
            catch (Exception ex)
            {
                Log?.Invoke("写入分辨率预设异常: " + ex.Message);
            }
        }
    }

    /// <summary>按进程名(忽略 .exe、大小写)判断是否有该进程在运行。</summary>
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

    /// <summary>句柄已失效的条目从字典剔除,防止无限增长。</summary>
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

        // 被夹光标的窗口若已销毁,立即解除限制
        lock (_gate)
        {
            if (_clippedHwnd != IntPtr.Zero && !NativeMethods.IsWindow(_clippedHwnd))
                ReleaseClipLocked();
        }
    }

    // ---- ClipCursor 生命周期:前台才夹,失焦/销毁/停止即放 ----

    /// <summary>
    /// 按当前前台窗口刷新光标限制。前台是某 ClipCursor=true profile 接管的窗口 → 夹到其目标矩形;
    /// 否则解除。多处调用(前台事件、Tick 末尾),用 _gate 串行化。
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

            // 前台不是 clip 窗口(或解析不出矩形):若之前夹着,解除
            ReleaseClipLocked();
        }
    }

    /// <summary>取该 profile 对该窗口的目标矩形;解析不出(如 Kind=None)则退回窗口当前矩形。</summary>
    private static NativeMethods.RECT? ResolveClipRect(IntPtr hwnd, Profile p, AppConfig cfg)
    {
        var w = new WindowInfo { Handle = hwnd };
        var rect = PlacementResolver.Resolve(w, p, cfg).Rect;
        if (rect is { } r) return r;
        return NativeMethods.GetWindowRect(hwnd, out var cur) ? cur : null;
    }

    /// <summary>解除光标限制(若有)。须在 _gate 下调用。</summary>
    private void ReleaseClipLocked()
    {
        if (_clippedHwnd == IntPtr.Zero) return;
        NativeMethods.ClipCursorRelease(IntPtr.Zero);
        _clippedHwnd = IntPtr.Zero;
    }

    private static Profile? FindProfile(AppConfig cfg, string id)
        => cfg.Profiles.FirstOrDefault(p => p.Id == id);

    // ---- MuteInBackground 生命周期:前台=它→取消静音,前台≠它且开关开→静音;销毁/停止→取消静音 ----

    /// <summary>
    /// 按当前前台窗口刷新"后台静音":遍历所有接管窗口,其 profile 开了 MuteInBackground 的——
    /// 该窗口在前台 → 取消静音;不在前台 → 静音。状态缓存在 _mutedPids,避免重复 COM 调用。
    /// </summary>
    private void UpdateMute(IntPtr foreground)
    {
        var cfg = _getConfig();
        // hwnd → (pid, 是否前台) 汇总;同一 pid 多窗口时,只要有一个窗口在前台就算前台。
        var want = new Dictionary<uint, bool>(); // pid → 应静音?
        foreach (var kv in _takeover)
        {
            var p = FindProfile(cfg, kv.Value);
            if (p is not { MuteInBackground: true }) continue;
            if (!NativeMethods.IsWindow(kv.Key)) continue;

            NativeMethods.GetWindowThreadProcessId(kv.Key, out uint pid);
            if (pid == 0) continue;

            bool isForeground = kv.Key == foreground;
            // 该 pid 最终是否静音 = 它的所有相关窗口都不在前台
            if (want.TryGetValue(pid, out bool prevMute))
                want[pid] = prevMute && !isForeground;
            else
                want[pid] = !isForeground;
        }

        lock (_gate)
        {
            if (!_running) { UnmuteAllLocked(); return; }

            foreach (var (pid, shouldMute) in want)
            {
                bool currentlyMuted = _mutedPids.Contains(pid);
                if (shouldMute && !currentlyMuted)
                {
                    AudioMute.SetMuteByPid(pid, true);
                    _mutedPids.Add(pid);
                }
                else if (!shouldMute && currentlyMuted)
                {
                    AudioMute.SetMuteByPid(pid, false);
                    _mutedPids.Remove(pid);
                }
            }

            // 已不再被任何 MuteInBackground profile 跟踪的 pid(窗口销毁/profile 释放):取消静音
            foreach (var pid in _mutedPids.Where(p => !want.ContainsKey(p)).ToList())
            {
                AudioMute.SetMuteByPid(pid, false);
                _mutedPids.Remove(pid);
            }
        }
    }

    /// <summary>取消所有我们施加的静音。须在 _gate 下调用。</summary>
    private void UnmuteAllLocked()
    {
        foreach (var pid in _mutedPids)
            AudioMute.SetMuteByPid(pid, false);
        _mutedPids.Clear();
    }

    /// <summary>
    /// 还原该 profile 接管的全部窗口并解除跟踪(UI 禁用 profile 时调)。
    /// 逐窗口 Restore + 从各字典剔除;若被夹的窗口属于它,解除光标限制。
    /// </summary>
    public void ReleaseProfile(string profileId)
    {
        // 找出该 profile 名下的窗口
        var hwnds = _takeover.Where(kv => kv.Value == profileId).Select(kv => kv.Key).ToList();

        foreach (var h in hwnds)
        {
            WindowOps.Restore(h);
            _takeover.TryRemove(h, out _);
            _thrash.TryRemove(h, out _);

            // _firstSeen 以 (hwnd, profileId) 为键,精确剔除这一对
            _firstSeen.TryRemove((h, profileId), out _);

            lock (_gate)
            {
                if (_clippedHwnd == h) ReleaseClipLocked();
            }
        }

        if (hwnds.Count > 0) Log?.Invoke($"已释放 profile({profileId}) 的 {hwnds.Count} 个窗口");

        // 这些窗口已不再被跟踪:刷新静音状态,把不再属于任何 MuteInBackground 窗口的 pid 取消静音
        UpdateMute(NativeMethods.GetForegroundWindow());
    }

    public void Dispose() => Stop();
}
