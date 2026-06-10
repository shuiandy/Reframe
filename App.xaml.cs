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

    private MainWindow? _window;
    private TrayIcon? _tray;
    private DispatcherQueue? _ui;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
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
        _ui = _window.DispatcherQueue;

        // 点 X 不退出:取消关闭、隐藏到托盘,引擎继续跑。退出只走托盘菜单。
        _window.AppWindow.Closing += OnAppWindowClosing;

        _window.Activate();

        // 托盘常驻。回调都切回 UI 线程执行。Ctrl+Alt+B 全局热键:前台窗口去框/还原(toggle)。
        _tray = new TrayIcon
        {
            OnOpen = () => _ui!.TryEnqueue(ShowMainWindow),
            OnToggleEngine = on => _ui!.TryEnqueue(() => SetEngineEnabled(on)),
            OnExit = () => _ui!.TryEnqueue(ExitApp),
            EngineEnabledProvider = () => ConfigService.Instance.Config.EngineEnabled,
            OnHotkey = () => _ui!.TryEnqueue(ToggleForegroundBorderless),
        };
        _tray.Start(
            tooltip: "Reframe",
            registerHotkey: true,
            hotkeyMods: TrayIcon.MOD_CONTROL | TrayIcon.MOD_ALT | TrayIcon.MOD_NOREPEAT,
            hotkeyVk: TrayIcon.VK_B);
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
        try { Engine?.Stop(restoreWindows: true); } catch { /* 尽力还原 */ }
        try { _tray?.Dispose(); } catch { /* ignore */ }   // 在 UI 线程 Dispose,不会自 join 托盘线程

        Exit(); // Application.Exit
    }

    /// <summary>全局热键:对当前前台窗口切换无边框(已接管→还原,未接管→去框)。</summary>
    private void ToggleForegroundBorderless()
    {
        IntPtr h = WindowActivation.GetForeground();
        if (h == IntPtr.Zero) return;
        if (WindowOps.IsTracked(h))
        {
            WindowOps.Restore(h);
        }
        else
        {
            // 只去边框、不动几何、不置顶;快照由 Apply 内部自动留存。
            var target = new PlacementResolver.Target(MakeBorderless: true, Rect: null, Topmost: false);
            WindowOps.Apply(h, in target);
        }
    }
}
