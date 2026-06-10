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
/// 仪表盘上"接管中的窗口"一张卡片的数据。
/// 卡片按 Handle 复用(见 DashboardPage.RefreshLive 的 diff):Handle/ProcessId 身份不变,
/// 文本字段(ProfileName/Title/RectText)与 Icon 经 INotifyPropertyChanged 原地更新,
/// 既不重建集合也不闪图标。
/// <para>
/// 绑定用经典 <c>{Binding ..., Mode=OneWay}</c> 而非 <c>x:Bind</c>:宿主是裸 ItemsControl
/// (非 ListViewBase),不触发 ContainerContentChanging,x:Bind 的相位/DataContext 驱动在此场景下
/// 初次渲染不可靠(曾导致文字全空);经典 Binding 走运行时 DataContext + INotifyPropertyChanged,
/// 初次显示与原地更新都可靠。改动详见 DashboardPage.xaml 的 DataTemplate。
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

    private string _rectText = "";
    public string RectText
    {
        get => _rectText;
        set { if (_rectText != value) { _rectText = value; Raise(nameof(RectText)); } }
    }

    // 图标:同步命中(TryGetCached)即刻设;未命中后台预热再回填(回填到复用的卡对象上,此后不再闪)。
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
    // 1.5s 轻量刷新:小地图 + 接管卡片(读快照即可)。
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private Action<string>? _logHandler;
    private Action? _changedHandler;
    // 回放完成前 handler 不追加(增量全由缓冲快照覆盖),完成后才接增量,杜绝回放/增量重复。
    private bool _logReplayed;

    // 接管卡片:持久集合,按 Handle 复用。每 tick 做 diff(增/删/原地更新),不再清空重建,
    // 故已有卡的图标不会因重建回到 null 占位而闪烁。ItemsSource 只在 OnLoaded 设一次。
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

        // 概览随配置变化刷新(任意线程触发 → 切回 UI 线程)。
        _changedHandler = () => DispatcherQueue.TryEnqueue(RefreshSummary);
        ConfigService.Instance.Changed += _changedHandler;

        // 日志流:最新在上,最多 200 条。msg 已由 Watcher.Emit 带 [HH:mm:ss] 时间戳,直接显示。
        // 回放完成前(_logReplayed=false)丢弃增量:这些条目都在缓冲里,会被回放快照一并补上,
        // 避免与回放重复;回放完成后才追加纯增量。
        _logHandler = msg => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_logReplayed) return;
            LogList.Items.Insert(0, msg);
            while (LogList.Items.Count > 200)
                LogList.Items.RemoveAt(LogList.Items.Count - 1);
        });
        // 先订阅再回放:引擎在 App.OnLaunched 即启动并接管已运行的游戏,这些日志发生在订阅之前,
        // 事件已错过。先挂上 handler(此后增量不漏),再从环形缓冲回放最近历史整体重建列表。
        App.Engine.Log += _logHandler;
        ReplayLogBuffer();

        TakenList.ItemsSource = _cards; // 持久集合,只设一次

        _timer.Tick += Timer_Tick;
        _timer.Start();
        RefreshLive();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        if (_logHandler is not null) App.Engine.Log -= _logHandler;
        if (_changedHandler is not null) ConfigService.Instance.Changed -= _changedHandler;
    }

    /// <summary>
    /// 用引擎环形缓冲回放最近日志并整体重建列表(去重)。
    /// 排到 UI 队列尾部执行:此时订阅瞬间~现在之间已入队的 handler 回调都已跑完,
    /// 我们再取一次缓冲快照(含这期间所有条目)整体替换列表,既补回订阅前丢失的历史,
    /// 又避免与 handler 增量重复。此后只有新增量经 handler 追加。
    /// </summary>
    private void ReplayLogBuffer()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var recent = App.Engine.GetRecentLog(); // 旧→新,已带时间戳
            LogList.Items.Clear();
            // 最新在上:倒序插入(最多 200 条,与增量上限一致)。
            int start = Math.Max(0, recent.Count - 200);
            for (int i = recent.Count - 1; i >= start; i--)
                LogList.Items.Add(recent[i]);
            // 快照之后的增量改由 handler 追加(此 lambda 与 handler 同在 UI 线程,无并发)。
            _logReplayed = true;
        });
    }

    private void RefreshSummary()
    {
        var cfg = ConfigService.Instance.Config;
        SummaryText.Text = $"{cfg.Profiles.Count} 个配置文件 · {cfg.Layouts.Count} 个布局";
        // EngineEnabled 可能被外部改动,保持开关一致(IsOn 未变则不触发 Toggled)。
        if (EngineToggle.IsOn != cfg.EngineEnabled)
            EngineToggle.IsOn = cfg.EngineEnabled;
    }

    private void Timer_Tick(object? sender, object e) => RefreshLive();

    /// <summary>
    /// 刷新小地图与接管卡片:读引擎快照 + 显示器快照,均为轻量调用。
    /// 卡片不再清空重建,而是按 Handle 对 _cards 做 diff:消失的删、新增的加、仍在的原地更新文本字段。
    /// 已有卡保留其 Icon 引用(不回 null),从根本上消除"清空→占位→异步回填→再清空"的无限闪烁。
    /// </summary>
    private void RefreshLive()
    {
        var cfg = ConfigService.Instance.Config;
        var monitors = MonitorService.GetMonitors();
        var taken = App.Engine.GetTakenWindows();

        // 小地图:把接管窗口降为 (Handle, ProfileId) 元组,控件内部自取实时矩形。
        var takenTuples = taken
            .Select(t => (t.Handle, t.ProfileId))
            .ToList();
        MonitorMap.Refresh(monitors, takenTuples, cfg);

        // 当前应存在的句柄集合(用于删除消失项)。
        var liveHandles = new HashSet<IntPtr>(taken.Select(t => t.Handle));

        // 删:_cards 里已不在快照中的句柄。
        for (int i = _cards.Count - 1; i >= 0; i--)
            if (!liveHandles.Contains(_cards[i].Handle))
                _cards.RemoveAt(i);

        // 增/原地更新:按句柄定位现有卡。
        foreach (var t in taken)
        {
            var profile = cfg.Profiles.FirstOrDefault(p => p.Id == t.ProfileId);
            string profileName = profile?.Name ?? "?";
            string title = WindowTitle(t.Handle);
            title = string.IsNullOrEmpty(title) ? "(无标题)" : title;
            string rectText = NativeMethods.GetWindowRect(t.Handle, out var rc)
                ? $"{rc.Left},{rc.Top}  {rc.Right - rc.Left}×{rc.Bottom - rc.Top}"
                : "(矩形不可用)";
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
                };
                _cards.Add(card);
                EnsureCardIcon(card, profile); // 新卡才需要拉图标
            }
            else
            {
                // 原地更新会变的字段(经 INotifyPropertyChanged,只更文本不重建行,不碰 Icon)。
                card.ProfileName = profileName;
                card.Title = title;
                card.RectText = rectText;
                // 图标仍为空(此前未命中)时,补一次同步快取;仍不中则不在每 tick 重复异步(已在新卡时安排过)。
                if (card.Icon is null && IconCache.TryGetCached(ProcNameForProfile(profile), out var hit) && hit is not null)
                    card.Icon = hit;
            }
        }

        TakenEmpty.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // 给一张新卡解析图标:先同步快取(命中即刻显示,不闪);未命中才后台预热 + 回填到该卡对象上。
    private void EnsureCardIcon(TakenCard card, Core.Profile? profile)
    {
        string? procName = ProcNameForProfile(profile);

        // 1. 同步内存快路径:命中(非 null)直接用,零 IO、不走异步。
        if (procName is not null && IconCache.TryGetCached(procName, out var cached) && cached is not null)
        {
            card.Icon = cached;
            return;
        }
        // ExePath 已配:本地可直接出图,ByProfile 同步取(WriteableBitmap 在 UI 线程,很快)。
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.ExePath))
        {
            var icon = IconCache.ByProfile(profile);
            if (icon is not null) { card.Icon = icon; return; }
        }

        // 2. 未命中:后台预热(可能慢的路径解析 / 联网),再切回 UI 线程构建并回填到这张卡。
        uint pid = card.ProcessId;
        string? apiKey = ConfigService.Instance.Config.SteamGridDbApiKey;
        var target = card;
        _ = Task.Run(async () =>
        {
            // 预热进程路径(MainModule → QueryFullProcessImageName 兜底)。
            IconCache.PrewarmByProcessName(procName ?? ProcessNameOf(pid));

            // UI 线程先取一次(命中本地链路:磁盘缓存 / 进程提取)。
            DispatcherQueue.TryEnqueue(() => BackfillIcon(target, profile, pid));

            // 仍可能没出图(反作弊读不到 + 无磁盘缓存)→ 最后兜底:SteamGridDB 在线(配了 key 才走)。
            if (await IconCache.PrewarmFromSteamGridDbAsync(apiKey, profile).ConfigureAwait(false))
                DispatcherQueue.TryEnqueue(() => BackfillIcon(target, profile, pid));
        });
    }

    private static void BackfillIcon(TakenCard card, Core.Profile? profile, uint pid)
    {
        if (card.Icon is not null) return; // 已有图标,别覆盖
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

    /// <summary>按句柄取窗口标题(用现有 P/Invoke,不依赖全量枚举)。</summary>
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
            RefreshLive(); // 立即反映还原结果(下次接管由引擎决定)
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
