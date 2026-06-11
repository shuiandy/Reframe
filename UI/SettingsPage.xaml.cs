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
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            // ToggleSwitch 的 On/OffContent 走代码本地化(附加式 resw x:Uid 较脆)。用 Common 通用词。
            string on = Loc.T("Common/On");
            string off = Loc.T("Common/Off");
            StartupToggle.OnContent = on;   StartupToggle.OffContent = off;
            DragSnapToggle.OnContent = on;  DragSnapToggle.OffContent = off;

            var cfg = ConfigService.Instance.Config;
            LoadConfigControls(cfg);
            StartupToggle.IsOn = StartupTaskService.IsEnabled();
            ConfigPathText.Text = ConfigStore.Path_;

            BuildHotkeyRows(cfg);

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = Loc.T("SettingsPage/VersionFormat", ver?.ToString() ?? "—");
        }
        finally { _loading = false; }

        // 外部热重载:config.json 被本程序之外(手改/同步/导入)改动时,ConfigService 会换 Config 引用并触发 Changed。
        // 订阅以把最新值读回控件。事件可能在任意线程触发,切回 UI 线程再动控件。Unloaded 退订防泄漏。
        ConfigService.Instance.Changed += OnConfigChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ConfigService.Instance.Changed -= OnConfigChanged;
    }

    // ConfigService.Changed 回调(任意线程)。切到 UI 线程,把外部改动后的最新配置读回控件。
    private void OnConfigChanged()
    {
        var queue = DispatcherQueue;
        if (queue is null) return;
        queue.TryEnqueue(() =>
        {
            // 复用 _loading 抑制:回写期间各控件的 *_Changed/Toggled 处理器会因 _loading 直接返回,
            // 不会把刚读回的值又 Save 一遍(否则形成 Changed → 读回 → Save → Changed 回写环)。
            _loading = true;
            try
            {
                var cfg = ConfigService.Instance.Config;
                LoadConfigControls(cfg);
                // 外部编辑也可能改了热键映射,一并刷新(BuildHotkeyRows 只设控件状态,无回写处理器,_loading 安全)。
                BuildHotkeyRows(cfg);
            }
            finally { _loading = false; }
        });
    }

    // 把配置中的"可热重载"控件值读回 UI。初始加载与外部热重载共用;调用方负责 _loading 抑制。
    // 仅覆盖随配置变化的控件:主题/材质/API key/轮询/拖拽吸附。开机自启(读 OS 任务态)、
    // 版本与路径(静态)不在此列。
    private void LoadConfigControls(AppConfig cfg)
    {
        LanguageCombo.SelectedIndex = LanguageToIndex(cfg.Language); // 0=system / 1=zh-CN / 2=en-US
        ThemeCombo.SelectedIndex = (int)cfg.Theme;        // 枚举顺序与 ComboBoxItem 顺序一致
        BackdropCombo.SelectedIndex = (int)cfg.Backdrop;  // 枚举顺序与 ComboBoxItem 顺序一致
        SgdbKeyBox.Text = cfg.SteamGridDbApiKey ?? "";
        PollBox.Value = cfg.PollIntervalMs;
        DragSnapToggle.IsOn = cfg.DragSnapEnabled;
    }

    // 语言 ComboBox 顺序 ↔ AppConfig.Language 值。项 0=跟随系统,1=简体中文,2=English。
    // 未知/缺省值一律视为 system(回到第 0 项),避免越界。
    private static readonly string[] _languageByIndex = { "system", "zh-CN", "en-US" };

    private static int LanguageToIndex(string? lang)
    {
        int i = Array.IndexOf(_languageByIndex, lang);
        return i >= 0 ? i : 0; // 含 null / "system" / 任何未知值 → 跟随系统
    }

    private static string IndexToLanguage(int index)
        => (index >= 0 && index < _languageByIndex.Length) ? _languageByIndex[index] : "system";

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
                Text = HotkeyLabel(act.Id),
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

    /// <summary>
    /// 一个热键动作的本地化显示名:优先 resw(键 Hotkey_&lt;Id&gt;);缺键时 Loc.T 回落 id 字符串,
    /// 再退到 Core 的 ActionInfo.DisplayName(中文硬编码),保证任何情况下都有可读文案。
    /// </summary>
    private static string HotkeyLabel(string actionId)
    {
        string locKey = $"SettingsPage/Hotkey_{actionId}";
        string locLabel = Loc.T(locKey);
        if (locLabel != locKey) return locLabel; // 命中本地化资源
        return HotkeyService.Actions.FirstOrDefault(a => a.Id == actionId)?.DisplayName ?? actionId;
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
            string sep = Loc.T("SettingsPage/HotkeyJoinSeparator");
            ShowHotkeyStatus(Loc.T("SettingsPage/HotkeyInvalidPrefix", string.Join(sep, invalid)), error: true);
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
                if (svc is null) { ShowHotkeyStatus(Loc.T("SettingsPage/HotkeySaved"), error: false); return; }

                var failed = new List<string>();
                foreach (var st in svc.GetStatuses())
                    if (!st.Registered)
                    {
                        var name = HotkeyLabel(st.ActionId);
                        failed.Add(Loc.T("SettingsPage/HotkeyEntryFormat", name, st.Error ?? ""));
                    }

                if (failed.Count > 0)
                {
                    string sep = Loc.T("SettingsPage/HotkeyJoinSeparator");
                    ShowHotkeyStatus(Loc.T("SettingsPage/HotkeyRegisterFailedPrefix", string.Join(sep, failed)), error: true);
                }
                else
                {
                    ShowHotkeyStatus(Loc.T("SettingsPage/HotkeyAllRegistered"), error: false);
                }
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
        ShowBackupStatus(Loc.T(ok ? "SettingsPage/BackupExported" : "SettingsPage/BackupExportFailed"), error: !ok);
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
            ShowBackupStatus(Loc.T("SettingsPage/BackupImported"), error: false);
        }
        else
        {
            ShowBackupStatus(Loc.T("SettingsPage/BackupImportFailed"), error: true);
        }
    }

    private void ShowBackupStatus(string text, bool error)
    {
        BackupStatusText.Text = text;
        BackupStatusText.Foreground = new SolidColorBrush(error ? Colors.OrangeRed : Colors.SeaGreen);
        BackupStatusText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 语言切换:写 Config.Language + Save,然后弹"需重启生效"对话框(立即重启 / 稍后)。
    /// 立即重启:Process.Start 自身 exe 再走现有退出链(App.RequestExit)。本程序 requireAdministrator,
    /// 自重启会触发一次 UAC —— 这是预期行为,按钮文案已注明。
    /// 切语言不即时改 UI(WinUI x:Uid 不可靠热切换);只有下次启动时 App 早期设 PrimaryLanguageOverride 才生效。
    /// </summary>
    private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        int idx = LanguageCombo.SelectedIndex;
        if (idx < 0) return;

        var svc = ConfigService.Instance;
        string lang = IndexToLanguage(idx);
        if (svc.Config.Language == lang) return;
        svc.Config.Language = lang;
        svc.Save();

        var dlg = new ContentDialog
        {
            Title = Loc.T("SettingsPage/RestartDialogTitle"),
            Content = Loc.T("SettingsPage/RestartDialogBody"),
            PrimaryButtonText = Loc.T("SettingsPage/RestartNowButton"),
            CloseButtonText = Loc.T("SettingsPage/RestartLaterButton"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
            RestartApp();
    }

    /// <summary>
    /// 重启自身:先启动一个新实例(继承当前管理员令牌会触发 UAC),再走 App 的正常退出链。
    /// 新进程的单实例互斥量:旧进程必须先退出后新进程才能抢到锁——但 Process.Start 不阻塞,
    /// 新实例可能在旧实例退出前抢锁失败而立即退出。为稳妥,这里用一个短延迟脚本:借 cmd 等待旧进程退出后再启动。
    /// 不引入新文件/依赖:用 ProcessStartInfo 直接拉起 exe;旧进程随即退出释放互斥量,
    /// 新进程的 SingleInstance 抢锁(若偶发竞争,用户再点一次托盘/重开即可——属可接受边角)。
    /// </summary>
    private void RestartApp()
    {
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            try
            {
                // UseShellExecute=true:unpackaged + requireAdministrator 下,新进程经 shell 提权(UAC)。
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            }
            catch { /* 启动失败就只退出,用户手动重开 */ }
        }
        // 走现有退出链(还原窗口 + 清托盘 + Application.Exit),释放单实例互斥量。
        App.RequestExit();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        int idx = ThemeCombo.SelectedIndex;
        if (idx < 0) return;

        var svc = ConfigService.Instance;
        var theme = (AppTheme)idx;  // 枚举顺序与 ComboBoxItem 顺序一致
        if (svc.Config.Theme == theme) return;
        svc.Config.Theme = theme;
        svc.Save();  // Save → Changed → MainWindow.ApplyTheme,无需直接调
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
                Title = Loc.T("SettingsPage/StartupFailedTitle"),
                Content = Loc.T(want ? "SettingsPage/StartupEnableFailed" : "SettingsPage/StartupDisableFailed"),
                CloseButtonText = Loc.T("Common/Ok"),
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
