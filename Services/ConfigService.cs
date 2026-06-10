using Reframe.Core;

namespace Reframe.Services;

/// <summary>
/// 配置的进程内单一真源:封装 ConfigStore，叠加热重载。
/// 监听 config.json 的外部改动(UI 之外的手改/同步)→ 防抖 → 重新 Load → 触发 Changed。
/// 自己 Save 引发的文件事件会被忽略。
/// 约定:Config 引用在热重载后会整体更换,消费者"用完即取,不要缓存"。
/// </summary>
public sealed class ConfigService
{
    private static readonly Lazy<ConfigService> _instance = new(() => new ConfigService());
    public static ConfigService Instance => _instance.Value;

    private volatile AppConfig _config;
    /// <summary>当前配置。引用替换是原子的;热重载后这里会指向新对象。</summary>
    public AppConfig Config => _config;

    /// <summary>Save 或外部文件改动后触发。任意线程,UI 自行 DispatcherQueue。</summary>
    public event Action? Changed;

    private readonly FileSystemWatcher _fsw;
    private readonly object _gate = new();

    // 自写忽略:Save 前记下写盘时刻,文件事件在该时刻附近一律视为自己引起。
    private DateTime _selfWriteUtc = DateTime.MinValue;
    private static readonly TimeSpan SelfWriteWindow = TimeSpan.FromMilliseconds(800);

    // 防抖:外部连写(编辑器保存往往多次触发 Changed/Created)合并成一次重载。
    private const int DebounceMs = 300;
    private Timer? _debounce;

    private ConfigService()
    {
        _config = ConfigStore.Load(); // 首次访问即加载(Load 会在缺文件时落默认盘)

        Directory.CreateDirectory(ConfigStore.Dir);
        _fsw = new FileSystemWatcher(ConfigStore.Dir, System.IO.Path.GetFileName(ConfigStore.Path_))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _fsw.Changed += OnFileEvent;
        _fsw.Created += OnFileEvent;
        _fsw.Renamed += OnFileEvent;
    }

    /// <summary>写盘 + 触发 Changed。</summary>
    public void Save()
    {
        lock (_gate)
        {
            _selfWriteUtc = DateTime.UtcNow; // 先标记,挡掉随之而来的文件事件
            ConfigStore.Save(_config);
        }
        Changed?.Invoke();
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // 自己刚写过的,忽略(文件事件可能滞后,留一个时间窗)
        lock (_gate)
        {
            if (DateTime.UtcNow - _selfWriteUtc < SelfWriteWindow) return;
        }

        // 防抖:重置计时器,静默 300ms 后才真正重载
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Reload(), null, DebounceMs, Timeout.Infinite);
        }
    }

    private void Reload()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;

            // 防抖窗口结束时再核一次自写标记(防抖期间发生的 Save)
            if (DateTime.UtcNow - _selfWriteUtc < SelfWriteWindow) return;
        }

        AppConfig fresh;
        try { fresh = ConfigStore.Load(); }
        catch { return; } // 读失败(写到一半)就放过,下一次事件再来

        _config = fresh;     // 原子替换引用
        Changed?.Invoke();
    }
}
