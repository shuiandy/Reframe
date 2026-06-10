using Microsoft.UI.Xaml;
using Reframe.Core;
using Reframe.Services;

namespace Reframe;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = null!;
    public static Watcher Engine { get; private set; } = null!;

    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Config = ConfigStore.Load();
        Engine = new Watcher(() => Config);
        Engine.Start();

        _window = new MainWindow();
        _window.Closed += (_, _) => Engine.Stop(restoreWindows: true);
        _window.Activate();
    }
}
