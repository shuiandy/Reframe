using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reframe.Core;
using Reframe.Interop;
using Reframe.Services;

namespace Reframe.UI;

/// <summary>
/// Backing data for one "active windows" card on the dashboard.
/// Cards are reused by Handle (see the diff in DashboardPage.RefreshLive): Handle/ProcessId are the
/// stable identity, while the text fields (ProfileName/Title/RectText) and Icon update in place via
/// INotifyPropertyChanged — no collection rebuild and no icon flicker.
/// <para>
/// Bindings use classic <c>{Binding ..., Mode=OneWay}</c> rather than <c>x:Bind</c>: the host is a bare
/// ItemsControl (not a ListViewBase), so it never raises ContainerContentChanging, and x:Bind's
/// phase/DataContext driving is unreliable for initial render here (it once left all text blank).
/// Classic Binding goes through the runtime DataContext + INotifyPropertyChanged, so both the first
/// paint and in-place updates are reliable. See the DataTemplate in DashboardPage.xaml.
/// </para>
/// </summary>
public sealed partial class TakenCard : System.ComponentModel.INotifyPropertyChanged
{
    public IntPtr Handle { get; init; }
    public uint ProcessId { get; init; }

    private string _profileName = "";
    public string ProfileName
    {
        get => _profileName;
        set { if (_profileName != value) { _profileName = value; Raise(nameof(ProfileName)); } }
    }

    private string _title = "";
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Raise(nameof(Title)); } }
    }

    // Card sub-info line (second row). See DashboardPage.ComputeSubInfo for the rule:
    // process name available and title == profile name (or empty) -> show process name only;
    // different -> "title · process"; process name unavailable -> fall back to title.
    private string _subInfo = "";
    public string SubInfo
    {
        get => _subInfo;
        set { if (_subInfo != value) { _subInfo = value; Raise(nameof(SubInfo)); } }
    }

    // Process name (with .exe) is looked up once and cached on the card; RefreshLive reuses it each
    // tick instead of re-querying the process table.
    // null = not looked up yet; "" = looked up but failed (process gone / no access), don't retry.
    public string? ProcessExeName { get; set; }

    private string _rectText = "";
    public string RectText
    {
        get => _rectText;
        set { if (_rectText != value) { _rectText = value; Raise(nameof(RectText)); } }
    }

    // Icon: a synchronous cache hit (TryGetCached) sets it immediately; a miss prewarms in the
    // background then backfills onto the reused card object (no flicker thereafter).
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

public sealed partial class DashboardPage : Page
{
    // Lightweight 1.5s refresh: mini-map + taken cards (reading snapshots is cheap).
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private Action<string>? _logHandler;
    private Action? _changedHandler;
    // Before replay completes the handler appends nothing (the buffer snapshot covers all deltas);
    // only after replay do we take deltas, so replay and live deltas never duplicate.
    private bool _logReplayed;

    // Taken cards: a persistent collection reused by Handle. Each tick diffs (add/remove/update in
    // place) instead of clear-and-rebuild, so an existing card's icon never flickers back to a null
    // placeholder on rebuild. ItemsSource is set once in OnLoaded.
    private readonly System.Collections.ObjectModel.ObservableCollection<TakenCard> _cards = new();

    public DashboardPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var cfg = ConfigService.Instance.Config;
        EngineToggle.IsOn = cfg.EngineEnabled;
        RefreshSummary();

        // Refresh the summary whenever config changes (may fire on any thread -> hop to the UI thread).
        _changedHandler = () => DispatcherQueue.TryEnqueue(RefreshSummary);
        ConfigService.Instance.Changed += _changedHandler;

        // Log stream: newest on top, capped at 200. msg already carries a [HH:mm:ss] timestamp from
        // Watcher.Emit, so display it as-is.
        // Drop deltas until replay finishes (_logReplayed=false): those entries are in the buffer and
        // the replay snapshot will include them, so we avoid duplicating; only append pure deltas afterward.
        _logHandler = msg => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_logReplayed) return;
            LogList.Items.Insert(0, msg);
            while (LogList.Items.Count > 200)
                LogList.Items.RemoveAt(LogList.Items.Count - 1);
        });
        // Subscribe before replaying: the engine starts in App.OnLaunched and takes over already-running
        // games, so those logs happen before we subscribe and the events are already missed. Attach the
        // handler first (no deltas dropped from here on), then replay recent history from the ring buffer
        // to rebuild the list wholesale.
        App.Engine.Log += _logHandler;
        ReplayLogBuffer();

        TakenList.ItemsSource = _cards; // persistent collection, set once

        _timer.Tick += Timer_Tick;
        _timer.Start();
        RefreshLive();
    }

    // Engine toggle On/OffContent come from resw via Loc.T (attached On/OffContent x:Uid is unreliable
    // in MRT Core). Set on Loaded so the runtime language override is already applied.
    private void EngineToggle_Loaded(object sender, RoutedEventArgs e)
    {
        EngineToggle.OnContent = Loc.T("DashboardPage/EngineOn");
        EngineToggle.OffContent = Loc.T("DashboardPage/EngineOff");
    }

    // The Rescan button lives inside a DataTemplate, so there's no x:Name to reach it; set its tooltip
    // from code on Loaded. ToolTipService.ToolTip is an attached property that's brittle via x:Uid.
    private void RescanButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button b)
            ToolTipService.SetToolTip(b, Loc.T("DashboardPage/RescanTooltip"));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        if (_logHandler is not null) App.Engine.Log -= _logHandler;
        if (_changedHandler is not null) ConfigService.Instance.Changed -= _changedHandler;
    }

    /// <summary>
    /// Replay recent logs from the engine ring buffer and rebuild the list wholesale (deduped).
    /// Runs at the tail of the UI queue: by then every handler callback enqueued between subscribing
    /// and now has run, so we take one more buffer snapshot (covering all of those entries) and swap
    /// the whole list — restoring the history lost before subscription while avoiding duplicates with
    /// the handler deltas. From here on only new deltas are appended by the handler.
    /// </summary>
    private void ReplayLogBuffer()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var recent = App.Engine.GetRecentLog(); // old -> new, already timestamped
            LogList.Items.Clear();
            // Newest on top: insert in reverse (cap 200, matching the delta cap).
            int start = Math.Max(0, recent.Count - 200);
            for (int i = recent.Count - 1; i >= start; i--)
                LogList.Items.Add(recent[i]);
            // Deltas after this snapshot are appended by the handler (this lambda and the handler share
            // the UI thread, so there's no concurrency).
            _logReplayed = true;
        });
    }

    private void RefreshSummary()
    {
        var cfg = ConfigService.Instance.Config;
        SummaryText.Text = Loc.T("DashboardPage/SummaryFormat", cfg.Profiles.Count, cfg.Layouts.Count);
        // EngineEnabled may be changed externally; keep the toggle in sync (no Toggled fires if IsOn is unchanged).
        if (EngineToggle.IsOn != cfg.EngineEnabled)
            EngineToggle.IsOn = cfg.EngineEnabled;
    }

    private void Timer_Tick(object? sender, object e) => RefreshLive();

    /// <summary>
    /// Refresh the mini-map and taken cards: reads the engine snapshot + monitor snapshot, both cheap.
    /// Cards are no longer cleared and rebuilt; instead _cards is diffed by Handle: drop vanished ones,
    /// add new ones, update text fields in place for those still present. Existing cards keep their Icon
    /// reference (never reset to null), which eliminates the "clear -> placeholder -> async backfill ->
    /// clear again" endless flicker at the root.
    /// </summary>
    private void RefreshLive()
    {
        var cfg = ConfigService.Instance.Config;
        var monitors = MonitorService.GetMonitors();
        var taken = App.Engine.GetTakenWindows();

        // Mini-map: reduce taken windows to (Handle, ProfileId) tuples; the control fetches live rects itself.
        var takenTuples = taken
            .Select(t => (t.Handle, t.ProfileId))
            .ToList();
        MonitorMap.Refresh(monitors, takenTuples, cfg);

        // Set of handles that should currently exist (used to drop vanished items).
        var liveHandles = new HashSet<IntPtr>(taken.Select(t => t.Handle));

        // Remove: handles in _cards no longer present in the snapshot.
        for (int i = _cards.Count - 1; i >= 0; i--)
            if (!liveHandles.Contains(_cards[i].Handle))
                _cards.RemoveAt(i);

        // Add / update in place: locate the existing card by handle.
        foreach (var t in taken)
        {
            var profile = cfg.Profiles.FirstOrDefault(p => p.Id == t.ProfileId);
            // No profile name = the profile was deleted (external config edit / hot reload dropped it),
            // but the window is still registered in the engine's _takeover (orphan). Show "(deleted)"
            // rather than a bare "?"; the card's [Restore] button still works by Handle (WindowOps.Restore
            // doesn't depend on the profile).
            string profileName = profile?.Name ?? Loc.T("DashboardPage/ProfileDeleted");
            string title = WindowTitle(t.Handle);
            title = string.IsNullOrEmpty(title) ? Loc.T("DashboardPage/WindowNoTitle") : title;
            string rectText = NativeMethods.GetWindowRect(t.Handle, out var rc)
                ? $"{rc.Left},{rc.Top}  {rc.Right - rc.Left}×{rc.Bottom - rc.Top}"
                : Loc.T("DashboardPage/RectUnavailable");
            NativeMethods.GetWindowThreadProcessId(t.Handle, out uint pid);

            var card = _cards.FirstOrDefault(c => c.Handle == t.Handle);
            if (card is null)
            {
                card = new TakenCard
                {
                    Handle = t.Handle,
                    ProcessId = pid,
                    ProfileName = profileName,
                    Title = title,
                    RectText = rectText,
                    ProcessExeName = ProcessExeNameOf(pid), // look up process name once, cache on the card
                };
                card.SubInfo = ComputeSubInfo(profileName, title, card.ProcessExeName);
                _cards.Add(card);
                EnsureCardIcon(card, profile); // only a new card needs to fetch an icon
            }
            else
            {
                // Update the fields that change (via INotifyPropertyChanged: text only, no row rebuild, no touching Icon).
                card.ProfileName = profileName;
                card.Title = title;
                card.RectText = rectText;
                // Process name is already cached (looked up on the first frame); recompute the sub-info line
                // as the title/profile name changes, without re-querying the process table.
                card.SubInfo = ComputeSubInfo(profileName, title, card.ProcessExeName ?? "");
                // If the icon is still null (previous miss), try one synchronous cache hit; if it still misses
                // don't re-fire async every tick (already scheduled when the card was new).
                if (card.Icon is null && IconCache.TryGetCached(ProcNameForProfile(profile), out var hit) && hit is not null)
                    card.Icon = hit;
            }
        }

        TakenEmpty.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Resolve an icon for a new card: try the synchronous cache first (a hit shows instantly, no flicker);
    // only on a miss prewarm in the background and backfill onto the card object.
    private void EnsureCardIcon(TakenCard card, Core.Profile? profile)
    {
        string? procName = ProcNameForProfile(profile);

        // 1. Synchronous in-memory fast path: a hit (non-null) is used directly, zero IO, no async.
        if (procName is not null && IconCache.TryGetCached(procName, out var cached) && cached is not null)
        {
            card.Icon = cached;
            return;
        }
        // ExePath is set: a local icon is available directly; ByProfile fetches synchronously
        // (WriteableBitmap on the UI thread, fast).
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.ExePath))
        {
            var icon = IconCache.ByProfile(profile);
            if (icon is not null) { card.Icon = icon; return; }
        }

        // 2. Miss: prewarm in the background (potentially slow path resolution / network), then hop back
        // to the UI thread to build and backfill onto this card.
        uint pid = card.ProcessId;
        string? apiKey = ConfigService.Instance.Config.SteamGridDbApiKey;
        var target = card;
        _ = Task.Run(async () =>
        {
            // Prewarm the process path (MainModule, falling back to QueryFullProcessImageName).
            IconCache.PrewarmByProcessName(procName ?? ProcessNameOf(pid));

            // Fetch once on the UI thread (hits the local chain: disk cache / process extraction).
            DispatcherQueue.TryEnqueue(() => BackfillIcon(target, profile, pid));

            // Might still produce nothing (anti-cheat blocks the read + no disk cache) -> last resort:
            // SteamGridDB online (only when an API key is configured).
            if (await IconCache.PrewarmFromSteamGridDbAsync(apiKey, profile).ConfigureAwait(false))
                DispatcherQueue.TryEnqueue(() => BackfillIcon(target, profile, pid));
        });
    }

    private static void BackfillIcon(TakenCard card, Core.Profile? profile, uint pid)
    {
        if (card.Icon is not null) return; // already has an icon, don't overwrite
        var icon = profile is not null ? IconCache.ByProfile(profile) : IconCache.ByProcessId(pid);
        icon ??= IconCache.ByProcessId(pid);
        if (icon is not null) card.Icon = icon;
    }

    private static string? ProcNameForProfile(Core.Profile? p)
        => p is { MatchKind: MatchKind.Process } && !string.IsNullOrWhiteSpace(p.MatchValue)
            ? p.MatchValue
            : null;

    private static string? ProcessNameOf(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    /// <summary>Process name + ".exe" (for the sub-info line). Returns "" if unavailable (process gone /
    /// no access), and is not retried.</summary>
    private static string ProcessExeNameOf(uint pid)
    {
        var name = ProcessNameOf(pid);
        return string.IsNullOrEmpty(name) ? "" : name + ".exe";
    }

    /// <summary>
    /// Compute the card sub-info line (second row). Rules:
    /// process name available and (title == profile name OR title empty) -> show process name only, e.g. "StarRail.exe";
    /// process name available and title differs -> "window title · StarRail.exe";
    /// process name unavailable -> fall back to the title (legacy behavior).
    /// </summary>
    private static string ComputeSubInfo(string profileName, string title, string exeName)
    {
        bool hasTitle = !string.IsNullOrEmpty(title) && title != Loc.T("DashboardPage/WindowNoTitle");
        if (string.IsNullOrEmpty(exeName))
            return hasTitle ? title : "";
        if (!hasTitle || title == profileName)
            return exeName;
        return Loc.T("DashboardPage/SubInfoFormat", title, exeName);
    }

    /// <summary>Get a window title by handle (uses the existing P/Invoke, no full enumeration).</summary>
    private static string WindowTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private void RestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IntPtr handle } && handle != IntPtr.Zero)
        {
            WindowOps.Restore(handle);
            RefreshLive(); // reflect the restore immediately (the engine decides the next takeover)
        }
    }

    private void Reapply_Click(object sender, RoutedEventArgs e)
    {
        App.Engine.Poke();
    }

    private void EngineToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var svc = ConfigService.Instance;
        if (svc.Config.EngineEnabled == EngineToggle.IsOn) return;
        svc.Config.EngineEnabled = EngineToggle.IsOn;
        svc.Save();
    }
}
