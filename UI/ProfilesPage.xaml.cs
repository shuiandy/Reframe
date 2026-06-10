using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Reframe.Core;
using Reframe.Services;

namespace Reframe.UI;

/// <summary>列表行视图模型(只读展示用,真正的数据落在 Config.Profiles 上)。</summary>
public sealed class ProfileRow
{
    public string ProfileId { get; init; } = "";
    public string Name { get; init; } = "";
    public string MatchSummary { get; init; } = "";
    public string RulesSummary { get; init; } = "";
    public bool Enabled { get; set; }
}

public sealed partial class ProfilesPage : Page
{
    // Save() 会触发 Changed,Changed 又会重建列表 —— 用此标志吞掉自己引发的回声,避免列表抖动。
    private bool _suppressReload;

    public ProfilesPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ConfigService.Instance.Changed += OnConfigChanged;
            Reload();
        };
        Unloaded += (_, _) => ConfigService.Instance.Changed -= OnConfigChanged;
    }

    private void OnConfigChanged()
    {
        if (_suppressReload) return;
        DispatcherQueue.TryEnqueue(Reload);
    }

    private void Reload()
    {
        var profiles = ConfigService.Instance.Config.Profiles;
        var rows = new List<ProfileRow>(profiles.Count);
        foreach (var p in profiles)
            rows.Add(ToRow(p));

        ProfileList.ItemsSource = rows;
        bool empty = rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ProfileList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        DeleteButton.IsEnabled = ProfileList.SelectedItem is not null;
    }

    private static ProfileRow ToRow(Profile p) => new()
    {
        ProfileId = p.Id,
        Name = string.IsNullOrWhiteSpace(p.Name) ? "未命名" : p.Name,
        Enabled = p.Enabled,
        MatchSummary = MatchSummaryOf(p),
        RulesSummary = $"{p.Rules.Count} 条规则",
    };

    private static string MatchSummaryOf(Profile p)
    {
        string label = p.MatchKind switch
        {
            MatchKind.Process => "进程名",
            MatchKind.Title => "标题包含",
            MatchKind.TitleRegex => "标题正则",
            _ => "匹配",
        };
        string val = string.IsNullOrWhiteSpace(p.MatchValue) ? "(未设置)" : p.MatchValue;
        return $"{label}: {val}";
    }

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts || ts.Tag is not string id) return;
        var profile = ConfigService.Instance.Config.Profiles.FirstOrDefault(x => x.Id == id);
        if (profile is null || profile.Enabled == ts.IsOn) return;

        profile.Enabled = ts.IsOn;

        // 禁用即还原:解除该 profile 接管的全部窗口(引擎契约 API),再落盘。
        if (!ts.IsOn)
            Reframe.App.Engine.ReleaseProfile(profile.Id);

        _suppressReload = true;
        ConfigService.Instance.Save();
        _suppressReload = false;
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => DeleteButton.IsEnabled = ProfileList.SelectedItem is not null;

    private void ProfileList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProfileRow row)
            Frame.Navigate(typeof(ProfileEditorPage), row.ProfileId);
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        var cfg = ConfigService.Instance.Config;
        var profile = new Profile
        {
            Name = "新配置文件",
            MatchKind = MatchKind.Process,
            MatchValue = "",
            Rules =
            {
                new PlacementRule
                {
                    Monitor = new MonitorFilter(),   // 任意屏
                    Kind = PlacementKind.Fullscreen, // 铺满
                },
            },
        };
        cfg.Profiles.Add(profile);

        _suppressReload = true;
        ConfigService.Instance.Save();
        _suppressReload = false;

        Frame.Navigate(typeof(ProfileEditorPage), profile.Id);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row) return;
        var cfg = ConfigService.Instance.Config;
        var profile = cfg.Profiles.FirstOrDefault(x => x.Id == row.ProfileId);
        if (profile is null) return;

        var dialog = new ContentDialog
        {
            Title = "删除配置文件",
            Content = $"确定要删除“{(string.IsNullOrWhiteSpace(profile.Name) ? "未命名" : profile.Name)}”吗?此操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        cfg.Profiles.Remove(profile);
        _suppressReload = true;
        ConfigService.Instance.Save();
        _suppressReload = false;
        Reload();
    }
}
