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
    // 防抖 Timer 建一次、用 Change() 重排,不每事件 new/Dispose(见 ScheduleTick)。
    private Timer? _debounce;
    // 前台 Clip/Mute 的独立短防抖(50ms):钩子泵线程只重排它,COM/ClipCursor 不在钩子回调里同步做。
    private Timer? _fgDebounce;
    private readonly object _gate = new();

    // Tick 重入闸:轮询线程 / 防抖 Timer / Poke 可能并发,已在跑就直接跳过(不排队)。
    private int _ticking;

    /// <summary>
    /// 给 UI 的日志回调(后台线程触发,UI 侧自行切线程)。
    /// 传入的字符串已带 <c>[HH:mm:ss]</c> 时间戳前缀(由 <see cref="Emit"/> 统一加),
    /// UI 直接显示即可,不要再加时间戳。
    /// </summary>
    public event Action<string>? Log;

    // ---- 日志环形缓冲:最近 N 条(带时间戳),线程安全 ----
    // 引擎在 App.OnLaunched 即启动并接管已运行的游戏,"引擎已启动/接管「×××」"都发生在
    // DashboardPage 订阅 Log 之前,事件错过即丢。缓冲让页面订阅时能回放最近历史。
    private const int LogBufferCapacity = 100;
    private readonly object _logGate = new();
    private readonly LinkedList<string> _logBuffer = new();

    /// <summary>
    /// 统一日志出口:加 <c>[HH:mm:ss]</c> 时间戳、压入环形缓冲(最多 <see cref="LogBufferCapacity"/> 条)、
    /// 再触发 <see cref="Log"/> 事件。任意线程可调,缓冲读写在 <see cref="_logGate"/> 下串行化。
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
    /// 外部组件(如 <see cref="Services.HotkeyService"/>)向仪表盘日志推一条消息。
    /// 走与引擎内部同一出口(带时间戳 + 入环形缓冲 + 触发 <see cref="Log"/>),
    /// 使订阅前发生的消息(启动期热键注册失败)也能被 DashboardPage 回放看到。任意线程可调。
    /// </summary>
    public void LogExternal(string message) => Emit(message);

    /// <summary>
    /// 最近日志快照(旧→新,已含时间戳)。UI 订阅 <see cref="Log"/> 前先调此回放历史,
    /// 弥补订阅前已发生的事件(如启动期接管)。返回独立拷贝,调用方可安全持有。
    /// </summary>
    public IReadOnlyList<string> GetRecentLog()
    {
        lock (_logGate)
            return _logBuffer.ToArray();
    }

    private volatile bool _running;
    public bool Running => _running;

    // ---- 事件防抖:多个 WinEvent 合并成一次 Tick ----
    private const int DebounceMs = 300;
    // 兜底轮询下限:事件驱动已覆盖绝大多数,轮询只兜底漏网,降频到 5s 起。
    private const int MinPollMs = 5000;

    // 防拉锯参数(10s 窗口 / 3 次上限 / 2 条告警 / 5min 衰减)集中在 Core/ThrashPolicy.cs。

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
        if (!_hook.Start())
        {
            // 钩子没装上:事件驱动失效,但兜底轮询仍在,引擎降级运行(只是响应慢些)。
            Emit("事件钩子启动失败,降级为定时轮询(响应可能变慢)");
            _hook.WindowEvent -= OnWindowEvent;
            _hook.Dispose();
            _hook = null;
        }

        _cts = new CancellationTokenSource();
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));

        // 防抖 Timer 建一次,后续只用 Change() 重排(见 ScheduleTick / ScheduleForegroundRefresh)。
        lock (_gate)
        {
            _debounce ??= new Timer(_ => SafeTick(), null, Timeout.Infinite, Timeout.Infinite);
            _fgDebounce ??= new Timer(_ => ForegroundRefreshTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        Emit("引擎已启动");

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

        List<uint> toUnmute;
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
            _fgDebounce?.Dispose();
            _fgDebounce = null;
            ReleaseClipLocked();            // 引擎停止必须解除光标限制
            toUnmute = DrainMutedLocked();  // 锁内只取差集,COM 出锁后做
        }
        UnmuteOutsideLock(toUnmute);        // 引擎停止必须取消所有静音(COM 不在锁内)

        if (restoreWindows)
        {
            WindowOps.RestoreAll();
            _takeover.Clear();
            Emit("已还原全部接管窗口");
        }
        Emit("引擎已停止");
    }

    // 钩子回调里只做防抖,真正扫描留给计时器,避免在系统回调里干重活。
    // 前台切换的 Clip/Mute(含 COM)不在钩子泵线程同步做,改排一个 50ms 短防抖,出回调再处理。
    private void OnWindowEvent(uint eventType, IntPtr hwnd)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
            ScheduleForegroundRefresh();
        ScheduleTick();
    }

    // 前台 Clip/Mute 防抖间隔:前台切换会连发,短攒一下;又要够灵敏(夹光标/恢复声音)。
    private const int ForegroundDebounceMs = 50;

    private void ScheduleTick()
    {
        lock (_gate)
        {
            if (!_running || _debounce is null) return;
            // 复用同一 Timer,重排 due 时间(不每事件 new/Dispose)。
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>排一次前台 Clip/Mute 刷新(50ms 防抖)。钩子泵线程只调这,COM/ClipCursor 留给 Timer 线程。</summary>
    private void ScheduleForegroundRefresh()
    {
        lock (_gate)
        {
            if (!_running || _fgDebounce is null) return;
            _fgDebounce.Change(ForegroundDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>前台防抖到点:读当前前台窗口,刷新光标限制与后台静音(均在 Timer 线程,不在钩子回调)。</summary>
    private void ForegroundRefreshTick()
    {
        if (!_running) return;
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

    private void SafeTick()
    {
        if (!_running) return;
        // 重入闸:轮询线程 / 防抖 Timer / Poke 可能同时触发,已有一次在跑就直接跳过(不排队、不堆积)。
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
        try
        {
            if (!_running) return;
            var cfg = _getConfig();
            if (!cfg.EngineEnabled) return;
            try { Tick(cfg); }
            catch (Exception ex) { Emit("扫描异常: " + ex.Message); }
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>首次见到 (窗口,profile) 的时间,用于 DelayMs(游戏启动期窗口会重建)。</summary>
    private readonly ConcurrentDictionary<(IntPtr, string), DateTime> _firstSeen = new();

    /// <summary>
    /// 接管映射:窗口句柄 → 接管它的 profileId。既做"宣告一次"去重,也供 ReleaseProfile
    /// 按 profile 找回名下窗口、供 ClipCursor 判断前台是否属于某 ClipCursor profile。
    /// </summary>
    private readonly ConcurrentDictionary<IntPtr, string> _takeover = new();

    /// <summary>已就(窗口,profile)报过一次"无权限"的去重集合(值占位),防每 tick 刷屏。死窗口清理时一并剔除。</summary>
    private readonly ConcurrentDictionary<(IntPtr, string), byte> _noPermLogged = new();

    /// <summary>当前被夹住光标的窗口句柄;IntPtr.Zero = 未夹。仅 _gate 下读写。</summary>
    private IntPtr _clippedHwnd = IntPtr.Zero;

    /// <summary>当前被我们静音的进程 PID 集合(MuteInBackground)。仅 _gate 下读写。</summary>
    private readonly HashSet<uint> _mutedPids = new();

    /// <summary>防拉锯:每个已接管窗口的节流状态(判定逻辑见 <see cref="ThrashPolicy"/>)。</summary>
    private readonly ConcurrentDictionary<IntPtr, ThrashState> _thrash = new();

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
                var outcome = WindowOps.Apply(w.Handle, in target);

                if (outcome == ApplyOutcome.Failed)
                {
                    // 受保护/UWP 窗口动不了:不登记接管,按 DESIGN §8 承诺明确报"无权限"。
                    // 每窗口每 profile 只报一次(借 _firstSeen 已存在的 key 去重——再报会刷屏)。
                    if (_noPermLogged.TryAdd((w.Handle, p.Id), 0))
                        Emit($"无权限,无法接管「{p.Name}」: {w.Title}");
                    break;
                }

                // 记录接管映射(供 ReleaseProfile / ClipCursor);首次记录时宣告一次
                if (_takeover.TryAdd(w.Handle, p.Id))
                {
                    if (outcome == ApplyOutcome.Changed) Emit($"接管「{p.Name}」: {w.Title}");
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

    /// <summary>
    /// 已接管窗口的重应用节流:薄壳,逻辑全在 <see cref="ThrashPolicy.Evaluate"/>
    /// (10s 窗口 / 3 次上限 / 2 条告警 / 5min 衰减)。本处只把"该告警"翻译成一条日志。
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
            string tail = finalWarn ? "(后续相同冲突不再提示,直到窗口重建或引擎重启)" : "";
            Emit($"「{p.Name}」反复被改回,本轮放过(疑似与游戏窗口管理冲突){tail}");
        }
        return allow;
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
                    Emit($"已写入分辨率预设 {preset.Width}×{preset.Height} → {preset.RegistryPath}");
                else
                    Emit($"分辨率预设未写入:注册表路径或键值不存在 → HKCU\\{preset.RegistryPath}(未安装该游戏或路径填错?)");
            }
            catch (Exception ex)
            {
                Emit("写入分辨率预设异常: " + ex.Message);
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

    /// <summary>句柄已失效的条目从各字典剔除,防止无限增长 + 防句柄复用时脏快照污染新窗口。</summary>
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

        // WindowOps 的原始快照同步清理:句柄会被系统复用,旧窗口销毁后若快照不清,
        // 新窗口拿到同一 HWND 时建快照会被旧值挡住,还原会写回上一个窗口的样式/位置。
        WindowOps.ForgetDead(NativeMethods.IsWindow);

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
    /// 该窗口在前台 → 取消静音;不在前台 → 静音。
    /// <para>线程模型(本次评审修复):COM(AudioMute)不能在 <see cref="_gate"/> 锁内、也不该跑在钩子泵线程。
    /// 故锁内只算"要静音/取消静音的 pid 差集"并即时更新 <see cref="_mutedPids"/>(决策串行化、不会重复下发),
    /// 出锁后才执行实际的 COM 调用。本方法的调用者(Tick / 50ms 前台防抖 / ReleaseProfile)均不在钩子回调里。</para>
    /// </summary>
    private void UpdateMute(IntPtr foreground)
    {
        var cfg = _getConfig();
        // hwnd → 是否应静音 汇总;同一 pid 多窗口时,只要有一个窗口在前台就算前台(=不静音)。
        var want = new Dictionary<uint, bool>(); // pid → 应静音?
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

        // 锁内:只算差集 + 更新 _mutedPids;COM 留到出锁后。
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
                // 已不再被任何 MuteInBackground 窗口跟踪的 pid(窗口销毁 / profile 释放):取消静音
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

    /// <summary>取出当前所有被我们静音的 pid 并清空 _mutedPids(须在 _gate 下调用);COM 取消静音由调用方出锁后做。</summary>
    private List<uint> DrainMutedLocked()
    {
        var list = _mutedPids.ToList();
        _mutedPids.Clear();
        return list;
    }

    /// <summary>对一组 pid 取消静音(COM,不持锁)。</summary>
    private static void UnmuteOutsideLock(List<uint> pids)
    {
        foreach (var pid in pids) AudioMute.SetMuteByPid(pid, false);
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

            // _firstSeen / _noPermLogged 以 (hwnd, profileId) 为键,精确剔除这一对
            _firstSeen.TryRemove((h, profileId), out _);
            _noPermLogged.TryRemove((h, profileId), out _);

            lock (_gate)
            {
                if (_clippedHwnd == h) ReleaseClipLocked();
            }
        }

        if (hwnds.Count > 0) Emit($"已释放 profile({profileId}) 的 {hwnds.Count} 个窗口");

        // 这些窗口已不再被跟踪:刷新静音状态,把不再属于任何 MuteInBackground 窗口的 pid 取消静音
        UpdateMute(NativeMethods.GetForegroundWindow());
    }

    public void Dispose() => Stop();
}
