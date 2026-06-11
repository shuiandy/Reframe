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

        // Modern title bar: extend content into the title-bar area (Mica shows through to the top), with
        // the Row0 drag region AppTitleDragRegion acting as the draggable title bar.
        // The right-side system buttons (minimize/maximize/close) are kept automatically by the system;
        // dragging / double-click-to-maximize is handled by SetTitleBar.
        // A true two-row layout: Row0 is the physical title bar (custom-drawn hamburger + icon + title +
        // drag region), and Row1 is the NavigationView.
        // The custom hamburger is outside the drag region, so it receives Click normally (interactive
        // controls inside a SetTitleBar element don't get input).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleDragRegion);
        InitTitleBarIcon();
        InitLocalizedAccessibility();
        // The title text greys out when deactivated and restores when activated (matching the Win11 Settings app).
        Activated += OnActivated;

        // Background material: chosen by config (Mica / Mica Alt / Acrylic), natively supported by
        // WinAppSDK 1.8 + Win11, no fallback needed.
        // The NavigationView Pane/content background brushes are set transparent in App.xaml so the
        // material shows through; after the title bar is extended, Mica shows through there too.
        ApplyBackdrop();

        // Theme (dark mode): chosen by config (follow system / light / dark), tracking the system light/dark in real time.
        ApplyTheme();

        // Config change (Settings page changes the material / external config.json hot reload) → marshal
        // back to the UI thread and re-apply. MainWindow's lifetime = the whole app, so don't unsubscribe.
        ConfigService.Instance.Changed += OnConfigChanged;

        // Top-left window + taskbar icon. Unpackaged, ApplicationIcon doesn't flow to AppWindow
        // automatically, so load it explicitly from the output directory (csproj already sets CopyToOutputDirectory).
        TrySetWindowIcon();

        // Window has no Width/Height, so use AppWindow.Resize (physical pixels).
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));

        // Default to the dashboard. Setting SelectedItem fires SelectionChanged → which does the Navigate uniformly.
        Nav.SelectedItem = Nav.MenuItems[0];
        // Fallback: if setting the selected item didn't trigger navigation (already selected, etc.), navigate once manually.
        if (ContentFrame.Content is null)
            ContentFrame.Navigate(typeof(DashboardPage));
    }

    /// <summary>Set the window background material from the current config. Takes effect immediately; call on the UI thread.</summary>
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
    /// Set the app theme (dark mode) from the current config. Takes effect immediately; call on the UI thread.
    /// System → ElementTheme.Default: follow the system light/dark automatically and respond to system switches in real time.
    /// Under ExtendsContentIntoTitleBar, the top-right system buttons' (minimize/maximize/close) foreground
    /// color doesn't adapt to ElementTheme automatically, so set an explicit set of TitleBar button colors
    /// from the effective theme here, ensuring they're legible in both light and dark.
    /// </summary>
    public void ApplyTheme()
    {
        if (Content is not FrameworkElement root) return;

        root.RequestedTheme = ConfigService.Instance.Config.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default, // System: follow the system, respond to switches in real time
        };

        ApplyTitleBarButtonColors(root);
    }

    /// <summary>
    /// Color the title-bar system buttons from the current effective theme. For System (Default), read
    /// the actual system light/dark via root.ActualTheme. Light theme uses dark text, dark theme uses
    /// light text; hover/pressed backgrounds use translucent grays, matching the Win11 look.
    /// </summary>
    private void ApplyTitleBarButtonColors(FrameworkElement root)
    {
        var titleBar = AppWindow.TitleBar;

        // With RequestedTheme=Default, ActualTheme reflects the actual system light/dark; for Light/Dark it's the chosen value.
        bool dark = root.ActualTheme == ElementTheme.Dark;

        var fg     = dark ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                          : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        var disabled = dark ? Windows.UI.Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E)
                            : Windows.UI.Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E);
        // hover/pressed use a low-opacity overlay of the theme text color (light gray in dark mode, dark gray in light mode).
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

    /// <summary>Config-change callback (any thread). Marshal back to the UI thread and re-apply the material and theme.</summary>
    private void OnConfigChanged()
        => DispatcherQueue.TryEnqueue(() => { ApplyBackdrop(); ApplyTheme(); });

    /// <summary>
    /// Set the localized tooltip and accessibility name on the custom-drawn hamburger button (fetched
    /// from MainWindow.resw via Loc). Done in code rather than XAML x:Uid: resw x:Uid for attached
    /// properties (ToolTipService/AutomationProperties) is brittle under MRT Core.
    /// </summary>
    private void InitLocalizedAccessibility()
    {
        string tip = Loc.T("MainWindow/NavButtonTooltip");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PaneToggleButton, tip);
        ToolTipService.SetToolTip(PaneToggleButton, tip);
    }

    /// <summary>Load Assets\reframe.ico into the 16px icon at the left of the title bar; leave it blank silently if the file is missing.</summary>
    private void InitTitleBarIcon()
    {
        try
        {
            string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "reframe.ico");
            if (System.IO.File.Exists(ico))
                TitleBarIcon.Source = new BitmapImage(new System.Uri(ico));
        }
        catch { /* the icon is non-critical, ignore */ }
    }

    /// <summary>The title text greys out when the window is deactivated and restores when re-activated (the standard Win11 look).</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        AppTitleText.Foreground = args.WindowActivationState == WindowActivationState.Deactivated
            ? (Brush)Application.Current.Resources["TextFillColorDisabledBrush"]
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    /// <summary>Row0 custom-drawn hamburger: open/close the NavigationView pane (replacing the hidden built-in PaneToggleButton).</summary>
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

    /// <summary>Set the window icon from Assets\reframe.ico in the output directory; skip silently if the file is missing (don't block startup).</summary>
    private void TrySetWindowIcon()
    {
        try
        {
            string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "reframe.ico");
            if (System.IO.File.Exists(ico))
                AppWindow.SetIcon(ico);
        }
        catch { /* the icon is non-critical, ignore */ }
    }
}
