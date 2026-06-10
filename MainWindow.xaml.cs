using Microsoft.UI.Xaml;
using Reframe.Services;

namespace Reframe;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var cfg = App.Config;
        EngineToggle.IsOn = cfg.EngineEnabled;
        SummaryText.Text = $"{cfg.Profiles.Count} 个配置文件 · {cfg.Layouts.Count} 个布局 · 配置: {ConfigStore.Path_}";

        App.Engine.Log += msg => DispatcherQueue.TryEnqueue(() =>
        {
            LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
            while (LogList.Items.Count > 200)
                LogList.Items.RemoveAt(LogList.Items.Count - 1);
        });
    }

    private void EngineToggle_Toggled(object sender, RoutedEventArgs e)
    {
        App.Config.EngineEnabled = EngineToggle.IsOn;
        ConfigStore.Save(App.Config);
    }
}
