using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reframe.UI;

namespace Reframe;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Window 没有 Width/Height,用 AppWindow.Resize(物理像素)。
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));

        // 默认进仪表盘。设 SelectedItem 会触发 SelectionChanged → 由它统一 Navigate。
        Nav.SelectedItem = Nav.MenuItems[0];
        // 兜底:若设选中项未触发导航(已是选中项等),手动进一次。
        if (ContentFrame.Content is null)
            ContentFrame.Navigate(typeof(DashboardPage));
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
}
