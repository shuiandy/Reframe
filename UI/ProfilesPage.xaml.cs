using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Reframe.Core;
using Reframe.Interop;
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

    /// <summary>有启动命令或可执行文件才可一键启动(否则「启动」按钮禁用)。</summary>
    public bool CanLaunch { get; init; }

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

/// <summary>
/// 左栏「运行中的窗口」一行。按 Handle 复用(见 RefreshWindows 的 diff):身份字段(Handle/ProcessId)
/// 不变,文本字段与 Icon 经 INotifyPropertyChanged 原地更新,既不重建集合也不闪图标。x:Bind 需要顶层 public 类。
/// </summary>
public sealed partial class WindowRow : System.ComponentModel.INotifyPropertyChanged
{
    public IntPtr Handle { get; init; }
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";   // 不含 .exe,小写;建 profile / 黑名单用

    private string _title = "";
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Raise(nameof(Title)); } }
    }

    // 次要灰字:进程 exe 完整路径(取得到),否则回落 "进程名.exe"。
    private string _pathLabel = "";
    public string PathLabel
    {
        get => _pathLabel;
        set { if (_pathLabel != value) { _pathLabel = value; Raise(nameof(PathLabel)); } }
    }

    private string _sizeLabel = "";
    public string SizeLabel
    {
        get => _sizeLabel;
        set { if (_sizeLabel != value) { _sizeLabel = value; Raise(nameof(SizeLabel)); } }
    }

    // 同进程已有配置 → 行尾显示「已有配置」灰字。随配置增删原地更新。
    private bool _hasProfile;
    public bool HasProfile
    {
        get => _hasProfile;
        set { if (_hasProfile != value) { _hasProfile = value; Raise(nameof(HasProfile)); Raise(nameof(HasProfileVisibility)); } }
    }

    public Visibility HasProfileVisibility => HasProfile ? Visibility.Visible : Visibility.Collapsed;

    // 图标:同步命中(TryGetCached/ByProcessId)即刻设;未命中后台预热再回填(回填到复用的行对象上,此后不再闪)。
    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            Raise(nameof(Icon));
            Raise(nameof(RealIconVisibility));
            Raise(nameof(FallbackIconVisibility));
        }
    }

    public Visibility RealIconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackIconVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string prop) => PropertyChanged?.Invoke(this, new(prop));
}

public sealed partial class ProfilesPage : Page
{
    // Save() 会触发 Changed,Changed 又会重建列表 —— 用此标志吞掉自己引发的回声,避免列表抖动。
    private bool _suppressReload;

    // 左栏窗口列表:_windows 是全量持久集合,按 Handle 复用做增量 diff(参考 DashboardPage),
    // 行对象(WindowRow)身份稳定、Icon 不闪。_windowsView 是绑给 ListView 的"过滤视图",
    // 只持有 _windows 里同一批行对象的引用子集(按搜索框命中);切换可见性靠增删视图成员而非容器可见性
    // (容器可见性在虚拟化/未实现容器时不可靠,会误判空)。行对象共享 → 视图增删不重置 Icon。
    private readonly List<WindowRow> _windows = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<WindowRow> _windowsView = new();
    private readonly DispatcherTimer _windowTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public ProfilesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ConfigService.Instance.Changed += OnConfigChanged;
        Reload();

        WindowList.ItemsSource = _windowsView; // 过滤视图,只设一次
        RefreshWindows();
        _windowTimer.Tick += WindowTimer_Tick;
        _windowTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ConfigService.Instance.Changed -= OnConfigChanged;
        _windowTimer.Stop();
        _windowTimer.Tick -= WindowTimer_Tick;
    }

    private void OnConfigChanged()
    {
        if (_suppressReload) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            Reload();
            ApplyWindowFilter(); // 配置增删 → 左栏「已有配置」标记需重算
        });
    }

    // ======================== 右栏:配置列表 ========================

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

    // 图标加载(与仪表盘一致的优先级:ExePath → 内存快取 → 本地链路 → SteamGridDB 兜底)。
    // 列表非定时重建(仅 Reload 时),无闪烁问题;但仍走"同步快取先取、未命中再异步"以即时显示已缓存图标。
    private void LoadIconsAsync(List<ProfileRow> rows)
    {
        var cfg = ConfigService.Instance.Config;
        string? apiKey = cfg.SteamGridDbApiKey;

        foreach (var row in rows)
        {
            var profile = cfg.Profiles.FirstOrDefault(p => p.Id == row.ProfileId);
            if (profile is null) continue;

            // 同步快取:已配 ExePath / 内存已命中 → 立即显示,不走异步。
            if (!string.IsNullOrWhiteSpace(profile.ExePath))
            {
                var icon = IconCache.ByProfile(profile);
                if (icon is not null) { row.Icon = icon; continue; }
            }
            if (!string.IsNullOrEmpty(row.ProcessNameForIcon)
                && IconCache.TryGetCached(row.ProcessNameForIcon, out var hit) && hit is not null)
            {
                row.Icon = hit;
                continue;
            }
            if (string.IsNullOrEmpty(row.ProcessNameForIcon)) continue;

            string proc = row.ProcessNameForIcon;
            var target = row;
            _ = Task.Run(async () =>
            {
                IconCache.PrewarmByProcessName(proc);
                DispatcherQueue.TryEnqueue(() => target.Icon ??= IconCache.ByProfile(profile));
                // 本地全失败 → SteamGridDB 在线兜底(配了 key 才走),成功后回填。
                if (await IconCache.PrewarmFromSteamGridDbAsync(apiKey, profile).ConfigureAwait(false))
                    DispatcherQueue.TryEnqueue(() => target.Icon ??= IconCache.ByProfile(profile));
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
        CanLaunch = !string.IsNullOrWhiteSpace(p.LaunchCommand) || !string.IsNullOrWhiteSpace(p.ExePath),
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

    // 行内「启动」按钮:据 Tag(ProfileId)定位 Profile,调 GameLauncher.Launch。
    // 失败(没配启动方式/文件不存在/已在运行/异常)用 ContentDialog 显示 error。
    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var profile = ConfigService.Instance.Config.Profiles.FirstOrDefault(x => x.Id == id);
        if (profile is null) return;

        if (GameLauncher.Launch(profile, out var error)) return;

        var dialog = new ContentDialog
        {
            Title = "无法启动",
            Content = error ?? "启动失败。",
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
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

    // 双击 = 进入编辑器。点开关/启动按钮上的双击不应导航(它们有自己的行为)。
    private void ProfileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsWithinInteractiveControl(e.OriginalSource as DependencyObject)) return;
        var row = RowFromEventSource(e.OriginalSource as DependencyObject);
        if (row is not null)
            Frame.Navigate(typeof(ProfileEditorPage), row.ProfileId);
    }

    // 右键 = 在 ContextFlyout 打开前选中该行,使编辑/删除落到正确项;在开关/按钮上右键不弹菜单。
    private void ProfileRow_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (IsWithinInteractiveControl(e.OriginalSource as DependencyObject))
        {
            e.Handled = true; // 吞掉,避免在开关/按钮上误弹行菜单
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

    // 行内交互控件(开关/按钮)上的双击/右键不应触发行级导航或菜单——它们各有行为。
    private static bool IsWithinInteractiveControl(DependencyObject? src)
    {
        while (src is not null && src is not ListViewItem)
        {
            if (src is ToggleSwitch or Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return true;
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
        ApplyWindowFilter(); // 左栏「已有配置」标记需重算
    }

    // ======================== 左栏:运行中的窗口 ========================

    private void WindowTimer_Tick(object? sender, object e) => RefreshWindows();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyWindowFilter();

    /// <summary>
    /// 刷新左栏窗口列表:扫描候选窗口(已剔除 cloaked / 小窗 / 系统外壳),按 Handle 对 _windows
    /// 做增量 diff(消失的删、新增的加、仍在的原地更新),不整列重建避免闪烁。随后按搜索框过滤可见项。
    /// </summary>
    private void RefreshWindows()
    {
        var scanned = WindowScanner.EnumerateCandidates();
        var live = new HashSet<IntPtr>(scanned.Select(w => w.Handle));

        // 删:已不在扫描结果中的句柄。
        for (int i = _windows.Count - 1; i >= 0; i--)
            if (!live.Contains(_windows[i].Handle))
                _windows.RemoveAt(i);

        foreach (var w in scanned)
        {
            string sizeLabel = w.Width > 0 && w.Height > 0 ? $"{w.Width}×{w.Height}" : "";
            bool hasProfile = HasProfileForProcess(w.ProcessName);

            var existing = _windows.FirstOrDefault(r => r.Handle == w.Handle);
            if (existing is null)
            {
                var row = new WindowRow
                {
                    Handle = w.Handle,
                    ProcessId = w.ProcessId,
                    ProcessName = w.ProcessName,
                    Title = w.Title,
                    PathLabel = PathLabelOf(w),
                    SizeLabel = sizeLabel,
                    HasProfile = hasProfile,
                    Icon = IconCache.ByProcessId(w.ProcessId), // 在跑的窗口优先用 pid 入口(精确且顺便学路径)
                };
                _windows.Add(row);
            }
            else
            {
                // 原地更新会变的字段(标题/尺寸/已有配置标记);Icon 已有则保留不闪。
                existing.Title = w.Title;
                existing.SizeLabel = sizeLabel;
                existing.HasProfile = hasProfile;
                if (existing.Icon is null)
                    existing.Icon = IconCache.ByProcessId(w.ProcessId);
            }
        }

        ApplyWindowFilter();
    }

    /// <summary>
    /// 按搜索框文本把 _windows 的命中子集同步进 _windowsView(原地增删,保持顺序)。
    /// _windows 与 _windowsView 共享同一批 WindowRow 实例,故视图增删不重置 Icon/状态,不闪。
    /// </summary>
    private void ApplyWindowFilter()
    {
        string q = (SearchBox?.Text ?? "").Trim();

        // 同步「已有配置」标记(配置增删后调用,扫描间隔外也能即时反映)。
        foreach (var row in _windows)
            row.HasProfile = HasProfileForProcess(row.ProcessName);

        bool Match(WindowRow r) => q.Length == 0
            || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (r.ProcessName + ".exe").Contains(q, StringComparison.OrdinalIgnoreCase);

        var desired = _windows.Where(Match).ToList();

        // 删:视图里已不该出现的。
        for (int i = _windowsView.Count - 1; i >= 0; i--)
            if (!desired.Contains(_windowsView[i]))
                _windowsView.RemoveAt(i);

        // 增/排序:按 desired 顺序就位(原地移动/插入,引用相等不动)。
        for (int i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            int cur = _windowsView.IndexOf(row);
            if (cur < 0) _windowsView.Insert(i, row);
            else if (cur != i) _windowsView.Move(cur, i);
        }

        bool empty = desired.Count == 0;
        WindowEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        WindowList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>进程 exe 完整路径(取得到),否则回落 "进程名.exe";进程名也空则 "(未知进程)"。</summary>
    private static string PathLabelOf(WindowInfo w)
    {
        string? path = IconCache.TryResolveExePath(w.ProcessId);
        if (!string.IsNullOrEmpty(path)) return path;
        return string.IsNullOrEmpty(w.ProcessName) ? "(未知进程)" : w.ProcessName + ".exe";
    }

    /// <summary>是否已有针对该进程名(MatchKind=Process)的配置。</summary>
    private static bool HasProfileForProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        return ConfigService.Instance.Config.Profiles.Any(p =>
            p.MatchKind == MatchKind.Process &&
            string.Equals(StripExe(p.MatchValue), processName, StringComparison.OrdinalIgnoreCase));
    }

    private static WindowRow? WindowRowFromSender(object sender)
        => (sender as FrameworkElement)?.DataContext as WindowRow;

    // 「+ 创建配置」(行主按钮 / 右键菜单项):沿用对话框版逻辑——
    // Name=标题截断、MatchKind=Process、预置任意屏→铺满规则、重复进程确认框、Save 后导航到编辑页。
    private async void CreateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (WindowRowFromSender(sender) is not { } w) return;

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

    // 右键「立即去边框」:对该窗口句柄临时去边框(不入配置)。已被跟踪则还原(切换语义)。
    private void QuickBorderless_Click(object sender, RoutedEventArgs e)
    {
        if (WindowRowFromSender(sender) is not { } w) return;
        if (w.Handle == IntPtr.Zero || !NativeMethods.IsWindow(w.Handle)) return;

        if (WindowOps.IsTracked(w.Handle))
            WindowOps.Restore(w.Handle);
        else
            WindowOps.Apply(w.Handle, new PlacementResolver.Target(true, null, false));
    }

    private static string StripExe(string s)
        => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;

    private static string Truncate(string s, int max)
    {
        s = string.IsNullOrWhiteSpace(s) ? "未命名" : s.Trim();
        return s.Length <= max ? s : s[..max];
    }
}
