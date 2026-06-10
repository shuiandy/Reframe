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
    private bool _shutdown;

    // 最近一次 Save 是否失败(写盘异常)。UI / 诊断可查;失败也会经 Watcher 日志通道对用户可见。
    private volatile string? _lastSaveError;
    /// <summary>最近一次 Save 的失败原因;成功后清空。null = 上次写盘正常。</summary>
    public string? LastSaveError => _lastSaveError;

    // 热重载读到半截/损坏文件时,只向仪表盘日志报一次"暂不可读、保留当前配置",避免编辑器多次保存刷屏。
    // 一旦某次重载成功(文件恢复正常),复位为可再报。仅 _gate 下读写。
    private bool _reloadUnreadableLogged;

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

    /// <summary>
    /// 写盘 + 触发 Changed。整个"读 _config + 序列化 + 写盘"在 _gate 内完成,与 Reload 的引用替换互斥,
    /// 消除"UI 改旧对象、Save 写新对象"或"序列化途中引用被热重载换掉"的窗口。
    /// 写盘失败不抛(避免 UI 调用点崩),改为记录 LastSaveError 并经 Watcher 日志让用户看见。
    /// </summary>
    public void Save()
    {
        bool ok = true;
        string? err = null;
        lock (_gate)
        {
            _selfWriteUtc = DateTime.UtcNow; // 先标记,挡掉随之而来的文件事件
            try
            {
                ConfigStore.Save(_config); // 序列化在锁内:与 Reload 替换引用互斥
                _lastSaveError = null;
            }
            catch (Exception ex)
            {
                ok = false;
                err = ex.Message;
                _lastSaveError = ex.Message;
            }
        }

        if (!ok)
        {
            // 让失败可见:走引擎日志通道(仪表盘可见),引擎未就绪时退化为 Debug。
            try { App.Engine?.LogExternal($"配置保存失败:{err}。改动可能未写入磁盘。"); }
            catch { System.Diagnostics.Debug.WriteLine($"ConfigService.Save 失败:{err}"); }
        }

        Changed?.Invoke(); // 内存 _config 已是最新,仍通知消费者(即便落盘失败,本会话内存值有效)
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_shutdown) return;
            // 自己刚写过的,忽略(文件事件可能滞后,留一个时间窗)
            if (DateTime.UtcNow - _selfWriteUtc < SelfWriteWindow) return;

            // 防抖:重置计时器,静默 300ms 后才真正重载
            _debounce?.Dispose();
            _debounce = new Timer(_ => Reload(), null, DebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// 热重载(防抖到点触发)。语义与首次启动的 ConfigStore.Load <b>刻意不同</b>:
    /// 用 <see cref="ConfigStore.TryLoad"/> 纯读——
    /// <list type="bullet">
    /// <item>读到合法配置:原子替换 _config(_gate 内,与 Save 序列化互斥),触发 Changed。</item>
    /// <item>读到 null(外部编辑器写到一半 / 文件损坏 / 暂时被占):<b>保留旧 _config 不动、不触发 Changed</b>,
    /// 仅首遇时报一条仪表盘日志(防刷屏)。文件恢复正常后,下一个文件事件自然把它重载进来。</item>
    /// </list>
    /// 旧实现走 ConfigStore.Load:它会在解析失败时 quarantine + 落默认盘,等于"外部半截写"把
    /// 运行中的好配置覆盖成默认——本次修复即消除此路径。
    /// </summary>
    private void Reload()
    {
        bool replaced = false;       // 本次是否真的换了配置(决定是否触发 Changed)
        bool needLogUnreadable = false;
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;

            if (_shutdown) return;
            // 防抖窗口结束时再核一次自写标记(防抖期间发生的 Save)
            if (DateTime.UtcNow - _selfWriteUtc < SelfWriteWindow) return;

            var fresh = ConfigStore.TryLoad();
            if (fresh is null)
            {
                // 读/解析失败:保留当前内存配置不动,不触发 Changed。只在首遇此态时安排一条日志。
                if (!_reloadUnreadableLogged)
                {
                    _reloadUnreadableLogged = true;
                    needLogUnreadable = true;
                }
                System.Diagnostics.Debug.WriteLine("ConfigService.Reload: config.json 暂不可读,保留当前配置");
            }
            else
            {
                _config = fresh;                 // 原子替换引用——在 _gate 内,与 Save 的序列化互斥
                _reloadUnreadableLogged = false; // 恢复正常后允许下次再报
                replaced = true;
            }
        }

        // 锁外发日志 / 通知,避免在 _gate 内调外部回调。
        if (needLogUnreadable)
        {
            try { App.Engine?.LogExternal("配置文件暂不可读(可能正被编辑器写入),保留当前配置。"); }
            catch { /* 引擎未就绪:已写过 Debug,放过 */ }
        }
        if (replaced)
            Changed?.Invoke();
    }

    /// <summary>
    /// 有序关闭:停文件监听与防抖计时器,之后不再触发 Reload/Changed。退出链(App.ExitApp)调用,
    /// 避免退出途中迟来的文件事件回调。幂等。
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_shutdown) return;
            _shutdown = true;
            try { _fsw.EnableRaisingEvents = false; } catch { /* ignore */ }
            try { _fsw.Dispose(); } catch { /* ignore */ }
            _debounce?.Dispose();
            _debounce = null;
        }
    }
}
