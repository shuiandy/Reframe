using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reframe.Core;
using Reframe.Interop;
using Reframe.Services;

namespace Reframe.UI;

/// <summary>仪表盘上"接管中的窗口"一张卡片的数据(x:Bind 用)。</summary>
public sealed class TakenCard
{
    public IntPtr Handle { get; init; }
    public string ProfileName { get; init; } = "";
    public string Title { get; init; } = "";
    public string RectText { get; init; } = "";
}

public sealed partial class DashboardPage : Page
{
    // 1.5s 轻量刷新:小地图 + 接管卡片(读快照即可)。
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private Action<string>? _logHandler;
    private Action? _changedHandler;

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

        // 日志流:最新在上,最多 200 条。
        _logHandler = msg => DispatcherQueue.TryEnqueue(() =>
        {
            LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
            while (LogList.Items.Count > 200)
                LogList.Items.RemoveAt(LogList.Items.Count - 1);
        });
        App.Engine.Log += _logHandler;

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

    private void RefreshSummary()
    {
        var cfg = ConfigService.Instance.Config;
        SummaryText.Text = $"{cfg.Profiles.Count} 个配置文件 · {cfg.Layouts.Count} 个布局";
        // EngineEnabled 可能被外部改动,保持开关一致(IsOn 未变则不触发 Toggled)。
        if (EngineToggle.IsOn != cfg.EngineEnabled)
            EngineToggle.IsOn = cfg.EngineEnabled;
    }

    private void Timer_Tick(object? sender, object e) => RefreshLive();

    /// <summary>刷新小地图与接管卡片:读引擎快照 + 显示器快照,均为轻量调用。</summary>
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

        // 卡片列表:逐窗口取标题 + 实时矩形 + profile 名。
        var cards = new List<TakenCard>();
        foreach (var t in taken)
        {
            string profileName = cfg.Profiles.FirstOrDefault(p => p.Id == t.ProfileId)?.Name ?? "?";
            string title = WindowTitle(t.Handle);
            string rectText = NativeMethods.GetWindowRect(t.Handle, out var rc)
                ? $"{rc.Left},{rc.Top}  {rc.Right - rc.Left}×{rc.Bottom - rc.Top}"
                : "(矩形不可用)";
            cards.Add(new TakenCard
            {
                Handle = t.Handle,
                ProfileName = profileName,
                Title = string.IsNullOrEmpty(title) ? "(无标题)" : title,
                RectText = rectText,
            });
        }

        TakenList.ItemsSource = cards;
        TakenEmpty.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
