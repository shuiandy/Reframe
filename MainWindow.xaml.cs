using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Reframe.Core;
using Reframe.Services;
using Reframe.UI;

namespace Reframe;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 现代标题栏:把内容延伸进标题栏区,自定义元素 AppTitleBar 作为可拖拽标题栏。
        // 右侧系统按钮(最小化/最大化/关闭)由系统自动保留;拖动 / 双击最大化由 SetTitleBar 接管。
        // NavigationView 铺满整窗,其汉堡(PaneToggle)按钮因此落在窗口最左上角。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        InitTitleBarIcon();
        // 失活时标题文字变灰,激活时恢复(对标 Win11 设置应用)。
        Activated += OnActivated;

        // 背景材质:由配置决定(云母 / 云母变体 / 亚克力),WinAppSDK 1.8 + Win11 原生支持,无需 fallback。
        // NavigationView 的 Pane/内容背景刷在 App.xaml 已设透明,材质得以透出;标题栏延伸后也透云母。
        ApplyBackdrop();

        // 配置变化(设置页改材质 / 外部热重载 config.json)→ 切回 UI 线程重新应用。
        // MainWindow 生命周期 = 应用全程,不退订。
        ConfigService.Instance.Changed += OnConfigChanged;

        // 窗口左上角 + 任务栏图标。unpackaged 下 ApplicationIcon 不会自动落到 AppWindow,
        // 显式从输出目录加载(csproj 已配 CopyToOutputDirectory)。
        TrySetWindowIcon();

        // Window 没有 Width/Height,用 AppWindow.Resize(物理像素)。
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));

        // 默认进仪表盘。设 SelectedItem 会触发 SelectionChanged → 由它统一 Navigate。
        Nav.SelectedItem = Nav.MenuItems[0];
        // 兜底:若设选中项未触发导航(已是选中项等),手动进一次。
        if (ContentFrame.Content is null)
            ContentFrame.Navigate(typeof(DashboardPage));
    }

    /// <summary>按当前配置设置窗口背景材质。改即生效;须在 UI 线程调用。</summary>
    public void ApplyBackdrop()
    {
        SystemBackdrop = ConfigService.Instance.Config.Backdrop switch
        {
            BackdropKind.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            BackdropKind.Acrylic => new DesktopAcrylicBackdrop(),
            _                    => new MicaBackdrop { Kind = MicaKind.Base },
        };
    }

    /// <summary>配置变更回调(任意线程)。切回 UI 线程重新应用材质。</summary>
    private void OnConfigChanged()
        => DispatcherQueue.TryEnqueue(ApplyBackdrop);

    /// <summary>把 Assets\reframe.ico 加载进标题栏左侧 16px 图标;缺文件时静默留空。</summary>
    private void InitTitleBarIcon()
    {
        try
        {
            string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "reframe.ico");
            if (System.IO.File.Exists(ico))
                TitleBarIcon.Source = new BitmapImage(new System.Uri(ico));
        }
        catch { /* 图标非关键,忽略 */ }
    }

    /// <summary>窗口失活时标题文字变灰,重新激活时恢复(Win11 标准观感)。</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        AppTitleText.Foreground = args.WindowActivationState == WindowActivationState.Deactivated
            ? (Brush)Application.Current.Resources["TextFillColorDisabledBrush"]
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        var pageType = (item.Tag as string) switch
        {
            "dashboard" => typeof(DashboardPage),
            "profiles"  => typeof(ProfilesPage),
            "layouts"   => typeof(LayoutsPage),
            "settings"  => typeof(SettingsPage),
            _ => null
        };

        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType);
    }

    /// <summary>从输出目录的 Assets\reframe.ico 设窗口图标;缺文件时静默跳过(不阻断启动)。</summary>
    private void TrySetWindowIcon()
    {
        try
        {
            string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "reframe.ico");
            if (System.IO.File.Exists(ico))
                AppWindow.SetIcon(ico);
        }
        catch { /* 图标非关键,忽略 */ }
    }
}
