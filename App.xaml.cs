using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Reframe.Core;
using Reframe.Services;

namespace Reframe;

public partial class App : Application
{
    /// <summary>转发到 ConfigService 当前配置(热重载后引用会更换,他人只读、用完即取)。</summary>
    public static AppConfig Config => ConfigService.Instance.Config;

    public static Watcher Engine { get; private set; } = null!;

    /// <summary>主窗口(OnLaunched 后非空)。页面如需直接触发可用;材质等主路径走 ConfigService.Changed。</summary>
    public static MainWindow? Main { get; private set; }

    /// <summary>全局热键服务(OnLaunched 后非空)。设置页据此查"应用后"的注册状态。</summary>
    public static HotkeyService? Hotkeys { get; private set; }

    private MainWindow? _window;
    private TrayIcon? _tray;
    private HotkeyService? _hotkeys;
    private DispatcherQueue? _ui;
    private bool _exiting;

    public App()
    {
        InitializeComponent();

        // 崩溃日志:把未处理异常落到 %LOCALAPPDATA%\Reframe\crash.log。
        // XAML/WinRT 层异常(0xc000027b stowed exception)否则只在事件查看器看到模块名,无堆栈。
        UnhandledException += (_, e) =>
        {
            LogCrash("XAML UnhandledException", e.Exception);
            // 不设 e.Handled = true:让进程照常崩,但我们已留下堆栈。
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            LogCrash("UnobservedTaskException", e.Exception);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe");
            System.IO.Directory.CreateDirectory(dir);
            string text = $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ==={Environment.NewLine}" +
                          (ex?.ToString() ?? "(null exception)") + Environment.NewLine + Environment.NewLine;
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"), text);
        }
        catch { /* 日志失败不能再抛 */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 单实例:最先做。已有实例会被唤起,本进程在此 Environment.Exit。
        if (!SingleInstance.EnsureSingle()) return;

        // 首次访问 Instance 即触发 Load。Engine 始终取最新配置引用。
        _ = ConfigService.Instance;
        Engine = new Watcher(() => ConfigService.Instance.Config);
        Engine.Start();

        // 拖拽吸附:按住修饰键拖窗口 → 分区覆盖层 → 松手入位。内部自管线程/钩子。
        DragSnapService.Start(() => ConfigService.Instance.Config);

        // 配置变化(UI 保存 / 外部改 config.json)→ 立刻重写 Unity 分辨率预设(游戏多半未运行,写了即生效)。
        ConfigService.Instance.Changed += () => Engine?.OnConfigChanged();

        _window = new MainWindow();
        Main = _window;
        _ui = _window.DispatcherQueue;

        // 点 X 不退出:取消关闭、隐藏到托盘,引擎继续跑。退出只走托盘菜单。
        _window.AppWindow.Closing += OnAppWindowClosing;

        _window.Activate();

        // 全局热键统管(自带消息窗口线程):去框/还原、送窗口入分区。配置变化自动重注册。
        _hotkeys = new HotkeyService();
        Hotkeys = _hotkeys;
        _hotkeys.Start(_ui!, () => ConfigService.Instance.Config);

        // 托盘常驻。回调都切回 UI 线程执行。
        _tray = new TrayIcon
        {
            OnOpen = () => _ui!.TryEnqueue(ShowMainWindow),
            OnToggleEngine = on => _ui!.TryEnqueue(() => SetEngineEnabled(on)),
            OnExit = () => _ui!.TryEnqueue(ExitApp),
            EngineEnabledProvider = () => ConfigService.Instance.Config.EngineEnabled,
        };
        _tray.Start(tooltip: "Reframe");
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        if (_exiting) return;       // 真正退出时放行
        e.Cancel = true;            // 拦下关闭
        sender.Hide();              // 隐藏到托盘
    }

    /// <summary>托盘左键 / 菜单"打开":显示并激活主窗口。</summary>
    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.AppWindow.Show();
        _window.Activate();
        WindowActivation.BringToFront(_window);
    }

    /// <summary>切换引擎启用(配置项),写盘。Watcher.SafeTick 据此即时生效。</summary>
    private void SetEngineEnabled(bool on)
    {
        var cfg = ConfigService.Instance.Config;
        if (cfg.EngineEnabled == on) return;
        cfg.EngineEnabled = on;
        ConfigService.Instance.Save();
    }

    /// <summary>真正退出:还原全部接管窗口 + 移除托盘 + 退出。只由托盘"退出"触发。</summary>
    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        try { DragSnapService.Stop(); } catch { /* 先停吸附钩子,再拆引擎 */ }
        try { _hotkeys?.Stop(); } catch { /* 注销全部热键 */ }
        try { Engine?.Stop(restoreWindows: true); } catch { /* 尽力还原 */ }
        try { _tray?.Dispose(); } catch { /* ignore */ }   // 在 UI 线程 Dispose,不会自 join 托盘线程

        Exit(); // Application.Exit
    }
}
