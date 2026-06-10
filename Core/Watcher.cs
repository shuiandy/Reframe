using System.Collections.Concurrent;

namespace Reframe.Core;

/// <summary>
/// 后台守护:周期扫描窗口、对命中的应用规则;游戏改回去会被下一轮糊回来。
/// M1 为轮询;M2 升级为 WinEvent 钩子事件驱动 + 低频兜底轮询(见 DESIGN.md §3)。
/// </summary>
public sealed class Watcher : IDisposable
{
    private readonly Func<AppConfig> _getConfig;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>给 UI 的日志回调(后台线程触发,UI 侧自行切线程)。</summary>
    public event Action<string>? Log;

    public bool Running => _loop is { IsCompleted: false };

    public Watcher(Func<AppConfig> getConfig) => _getConfig = getConfig;

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <param name="restoreWindows">停止时是否把接管过的窗口还原。</param>
    public void Stop(bool restoreWindows = true)
    {
        _cts?.Cancel();
        try { _loop?.Wait(2000); } catch { /* ignore */ }
        _loop = null;
        if (restoreWindows)
        {
            WindowOps.RestoreAll();
            Log?.Invoke("已还原全部接管窗口");
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        Log?.Invoke("引擎已启动");
        while (!ct.IsCancellationRequested)
        {
            var cfg = _getConfig();
            int interval = Math.Max(300, cfg.PollIntervalMs);

            if (cfg.EngineEnabled)
            {
                try { Tick(cfg); }
                catch (Exception ex) { Log?.Invoke("扫描异常: " + ex.Message); }
            }

            try { await Task.Delay(interval, ct); }
            catch (TaskCanceledException) { break; }
        }
        Log?.Invoke("引擎已停止");
    }

    /// <summary>首次见到 (窗口,profile) 的时间,用于 DelayMs(游戏启动期窗口会重建)。</summary>
    private readonly ConcurrentDictionary<(IntPtr, string), DateTime> _firstSeen = new();

    /// <summary>已宣告过接管的窗口,避免日志刷屏。</summary>
    private readonly ConcurrentDictionary<IntPtr, byte> _announced = new();

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

                var target = PlacementResolver.Resolve(w, p, cfg);
                bool changed = WindowOps.Apply(w.Handle, in target);
                if (changed && _announced.TryAdd(w.Handle, 0))
                    Log?.Invoke($"接管「{p.Name}」: {w.Title}");

                break; // 一个窗口只吃第一个命中的 profile
            }
        }
    }

    public void Dispose() => Stop();
}
