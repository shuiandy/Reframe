using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Reframe.Core;
using Reframe.Services;

namespace Reframe.UI;

/// <summary>列表行视图模型(只读展示用,真正的数据落在 Config.Profiles 上)。</summary>
public sealed partial class ProfileRow : System.ComponentModel.INotifyPropertyChanged
{
    public string ProfileId { get; init; } = "";
    public string Name { get; init; } = "";
    public string MatchSummary { get; init; } = "";
    public string RulesSummary { get; init; } = "";
    public bool Enabled { get; set; }

    // MatchKind=Process 用 IconCache 取进程图标;其它匹配方式无进程可言 → 一律默认字形。
    // 图标异步回填(Reload 时先 null,Task.Run 提取后切回 UI 线程 set),故用通知属性。
    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new(nameof(Icon)));
            PropertyChanged?.Invoke(this, new(nameof(RealIconVisibility)));
            PropertyChanged?.Invoke(this, new(nameof(FallbackIconVisibility)));
        }
    }

    /// <summary>进程匹配且需要尝试取图标时,存其进程名(不含 .exe);否则 null = 始终默认字形。</summary>
    public string? ProcessNameForIcon { get; init; }

    public Visibility RealIconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackIconVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
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
        UpdateCommandState();

        LoadIconsAsync(rows);
    }

    // 图标异步提取,不阻塞列表构建:后台线程做可能慢的路径解析(预热),再切回 UI 线程构建/回填位图
    // (WriteableBitmap 必须在 UI 线程创建;预热后这一跳几乎只剩内存命中 + 一次 GetDIBits,很快)。
    private void LoadIconsAsync(List<ProfileRow> rows)
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.ProcessNameForIcon)) continue;
            string proc = row.ProcessNameForIcon;
            _ = Task.Run(() =>
            {
                IconCache.PrewarmByProcessName(proc);
                DispatcherQueue.TryEnqueue(() => row.Icon = IconCache.ByProcessName(proc));
            });
        }
    }

    private static ProfileRow ToRow(Profile p) => new()
    {
        ProfileId = p.Id,
        Name = string.IsNullOrWhiteSpace(p.Name) ? "未命名" : p.Name,
        Enabled = p.Enabled,
        MatchSummary = MatchSummaryOf(p),
        RulesSummary = $"{p.Rules.Count} 条规则",
        ProcessNameForIcon = p.MatchKind == MatchKind.Process && !string.IsNullOrWhiteSpace(p.MatchValue)
            ? p.MatchValue
            : null,
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
        => UpdateCommandState();

    private void UpdateCommandState()
    {
        bool has = ProfileList.SelectedItem is not null;
        EditButton.IsEnabled = has;
        DeleteButton.IsEnabled = has;
    }

    private ProfileRow? SelectedRow => ProfileList.SelectedItem as ProfileRow;

    // 双击 = 进入编辑器。点 ToggleSwitch 上的双击不应导航(开关有自己的行为)。
    private void ProfileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsWithinToggle(e.OriginalSource as DependencyObject)) return;
        var row = RowFromEventSource(e.OriginalSource as DependencyObject);
        if (row is not null)
            Frame.Navigate(typeof(ProfileEditorPage), row.ProfileId);
    }

    // 右键 = 在 ContextFlyout 打开前选中该行,使编辑/删除落到正确项;在开关上右键不弹菜单。
    private void ProfileRow_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (IsWithinToggle(e.OriginalSource as DependencyObject))
        {
            e.Handled = true; // 吞掉,避免在开关上误弹行菜单
            return;
        }
        if ((sender as FrameworkElement)?.DataContext is ProfileRow row)
            ProfileList.SelectedItem = row;
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
            Frame.Navigate(typeof(ProfileEditorPage), row.ProfileId);
    }

    private static bool IsWithinToggle(DependencyObject? src)
    {
        while (src is not null && src is not ListViewItem)
        {
            if (src is ToggleSwitch) return true;
            src = VisualTreeHelper.GetParent(src);
        }
        return false;
    }

    private static ProfileRow? RowFromEventSource(DependencyObject? src)
    {
        while (src is not null && src is not ListViewItem)
            src = VisualTreeHelper.GetParent(src);
        return (src as ListViewItem)?.Content as ProfileRow;
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

    private async void FromWindowButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new WindowPickerDialog { XamlRoot = XamlRoot };
        if (await picker.ShowAsync() != ContentDialogResult.Primary) return;
        if (picker.SelectedWindow is not { } w) return;

        var cfg = ConfigService.Instance.Config;

        // 进程名:WindowScanner 给的是不含 .exe 的小写;配置里统一存 .exe(与默认配置一致,匹配端会 StripExe)。
        string proc = string.IsNullOrEmpty(w.ProcessName) ? "" : w.ProcessName;
        string matchValue = string.IsNullOrEmpty(proc) ? "" : proc + ".exe";

        // 同进程名已有 profile → 确认是否再建一个。
        if (!string.IsNullOrEmpty(proc))
        {
            var dup = cfg.Profiles.FirstOrDefault(p =>
                p.MatchKind == MatchKind.Process &&
                string.Equals(StripExe(p.MatchValue), proc, StringComparison.OrdinalIgnoreCase));
            if (dup is not null)
            {
                var confirm = new ContentDialog
                {
                    Title = "已有针对该进程的配置",
                    Content = $"已有针对该进程的配置“{(string.IsNullOrWhiteSpace(dup.Name) ? "未命名" : dup.Name)}”,仍要再建一个吗?",
                    PrimaryButtonText = "仍要新建",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            }
        }

        var profile = new Profile
        {
            Name = Truncate(w.Title, 40),
            MatchKind = MatchKind.Process,
            MatchValue = matchValue,
            DelayMs = 1000,
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

    private static string StripExe(string s)
        => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;

    private static string Truncate(string s, int max)
    {
        s = string.IsNullOrWhiteSpace(s) ? "未命名" : s.Trim();
        return s.Length <= max ? s : s[..max];
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
