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

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 首次访问 Instance 即触发 Load。Engine 始终取最新配置引用。
        _ = ConfigService.Instance;
        Engine = new Watcher(() => ConfigService.Instance.Config);
        Engine.Start();

        _window = new MainWindow();
        _window.Closed += (_, _) => Engine.Stop(restoreWindows: true);
        _window.Activate();
    }
}
