using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Reframe.Core;
using Reframe.Services;

namespace Reframe.UI;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

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
            PollBox.Value = cfg.PollIntervalMs;
            DragSnapToggle.IsOn = cfg.DragSnapEnabled;
            StartupToggle.IsOn = StartupTaskService.IsEnabled();
            ConfigPathText.Text = ConfigStore.Path_;

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"版本 {ver?.ToString() ?? "—"}";
        }
        finally { _loading = false; }
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
