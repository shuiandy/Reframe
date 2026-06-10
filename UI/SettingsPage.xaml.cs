using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reframe.Core;
using Reframe.Services;

namespace Reframe.UI;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    // 动作 Id → 该行的手势输入框(应用时遍历回写 Config.Hotkeys)。
    private readonly Dictionary<string, TextBox> _hotkeyBoxes = new();

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            var cfg = ConfigService.Instance.Config;
            BackdropCombo.SelectedIndex = (int)cfg.Backdrop;  // 枚举顺序与 ComboBoxItem 顺序一致
            SgdbKeyBox.Text = cfg.SteamGridDbApiKey ?? "";
            PollBox.Value = cfg.PollIntervalMs;
            DragSnapToggle.IsOn = cfg.DragSnapEnabled;
            StartupToggle.IsOn = StartupTaskService.IsEnabled();
            ConfigPathText.Text = ConfigStore.Path_;

            BuildHotkeyRows(cfg);

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"版本 {ver?.ToString() ?? "—"}";
        }
        finally { _loading = false; }
    }

    // ====================== 热键 ======================

    /// <summary>按动作表生成每行(中文名 + 手势 TextBox);手势取配置,缺项回落默认。</summary>
    private void BuildHotkeyRows(AppConfig cfg)
    {
        HotkeyRows.Children.Clear();
        _hotkeyBoxes.Clear();

        foreach (var act in HotkeyService.Actions)
        {
            string gesture =
                cfg.Hotkeys != null &&
                cfg.Hotkeys.TryGetValue(act.Id, out var g) && !string.IsNullOrWhiteSpace(g)
                    ? g
                    : act.DefaultGesture;

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });

            var label = new TextBlock
            {
                Text = act.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 0);

            var box = new TextBox { Text = gesture, PlaceholderText = act.DefaultGesture };
            Grid.SetColumn(box, 1);

            row.Children.Add(label);
            row.Children.Add(box);
            HotkeyRows.Children.Add(row);
            _hotkeyBoxes[act.Id] = box;
        }
    }

    private void ApplyHotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        var svc = ConfigService.Instance;
        var cfg = svc.Config;
        cfg.Hotkeys ??= new Dictionary<string, string>();

        // 先做本地解析校验:任何一行无效则不写盘,红字指出。
        var invalid = new List<string>();
        foreach (var act in HotkeyService.Actions)
        {
            if (!_hotkeyBoxes.TryGetValue(act.Id, out var box)) continue;
            string text = (box.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text)) text = act.DefaultGesture; // 空 = 回落默认
            if (!HotkeyGesture.TryParse(text, out _, out _))
                invalid.Add(act.DisplayName);
        }

        if (invalid.Count > 0)
        {
            ShowHotkeyStatus("手势无效:" + string.Join("、", invalid), error: true);
            return;
        }

        // 全部合法:写入配置(空 → 回落默认)。
        foreach (var act in HotkeyService.Actions)
        {
            if (!_hotkeyBoxes.TryGetValue(act.Id, out var box)) continue;
            string text = (box.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text)) text = act.DefaultGesture;
            cfg.Hotkeys[act.Id] = text;
        }

        svc.Save(); // → ConfigService.Changed → HotkeyService 防抖重注册

        // 重注册是异步防抖(250ms),稍候查状态表报告占用冲突。
        ReportHotkeyStatusDeferred();
    }

    private void ResetHotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var act in HotkeyService.Actions)
            if (_hotkeyBoxes.TryGetValue(act.Id, out var box))
                box.Text = act.DefaultGesture;

        var svc = ConfigService.Instance;
        var cfg = svc.Config;
        cfg.Hotkeys ??= new Dictionary<string, string>();
        foreach (var act in HotkeyService.Actions)
            cfg.Hotkeys[act.Id] = act.DefaultGesture;
        svc.Save();

        ReportHotkeyStatusDeferred();
    }

    // 等重注册防抖窗口过去再读状态表,把"被占用"的动作汇总成红字;全绿则绿字。
    private void ReportHotkeyStatusDeferred()
    {
        var queue = DispatcherQueue;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(450);
            queue.TryEnqueue(() =>
            {
                var svc = App.Hotkeys;
                if (svc is null) { ShowHotkeyStatus("已保存。", error: false); return; }

                var failed = new List<string>();
                foreach (var st in svc.GetStatuses())
                    if (!st.Registered)
                    {
                        var name = HotkeyService.Actions.FirstOrDefault(a => a.Id == st.ActionId)?.DisplayName ?? st.ActionId;
                        failed.Add($"{name}({st.Error})");
                    }

                if (failed.Count > 0)
                    ShowHotkeyStatus("以下热键注册失败:" + string.Join("、", failed), error: true);
                else
                    ShowHotkeyStatus("已应用,全部热键注册成功。", error: false);
            });
        });
    }

    private void ShowHotkeyStatus(string text, bool error)
    {
        HotkeyStatusText.Text = text;
        HotkeyStatusText.Foreground = new SolidColorBrush(error ? Colors.OrangeRed : Colors.SeaGreen);
        HotkeyStatusText.Visibility = Visibility.Visible;
    }

    // ====================== 配置备份 ======================

    private async void ExportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        bool ok = await ConfigBackup.ExportAsync(this.XamlRoot);
        ShowBackupStatus(ok ? "已导出配置。" : "导出已取消或失败。", error: !ok);
    }

    private async void ImportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        bool ok = await ConfigBackup.ImportAsync(this.XamlRoot);
        if (ok)
        {
            // 导入会替换配置 + 触发 Changed;刷新本页控件以反映新值。
            _loading = true;
            try { BuildHotkeyRows(ConfigService.Instance.Config); }
            finally { _loading = false; }
            ShowBackupStatus("已导入。", error: false);
        }
        else
        {
            ShowBackupStatus("导入已取消或文件无效。", error: true);
        }
    }

    private void ShowBackupStatus(string text, bool error)
    {
        BackupStatusText.Text = text;
        BackupStatusText.Foreground = new SolidColorBrush(error ? Colors.OrangeRed : Colors.SeaGreen);
        BackupStatusText.Visibility = Visibility.Visible;
    }

    private void BackdropCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        int idx = BackdropCombo.SelectedIndex;
        if (idx < 0) return;

        var svc = ConfigService.Instance;
        var kind = (BackdropKind)idx;  // 枚举顺序与 ComboBoxItem 顺序一致
        if (svc.Config.Backdrop == kind) return;
        svc.Config.Backdrop = kind;
        svc.Save();  // Save → Changed → MainWindow.ApplyBackdrop,无需直接调
    }

    private void SgdbKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var svc = ConfigService.Instance;
        string v = SgdbKeyBox.Text?.Trim() ?? "";
        string? key = string.IsNullOrEmpty(v) ? null : v;  // 空串归一为 null = 关闭
        if (svc.Config.SteamGridDbApiKey == key) return;
        svc.Config.SteamGridDbApiKey = key;
        svc.Save();
    }

    private void PollBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (double.IsNaN(args.NewValue)) return;

        var svc = ConfigService.Instance;
        int v = (int)Math.Round(args.NewValue);
        if (svc.Config.PollIntervalMs == v) return;
        svc.Config.PollIntervalMs = v;
        svc.Save();
    }

    private void DragSnapToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var svc = ConfigService.Instance;
        bool on = DragSnapToggle.IsOn;
        if (svc.Config.DragSnapEnabled == on) return;
        svc.Config.DragSnapEnabled = on;
        svc.Save();
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        bool want = StartupToggle.IsOn;
        bool ok = want ? StartupTaskService.Enable() : StartupTaskService.Disable();

        if (!ok)
        {
            // 失败:回滚开关并提示(schtasks 需要管理员;本程序已 requireAdministrator,通常可用)。
            _loading = true;
            StartupToggle.IsOn = !want;
            _loading = false;

            var dlg = new ContentDialog
            {
                Title = "操作失败",
                Content = want ? "创建开机自启计划任务失败。" : "删除开机自启计划任务失败。",
                CloseButtonText = "好",
                XamlRoot = this.XamlRoot
            };
            await dlg.ShowAsync();
        }
    }

    private void RestoreAllButton_Click(object sender, RoutedEventArgs e)
    {
        WindowOps.RestoreAll();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ConfigStore.Dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{ConfigStore.Dir}\"",
                UseShellExecute = true
            });
        }
        catch { /* 打不开就算了 */ }
    }
}
