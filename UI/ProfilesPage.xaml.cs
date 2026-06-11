using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Reframe.Core;
using Reframe.Interop;
using Reframe.Services;

namespace Reframe.UI;

/// <summary>List-row view model (read-only display; the real data lives on Config.Profiles).</summary>
public sealed partial class ProfileRow : System.ComponentModel.INotifyPropertyChanged
{
    public string ProfileId { get; init; } = "";
    public string Name { get; init; } = "";
    public string MatchSummary { get; init; } = "";
    public string RulesSummary { get; init; } = "";
    public bool Enabled { get; set; }

    /// <summary>One-click launch is available only with a launch command or an executable (otherwise the Launch button is disabled).</summary>
    public bool CanLaunch { get; init; }

    // MatchKind=Process fetches the process icon via IconCache; other match kinds have no process, so they always use the default glyph.
    // The icon is filled in asynchronously (null first on Reload, then set on the UI thread after Task.Run extraction), hence the notify property.
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

    /// <summary>For a process match where an icon should be attempted, holds the process name (without .exe); otherwise null = always the default glyph.</summary>
    public string? ProcessNameForIcon { get; init; }

    public Visibility RealIconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackIconVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A row in the left-column "running windows" list. Reused by Handle (see the diff in RefreshWindows): identity fields
/// (Handle/ProcessId) stay fixed, while text fields and Icon update in place via INotifyPropertyChanged — neither rebuilding the
/// collection nor flickering the icon. x:Bind requires a top-level public class.
/// </summary>
public sealed partial class WindowRow : System.ComponentModel.INotifyPropertyChanged
{
    public IntPtr Handle { get; init; }
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";   // without .exe, lowercase; used for building profiles / the ignore list

    // ---- Filter state (backs "show filtered" and ignore-list management) ----
    // Reason comes from WindowScanner.Classify; updated in place (no rescan needed after ignore / stop-ignoring).
    private FilterReason _reason = FilterReason.None;
    public FilterReason Reason
    {
        get => _reason;
        set
        {
            if (_reason == value) return;
            _reason = value;
            Raise(nameof(Reason));
            Raise(nameof(IsFiltered));
            Raise(nameof(IsUserIgnored));
            Raise(nameof(CanIgnore));
            Raise(nameof(RowOpacity));
            Raise(nameof(ReasonLabel));
            Raise(nameof(ReasonVisibility));
            Raise(nameof(IgnoreItemVisibility));
            Raise(nameof(UnignoreItemVisibility));
        }
    }

    /// <summary>Whether this row is filtered (not a normal candidate). Filtered rows are greyed out and show a reason caption.</summary>
    public bool IsFiltered => Reason != FilterReason.None;

    /// <summary>Whether it's filtered by the "user ignore list" (reversible; shows "Stop ignoring").</summary>
    public bool IsUserIgnored => Reason == FilterReason.UserIgnored;

    /// <summary>Whether it can be acted on by "ignore this process" (the system-shell blacklist is irreversible; if already user-ignored, "Stop ignoring" is offered instead). Requires a process name.</summary>
    public bool CanIgnore => Reason != FilterReason.SystemShell && !string.IsNullOrEmpty(ProcessName);

    /// <summary>Filtered rows are greyed out.</summary>
    public double RowOpacity => IsFiltered ? 0.45 : 1.0;

    /// <summary>Filter-reason caption (System window / Ignored / Hidden / Too small). Empty for normal candidates.</summary>
    public string ReasonLabel => Reason switch
    {
        FilterReason.SystemShell => Loc.T("ProfilesPage/ReasonSystemWindow"),
        FilterReason.UserIgnored => Loc.T("ProfilesPage/ReasonIgnored"),
        FilterReason.Cloaked     => Loc.T("ProfilesPage/ReasonHidden"),
        FilterReason.TooSmall    => Loc.T("ProfilesPage/ReasonTooSmall"),
        _ => "",
    };

    public Visibility ReasonVisibility => IsFiltered ? Visibility.Visible : Visibility.Collapsed;

    // The two context-menu items are mutually exclusive: ignorable and not yet user-ignored -> show "Ignore this process"; already user-ignored -> show "Stop ignoring".
    public Visibility IgnoreItemVisibility
        => CanIgnore && !IsUserIgnored ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnignoreItemVisibility
        => IsUserIgnored ? Visibility.Visible : Visibility.Collapsed;

    private string _title = "";
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Raise(nameof(Title)); } }
    }

    // Secondary grey text: the process exe's full path (when resolvable), otherwise falls back to "processname.exe".
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

    // If the same process already has a profile -> show the "Has profile" grey tag at the end of the row. Updated in place as profiles are added/removed.
    private bool _hasProfile;
    public bool HasProfile
    {
        get => _hasProfile;
        set { if (_hasProfile != value) { _hasProfile = value; Raise(nameof(HasProfile)); Raise(nameof(HasProfileVisibility)); } }
    }

    public Visibility HasProfileVisibility => HasProfile ? Visibility.Visible : Visibility.Collapsed;

    // Icon: set immediately on a synchronous hit (TryGetCached/ByProcessId); on a miss, prewarm in the background and fill in later (onto the reused row object, so it won't flicker again afterward).
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
    // Save() raises Changed, and Changed rebuilds the list — this flag swallows the echo we caused ourselves, avoiding list churn.
    private bool _suppressReload;

    // Left-column window list: _windows is the full persistent collection, reused by Handle for an incremental diff (cf. DashboardPage),
    // so row objects (WindowRow) keep a stable identity and the Icon doesn't flicker. _windowsView is the "filter view" bound to the ListView,
    // holding only a subset of references to the same row objects in _windows (per search-box hits); visibility toggling is done by adding/removing
    // view members rather than container visibility (container visibility is unreliable under virtualization / unrealized containers and misreports empty).
    // Because the row objects are shared, adding/removing from the view doesn't reset the Icon.
    private readonly List<WindowRow> _windows = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<WindowRow> _windowsView = new();
    private readonly DispatcherTimer _windowTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    // "Show filtered" toggle: off = list only normal candidates (default); on = also list filtered ones (greyed + reason), a fallback for recovering games filtered by mistake.
    private bool _showFiltered;

    public ProfilesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Localize the named-element tooltips from code-behind (attached-property x:Uid is brittle in MRT Core).
        ToolTipService.SetToolTip(ShowFilteredToggle, Loc.T("ProfilesPage/ShowFilteredToggle.ToolTip"));
        ToolTipService.SetToolTip(RefreshButton, Loc.T("ProfilesPage/RefreshButton.ToolTip"));

        ConfigService.Instance.Changed += OnConfigChanged;
        Reload();

        WindowList.ItemsSource = _windowsView; // filter view, set once
        RefreshWindows();
        _windowTimer.Tick += WindowTimer_Tick;
        _windowTimer.Start();
    }

    // In-template button tooltips: realized per row, so they can't be named — set them as each button loads (Loc.T).
    private void CreateFromWindowButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button b)
            ToolTipService.SetToolTip(b, Loc.T("ProfilesPage/CreateFromWindowButton.ToolTip"));
    }

    private void LaunchButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button b)
            ToolTipService.SetToolTip(b, Loc.T("ProfilesPage/LaunchButton.ToolTip"));
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
            ApplyWindowFilter(); // profiles added/removed -> the left-column "has profile" tags must be recomputed
        });
    }

    // ======================== Right column: profile list ========================

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

    // Icon loading (same priority as the dashboard: ExePath -> in-memory cache -> local chain -> SteamGridDB fallback).
    // The list isn't rebuilt on a timer (only on Reload), so there's no flicker; but it still does "synchronous cache first, async on miss" to show cached icons immediately.
    private void LoadIconsAsync(List<ProfileRow> rows)
    {
        var cfg = ConfigService.Instance.Config;
        string? apiKey = cfg.SteamGridDbApiKey;

        foreach (var row in rows)
        {
            var profile = cfg.Profiles.FirstOrDefault(p => p.Id == row.ProfileId);
            if (profile is null) continue;

            // Synchronous cache: ExePath configured / in-memory hit -> show immediately, skip the async path.
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
                // All local sources failed -> SteamGridDB online fallback (only if a key is set); fill in on success.
                if (await IconCache.PrewarmFromSteamGridDbAsync(apiKey, profile).ConfigureAwait(false))
                    DispatcherQueue.TryEnqueue(() => target.Icon ??= IconCache.ByProfile(profile));
            });
        }
    }

    private static ProfileRow ToRow(Profile p) => new()
    {
        ProfileId = p.Id,
        Name = string.IsNullOrWhiteSpace(p.Name) ? Loc.T("ProfilesPage/Unnamed") : p.Name,
        Enabled = p.Enabled,
        MatchSummary = MatchSummaryOf(p),
        RulesSummary = Loc.T("ProfilesPage/RulesCountFormat", p.Rules.Count),
        CanLaunch = !string.IsNullOrWhiteSpace(p.LaunchCommand) || !string.IsNullOrWhiteSpace(p.ExePath),
        ProcessNameForIcon = p.MatchKind == MatchKind.Process && !string.IsNullOrWhiteSpace(p.MatchValue)
            ? p.MatchValue
            : null,
    };

    private static string MatchSummaryOf(Profile p)
    {
        string label = p.MatchKind switch
        {
            MatchKind.Process => Loc.T("ProfilesPage/MatchKindProcess"),
            MatchKind.Title => Loc.T("ProfilesPage/MatchKindTitle"),
            MatchKind.TitleRegex => Loc.T("ProfilesPage/MatchKindTitleRegex"),
            _ => Loc.T("ProfilesPage/MatchKindGeneric"),
        };
        string val = string.IsNullOrWhiteSpace(p.MatchValue) ? Loc.T("ProfilesPage/MatchValueUnset") : p.MatchValue;
        return Loc.T("ProfilesPage/MatchSummaryFormat", label, val);
    }

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts || ts.Tag is not string id) return;
        var profile = ConfigService.Instance.Config.Profiles.FirstOrDefault(x => x.Id == id);
        if (profile is null || profile.Enabled == ts.IsOn) return;

        profile.Enabled = ts.IsOn;

        // Disable = restore: release all windows this profile took over (engine contract API), then persist.
        if (!ts.IsOn)
            Reframe.App.Engine.ReleaseProfile(profile.Id);

        _suppressReload = true;
        ConfigService.Instance.Save();
        _suppressReload = false;
    }

    // In-row "Launch" button: locate the Profile by Tag (ProfileId) and call GameLauncher.Launch.
    // On failure (no launch method configured / file missing / already running / exception) show the error in a ContentDialog.
    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var profile = ConfigService.Instance.Config.Profiles.FirstOrDefault(x => x.Id == id);
        if (profile is null) return;

        if (GameLauncher.Launch(profile, out var error)) return;

        var dialog = new ContentDialog
        {
            Title = Loc.T("ProfilesPage/LaunchFailedTitle"),
            Content = error ?? Loc.T("ProfilesPage/LaunchFailedFallback"),
            CloseButtonText = Loc.T("ProfilesPage/LaunchFailedClose"),
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

    // Double-click = open the editor. A double-click on the toggle / launch button should not navigate (they have their own behavior).
    private void ProfileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsWithinInteractiveControl(e.OriginalSource as DependencyObject)) return;
        var row = RowFromEventSource(e.OriginalSource as DependencyObject);
        if (row is not null)
            Frame.Navigate(typeof(ProfileEditorPage), row.ProfileId);
    }

    // Right-click = select the row before the ContextFlyout opens, so Edit/Delete act on the correct item; don't pop the menu when right-clicking the toggle/button.
    private void ProfileRow_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (IsWithinInteractiveControl(e.OriginalSource as DependencyObject))
        {
            e.Handled = true; // swallow, to avoid wrongly popping the row menu over the toggle/button
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

    // A double-click / right-click on in-row interactive controls (toggle/button) should not trigger row-level navigation or menus — each has its own behavior.
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
            Name = Loc.T("ProfilesPage/NewProfileName"),
            MatchKind = MatchKind.Process,
            MatchValue = "",
            Rules =
            {
                new PlacementRule
                {
                    Monitor = new MonitorFilter(),   // any monitor
                    Kind = PlacementKind.Fullscreen, // fill
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

        string displayName = string.IsNullOrWhiteSpace(profile.Name) ? Loc.T("ProfilesPage/Unnamed") : profile.Name;
        var dialog = new ContentDialog
        {
            Title = Loc.T("ProfilesPage/DeleteDialogTitle"),
            Content = Loc.T("ProfilesPage/DeleteDialogContentFormat", displayName),
            PrimaryButtonText = Loc.T("Common/Delete"),
            CloseButtonText = Loc.T("Common/Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // Restore all of this profile's taken-over windows before deleting: otherwise, after Remove the engine can no longer find this rule,
        // and windows already made borderless / topmost / clipped / muted would stay taken over forever (orphaned).
        Reframe.App.Engine.ReleaseProfile(profile.Id);

        cfg.Profiles.Remove(profile);
        _suppressReload = true;
        ConfigService.Instance.Save();
        _suppressReload = false;
        Reload();
        ApplyWindowFilter(); // the left-column "has profile" tags must be recomputed
    }

    // ======================== Left column: running windows ========================

    private void WindowTimer_Tick(object? sender, object e) => RefreshWindows();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyWindowFilter();

    // "Show filtered" toggle change: no rescan, just change the visible set (filtered rows are already in _windows).
    private void ShowFilteredToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _showFiltered = ShowFilteredToggle.IsChecked == true;
        ApplyWindowFilter();
    }

    /// <summary>
    /// Refresh the left-column window list: enumerate all top-level windows with their filter reason (system shell / user-ignored / cloaked / too small),
    /// diff against _windows incrementally by Handle (remove gone, add new, update surviving in place), without rebuilding the whole list, to avoid flicker.
    /// Then filter the visible items by the search box + the "show filtered" toggle. Filtered rows stay in _windows too, so flipping the toggle reveals them immediately (greyed + reason).
    /// </summary>
    private void RefreshWindows()
    {
        var ignores = ConfigService.Instance.Config.IgnoredProcesses;
        var scanned = WindowScanner.EnumerateAllWithReason(ignores);
        var live = new HashSet<IntPtr>(scanned.Select(s => s.Window.Handle));

        // Remove: handles no longer in the scan results.
        for (int i = _windows.Count - 1; i >= 0; i--)
            if (!live.Contains(_windows[i].Handle))
                _windows.RemoveAt(i);

        foreach (var s in scanned)
        {
            var w = s.Window;
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
                    Reason = s.Reason,
                    Icon = IconCache.ByProcessId(w.ProcessId), // for a running window, prefer the pid entry (precise, and it learns the path along the way)
                };
                _windows.Add(row);
            }
            else
            {
                // Update the fields that can change in place (title / size / has-profile tag / filter reason); keep an existing Icon to avoid flicker.
                existing.Title = w.Title;
                existing.SizeLabel = sizeLabel;
                existing.HasProfile = hasProfile;
                existing.Reason = s.Reason;
                if (existing.Icon is null)
                    existing.Icon = IconCache.ByProcessId(w.ProcessId);
            }
        }

        ApplyWindowFilter();
    }

    /// <summary>
    /// Sync the subset of _windows matching the search box into _windowsView (add/remove in place, preserving order).
    /// _windows and _windowsView share the same WindowRow instances, so adding/removing from the view doesn't reset the Icon/state — no flicker.
    /// </summary>
    private void ApplyWindowFilter()
    {
        string q = (SearchBox?.Text ?? "").Trim();
        var ignores = ConfigService.Instance.Config.IgnoredProcesses;

        foreach (var row in _windows)
        {
            // Sync the "has profile" tag (called after profiles are added/removed, so it reflects immediately even between scans).
            row.HasProfile = HasProfileForProcess(row.ProcessName);

            // Sync the user-ignore state (reflects immediately after ignore / stop-ignoring, or an external edit to the list, without waiting for the next rescan).
            // Only flip between None <-> UserIgnored; system shell / cloaked / too small are decided by the scan and not touched here.
            bool ignored = WindowScanner.IsUserIgnored(row.ProcessName, ignores);
            if (ignored && row.Reason == FilterReason.None)
                row.Reason = FilterReason.UserIgnored;
            else if (!ignored && row.Reason == FilterReason.UserIgnored)
                row.Reason = FilterReason.None;
        }

        bool Match(WindowRow r) => q.Length == 0
            || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (r.ProcessName + ".exe").Contains(q, StringComparison.OrdinalIgnoreCase);

        // When "show filtered" is off, filtered rows don't enter the visible set (they stay in _windows to be revealed when toggled on).
        var desired = _windows.Where(r => (_showFiltered || !r.IsFiltered) && Match(r)).ToList();

        // Remove: items that should no longer appear in the view.
        for (int i = _windowsView.Count - 1; i >= 0; i--)
            if (!desired.Contains(_windowsView[i]))
                _windowsView.RemoveAt(i);

        // Add / reorder: place into the desired order (move/insert in place; leave alone when reference-equal).
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

    /// <summary>The process exe's full path (when resolvable), otherwise "processname.exe"; if the process name is empty too, "(unknown process)".</summary>
    private static string PathLabelOf(WindowInfo w)
    {
        string? path = IconCache.TryResolveExePath(w.ProcessId);
        if (!string.IsNullOrEmpty(path)) return path;
        return string.IsNullOrEmpty(w.ProcessName) ? Loc.T("ProfilesPage/UnknownProcess") : w.ProcessName + ".exe";
    }

    /// <summary>Whether a profile already targets this process name (MatchKind=Process).</summary>
    private static bool HasProfileForProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        return ConfigService.Instance.Config.Profiles.Any(p =>
            p.MatchKind == MatchKind.Process &&
            string.Equals(StripExe(p.MatchValue), processName, StringComparison.OrdinalIgnoreCase));
    }

    private static WindowRow? WindowRowFromSender(object sender)
        => (sender as FrameworkElement)?.DataContext as WindowRow;

    // "+ Create profile" (row main button / context-menu item): same logic as the dialog version —
    // Name = truncated title, MatchKind = Process, preset any-monitor -> fill rule, duplicate-process confirmation, navigate to the editor after Save.
    private async void CreateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (WindowRowFromSender(sender) is not { } w) return;

        var cfg = ConfigService.Instance.Config;

        // Process name: WindowScanner gives lowercase without .exe; the config stores .exe consistently (matching the default config; the match side does StripExe).
        string proc = string.IsNullOrEmpty(w.ProcessName) ? "" : w.ProcessName;
        string matchValue = string.IsNullOrEmpty(proc) ? "" : proc + ".exe";

        // A profile already targets this process name -> confirm whether to create another.
        if (!string.IsNullOrEmpty(proc))
        {
            var dup = cfg.Profiles.FirstOrDefault(p =>
                p.MatchKind == MatchKind.Process &&
                string.Equals(StripExe(p.MatchValue), proc, StringComparison.OrdinalIgnoreCase));
            if (dup is not null)
            {
                string dupName = string.IsNullOrWhiteSpace(dup.Name) ? Loc.T("ProfilesPage/Unnamed") : dup.Name;
                var confirm = new ContentDialog
                {
                    Title = Loc.T("ProfilesPage/DuplicateDialogTitle"),
                    Content = Loc.T("ProfilesPage/DuplicateDialogContentFormat", dupName),
                    PrimaryButtonText = Loc.T("ProfilesPage/DuplicateDialogPrimary"),
                    CloseButtonText = Loc.T("Common/Cancel"),
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
                    Monitor = new MonitorFilter(),   // any monitor
                    Kind = PlacementKind.Fullscreen, // fill
                },
            },
        };
        cfg.Profiles.Add(profile);

        _suppressReload = true;
        ConfigService.Instance.Save();
        _suppressReload = false;

        Frame.Navigate(typeof(ProfileEditorPage), profile.Id);
    }

    // Context-menu "Ignore this process": add the process name to Config.IgnoredProcesses (lowercase, without .exe) + Save.
    // In normal mode the process's windows then disappear from the visible set; with "show filtered" on, they're greyed and show "Ignored" + "Stop ignoring".
    // The menu item is hidden for system-shell windows (CanIgnore=false), so we never reach here for them.
    private void IgnoreProcess_Click(object sender, RoutedEventArgs e)
    {
        if (WindowRowFromSender(sender) is not { } w) return;
        if (string.IsNullOrEmpty(w.ProcessName)) return;

        var cfg = ConfigService.Instance.Config;
        string proc = w.ProcessName; // WindowScanner already gives lowercase without .exe
        if (!cfg.IgnoredProcesses.Any(x =>
                string.Equals(StripExe(x.Trim()), proc, StringComparison.OrdinalIgnoreCase)))
        {
            cfg.IgnoredProcesses.Add(proc);
            _suppressReload = true;
            ConfigService.Instance.Save();
            _suppressReload = false;
        }

        // Reflect immediately (don't wait for the next rescan): flip every row of this process to "Ignored".
        ApplyWindowFilter();
    }

    // Context-menu "Stop ignoring": remove the process name from Config.IgnoredProcesses + Save, returning its windows to the normal list.
    private void UnignoreProcess_Click(object sender, RoutedEventArgs e)
    {
        if (WindowRowFromSender(sender) is not { } w) return;
        if (string.IsNullOrEmpty(w.ProcessName)) return;

        var cfg = ConfigService.Instance.Config;
        int removed = cfg.IgnoredProcesses.RemoveAll(x =>
            string.Equals(StripExe(x.Trim()), w.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            _suppressReload = true;
            ConfigService.Instance.Save();
            _suppressReload = false;
        }

        ApplyWindowFilter();
    }

    // Context-menu "Make borderless now": temporarily make this window handle borderless (not stored in config). If already tracked, restore it (toggle semantics).
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
        s = string.IsNullOrWhiteSpace(s) ? Loc.T("ProfilesPage/Unnamed") : s.Trim();
        return s.Length <= max ? s : s[..max];
    }
}
