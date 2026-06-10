using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reframe.Core;
using Reframe.Services;

namespace Reframe.UI;

public sealed partial class DashboardPage : Page
{
    private readonly DispatcherTimer _hitTimer = new() { Interval = TimeSpan.FromSeconds(2) };
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

        // 命中窗口:每 2 秒扫一次。
        _hitTimer.Tick += HitTimer_Tick;
        _hitTimer.Start();
        RefreshHits();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _hitTimer.Stop();
        _hitTimer.Tick -= HitTimer_Tick;
        if (_logHandler is not null) App.Engine.Log -= _logHandler;
        if (_changedHandler is not null) ConfigService.Instance.Changed -= _changedHandler;
    }

    private void RefreshSummary()
    {
        var cfg = ConfigService.Instance.Config;
        SummaryText.Text = $"{cfg.Profiles.Count} 个配置文件 · {cfg.Layouts.Count} 个布局";
        // EngineEnabled 可能被外部改动,保持开关一致(不会重复触发 Save:IsOn 未变则不触发 Toggled)。
        if (EngineToggle.IsOn != cfg.EngineEnabled)
            EngineToggle.IsOn = cfg.EngineEnabled;
    }

    private void HitTimer_Tick(object? sender, object e) => RefreshHits();

    private void RefreshHits()
    {
        var cfg = ConfigService.Instance.Config;
        HitList.Items.Clear();

        foreach (var w in WindowScanner.EnumerateTopLevel())
        {
            foreach (var p in cfg.Profiles)
            {
                if (!MatchEngine.Matches(w, p)) continue;
                string proc = string.IsNullOrEmpty(w.ProcessName) ? "?" : w.ProcessName;
                HitList.Items.Add($"「{p.Name}」← {w.Title} ({proc})");
                break; // 一个窗口只算第一个命中的 profile(与引擎一致)
            }
        }

        HitEmpty.Visibility = HitList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EngineToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var svc = ConfigService.Instance;
        if (svc.Config.EngineEnabled == EngineToggle.IsOn) return;
        svc.Config.EngineEnabled = EngineToggle.IsOn;
        svc.Save();
    }
}
