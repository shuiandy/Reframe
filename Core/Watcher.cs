using System.Collections.Concurrent;
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

    public Watcher(Func<AppConfig> getConfig) => _getConfig = getConfig;

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
        ScheduleTick(); // 启动即先扫一遍现有窗口
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
        }

        if (restoreWindows)
        {
            WindowOps.RestoreAll();
            Log?.Invoke("已还原全部接管窗口");
        }
        Log?.Invoke("引擎已停止");
    }

    // 钩子回调里只做防抖,真正扫描留给计时器,避免在系统回调里干重活。
    private void OnWindowEvent(IntPtr hwnd) => ScheduleTick();

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

    /// <summary>已宣告过接管的窗口,避免日志刷屏。</summary>
    private readonly ConcurrentDictionary<IntPtr, byte> _announced = new();

    /// <summary>防拉锯:每个已接管窗口最近一段时间内的重新应用次数。</summary>
    private readonly ConcurrentDictionary<IntPtr, ThrashGuard> _thrash = new();

    private sealed class ThrashGuard
    {
        public DateTime WindowStart = DateTime.UtcNow;
        public int Count;
        public bool Warned;
    }

    private void Tick(AppConfig cfg)
    {
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
                if (changed && _announced.TryAdd(w.Handle, 0))
                    Log?.Invoke($"接管「{p.Name}」: {w.Title}");

                break; // 一个窗口只吃第一个命中的 profile
            }
        }

        CleanupDeadWindows();
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
                if (!g.Warned)
                {
                    g.Warned = true;
                    Log?.Invoke($"「{p.Name}」反复被改回,本轮放过(疑似与游戏窗口管理冲突)");
                }
                return false;
            }
            g.Count++;
            return true;
        }
    }

    /// <summary>句柄已失效的条目从字典剔除,防止无限增长。</summary>
    private void CleanupDeadWindows()
    {
        foreach (var key in _firstSeen.Keys)
            if (!NativeMethods.IsWindow(key.Item1))
                _firstSeen.TryRemove(key, out _);

        foreach (var h in _announced.Keys)
            if (!NativeMethods.IsWindow(h))
                _announced.TryRemove(h, out _);

        foreach (var h in _thrash.Keys)
            if (!NativeMethods.IsWindow(h))
                _thrash.TryRemove(h, out _);
    }

    public void Dispose() => Stop();
}
