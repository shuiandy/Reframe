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

        // 现代标题栏:把内容延伸进标题栏区(Mica 透到顶),Row0 拖拽区 AppTitleDragRegion 作为可拖拽标题栏。
        // 右侧系统按钮(最小化/最大化/关闭)由系统自动保留;拖动 / 双击最大化由 SetTitleBar 接管。
        // 真两行布局:Row0 实体标题栏(自绘汉堡 + 图标 + 标题 + 拖拽区),Row1 才是 NavigationView。
        // 自绘汉堡在拖拽区之外,故能正常收到 Click(SetTitleBar 元素内的交互控件收不到输入)。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleDragRegion);
        InitTitleBarIcon();
        // 失活时标题文字变灰,激活时恢复(对标 Win11 设置应用)。
        Activated += OnActivated;

        // 背景材质:由配置决定(云母 / 云母变体 / 亚克力),WinAppSDK 1.8 + Win11 原生支持,无需 fallback。
        // NavigationView 的 Pane/内容背景刷在 App.xaml 已设透明,材质得以透出;标题栏延伸后也透云母。
        ApplyBackdrop();

        // 主题(夜间模式):由配置决定(跟随系统 / 浅色 / 深色),实时跟随系统明暗。
        ApplyTheme();

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

    /// <summary>
    /// 按当前配置设置应用主题(夜间模式)。改即生效;须在 UI 线程调用。
    /// System → ElementTheme.Default:自动跟随系统明暗,并实时响应系统切换。
    /// ExtendsContentIntoTitleBar 下,右上系统按钮(最小化/最大化/关闭)的前景色不会随 ElementTheme
    /// 自动适配,这里据最终生效主题显式给 TitleBar 配一组按钮颜色,保证深浅两态都看得清。
    /// </summary>
    public void ApplyTheme()
    {
        if (Content is not FrameworkElement root) return;

        root.RequestedTheme = ConfigService.Instance.Config.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default, // System:跟随系统,实时响应切换
        };

        ApplyTitleBarButtonColors(root);
    }

    /// <summary>
    /// 据当前实际主题给标题栏系统按钮上色。System(Default)时按 root.ActualTheme 读出系统实际明暗。
    /// 浅色主题用深字、深色主题用浅字;hover/pressed 背景用半透明灰阶,与 Win11 观感一致。
    /// </summary>
    private void ApplyTitleBarButtonColors(FrameworkElement root)
    {
        var titleBar = AppWindow.TitleBar;

        // RequestedTheme=Default 时 ActualTheme 反映系统实际明暗;Light/Dark 时即所选值。
        bool dark = root.ActualTheme == ElementTheme.Dark;

        var fg     = dark ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                          : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        var disabled = dark ? Windows.UI.Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E)
                            : Windows.UI.Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E);
        // hover/pressed 用主题字色的低透明度叠色(深色态浅灰、浅色态深灰)。
        var hoverBg   = dark ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
                             : Windows.UI.Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        var pressedBg = dark ? Windows.UI.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)
                             : Windows.UI.Color.FromArgb(0x0A, 0x00, 0x00, 0x00);
        var transparent = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);

        titleBar.ButtonForegroundColor         = fg;
        titleBar.ButtonHoverForegroundColor    = fg;
        titleBar.ButtonPressedForegroundColor  = fg;
        titleBar.ButtonInactiveForegroundColor = disabled;

        titleBar.ButtonBackgroundColor         = transparent;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonHoverBackgroundColor    = hoverBg;
        titleBar.ButtonPressedBackgroundColor  = pressedBg;
    }

    /// <summary>配置变更回调(任意线程)。切回 UI 线程重新应用材质与主题。</summary>
    private void OnConfigChanged()
        => DispatcherQueue.TryEnqueue(() => { ApplyBackdrop(); ApplyTheme(); });

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

    /// <summary>Row0 自绘汉堡:开/合 NavigationView 侧栏(替代已隐藏的自带 PaneToggleButton)。</summary>
    private void PaneToggle_Click(object sender, RoutedEventArgs e)
        => Nav.IsPaneOpen = !Nav.IsPaneOpen;

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
