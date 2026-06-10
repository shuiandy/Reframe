using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Reframe.Core;
using Layout = Reframe.Core.Layout;
using Reframe.Services;

namespace Reframe.UI;

public sealed partial class ProfileEditorPage : Page
{
    // 「点保存才生效」:进入时对真实 Profile 做深拷贝,全部编辑落在 _work 副本上;
    // 保存时把 _work 字段写回真实对象(保留其引用,不替换列表元素),再 Save()。
    // 引擎读的是真实对象,未保存的副本改动对其不可见。
    private string? _realId;     // 真实 Profile 的 Id(保存时定位写回目标)
    private Profile? _work;      // 编辑副本,所有 UI 事件只动它
    // 加载阶段抑制控件事件回写,避免初始化 SelectedIndex/Text 时污染模型。
    private bool _loading;

    public ProfileEditorPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var id = e.Parameter as string;
        var real = id is null
            ? null
            : ConfigService.Instance.Config.Profiles.FirstOrDefault(p => p.Id == id);

        if (real is null)
        {
            _realId = null;
            _work = null;
            MissingHint.Visibility = Visibility.Visible;
            SaveButton.IsEnabled = false;
            return;
        }

        _realId = real.Id;
        _work = Clone(real);     // 深拷贝:编辑只动副本
        LoadFromModel(_work);
    }

    // ---------- 深拷贝(Id 全部保持不变,引用关系靠 Id) ----------
    private static Profile Clone(Profile src) => new()
    {
        Id = src.Id,
        Name = src.Name,
        Enabled = src.Enabled,
        MatchKind = src.MatchKind,
        MatchValue = src.MatchValue,
        ExePath = src.ExePath,
        Borderless = src.Borderless,
        Method = src.Method,
        DelayMs = src.DelayMs,
        Offsets = new Offsets
        {
            Left = src.Offsets.Left,
            Top = src.Offsets.Top,
            Right = src.Offsets.Right,
            Bottom = src.Offsets.Bottom,
        },
        Topmost = src.Topmost,
        KeepAspectRatio = src.KeepAspectRatio,
        PreserveClientArea = src.PreserveClientArea,
        MuteInBackground = src.MuteInBackground,
        ClipCursor = src.ClipCursor,
        ResolutionPreset = ClonePreset(src.ResolutionPreset),
        Rules = src.Rules.Select(CloneRule).ToList(),
    };

    private static PlacementRule CloneRule(PlacementRule r) => new()
    {
        Monitor = new MonitorFilter { Width = r.Monitor.Width, Height = r.Monitor.Height },
        Kind = r.Kind,
        LayoutId = r.LayoutId,
        ZoneId = r.ZoneId,
        CustomRect = r.CustomRect is null
            ? null
            : new RectPx { X = r.CustomRect.X, Y = r.CustomRect.Y, W = r.CustomRect.W, H = r.CustomRect.H },
        UseWorkArea = r.UseWorkArea,
        MoveOnly = r.MoveOnly,
    };

    private static UnityResolutionPreset? ClonePreset(UnityResolutionPreset? src)
        => src is null
            ? null
            : new UnityResolutionPreset
            {
                Enabled = src.Enabled,
                RegistryPath = src.RegistryPath,
                Width = src.Width,
                Height = src.Height,
                Windowed = src.Windowed,
            };

    private void LoadFromModel(Profile p)
    {
        _loading = true;

        NameBox.Text = p.Name;
        MatchKindBox.SelectedIndex = p.MatchKind switch
        {
            MatchKind.Process => 0,
            MatchKind.Title => 1,
            MatchKind.TitleRegex => 2,
            _ => 0,
        };
        ApplyMatchPlaceholder();
        MatchValueBox.Text = p.MatchValue;
        ExePathBox.Text = p.ExePath ?? "";

        BorderlessToggle.IsOn = p.Borderless;
        DelayBox.Value = p.DelayMs;

        OffLeftBox.Value = p.Offsets.Left;
        OffTopBox.Value = p.Offsets.Top;
        OffRightBox.Value = p.Offsets.Right;
        OffBottomBox.Value = p.Offsets.Bottom;

        TopmostToggle.IsOn = p.Topmost;
        AspectToggle.IsOn = p.KeepAspectRatio;
        ClientToggle.IsOn = p.PreserveClientArea;
        MuteToggle.IsOn = p.MuteInBackground;
        ClipToggle.IsOn = p.ClipCursor;

        // 启动分辨率预设(可空:无则显示默认禁用态)
        var rp = p.ResolutionPreset;
        ResEnabledToggle.IsOn = rp?.Enabled ?? false;
        ResPathBox.Text = rp?.RegistryPath ?? "";
        ResWidthBox.Value = rp?.Width ?? 0;
        ResHeightBox.Value = rp?.Height ?? 0;
        ResWindowedCheck.IsChecked = rp?.Windowed ?? true;
        if (rp is not null) ResolutionExpander.IsExpanded = rp.Enabled;

        _loading = false;

        RebuildRules();
    }

    // ---------- 启动分辨率预设(Unity) ----------

    /// <summary>确保 _work.ResolutionPreset 存在(任一字段被编辑时按需创建)。</summary>
    private UnityResolutionPreset EnsurePreset()
        => _work!.ResolutionPreset ??= new UnityResolutionPreset();

    private void ResEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _work is null) return;
        EnsurePreset().Enabled = ResEnabledToggle.IsOn;
    }

    private void ResPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _work is null) return;
        EnsurePreset().RegistryPath = ResPathBox.Text;
    }

    private void ResSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _work is null) return;
        int v = double.IsNaN(args.NewValue) ? 0 : (int)args.NewValue;
        if (sender == ResWidthBox) EnsurePreset().Width = v;
        else if (sender == ResHeightBox) EnsurePreset().Height = v;
    }

    private void ResWindowed_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _work is null) return;
        EnsurePreset().Windowed = ResWindowedCheck.IsChecked == true;
    }

    // ---------- 基本区 ----------

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _work is null) return;
        _work.Name = NameBox.Text;
    }

    private void MatchKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _work is null) return;
        _work.MatchKind = MatchKindBox.SelectedIndex switch
        {
            1 => MatchKind.Title,
            2 => MatchKind.TitleRegex,
            _ => MatchKind.Process,
        };
        ApplyMatchPlaceholder();
    }

    private void ApplyMatchPlaceholder()
    {
        MatchValueBox.PlaceholderText = MatchKindBox.SelectedIndex switch
        {
            0 => "例如 StarRail.exe",
            1 => "例如 原神",
            2 => "例如 ^崩坏",
            _ => "",
        };
    }

    private void MatchValueBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _work is null) return;
        _work.MatchValue = MatchValueBox.Text;
    }

    private void ExePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _work is null) return;
        // 空串归一为 null(避免存盘出现空字符串与"未设置"两种态)。
        string v = ExePathBox.Text?.Trim() ?? "";
        _work.ExePath = string.IsNullOrEmpty(v) ? null : v;
    }

    // 浏览 .exe:WinUI3 桌面下 FileOpenPicker 必须 InitializeWithWindow 绑主窗口句柄(否则抛 COM 异常)。
    private async void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        if (_work is null) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add(".exe");

            IntPtr hwnd = Reframe.App.Main is { } w
                ? WinRT.Interop.WindowNative.GetWindowHandle(w)
                : IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            ExePathBox.Text = file.Path; // 触发 ExePathBox_TextChanged → 写入 _work.ExePath
        }
        catch { /* 选取失败/取消:忽略 */ }
    }

    private void BorderlessToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _work is null) return;
        _work.Borderless = BorderlessToggle.IsOn;
    }

    private void DelayBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _work is null) return;
        if (!double.IsNaN(args.NewValue))
            _work.DelayMs = (int)args.NewValue;
    }

    // ---------- 高级区 ----------

    private void Offset_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _work is null) return;
        int v = double.IsNaN(args.NewValue) ? 0 : (int)args.NewValue;
        if (sender == OffLeftBox) _work.Offsets.Left = v;
        else if (sender == OffTopBox) _work.Offsets.Top = v;
        else if (sender == OffRightBox) _work.Offsets.Right = v;
        else if (sender == OffBottomBox) _work.Offsets.Bottom = v;
    }

    private void M3Toggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _work is null) return;
        _work.Topmost = TopmostToggle.IsOn;
        _work.KeepAspectRatio = AspectToggle.IsOn;
        _work.PreserveClientArea = ClientToggle.IsOn;
        _work.MuteInBackground = MuteToggle.IsOn;
        _work.ClipCursor = ClipToggle.IsOn;
    }

    // ---------- 规则表 ----------

    private void RebuildRules()
    {
        RulesPanel.Children.Clear();
        if (_work is null) return;
        for (int i = 0; i < _work.Rules.Count; i++)
            RulesPanel.Children.Add(BuildRuleRow(_work.Rules[i], i));
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (_work is null) return;
        _work.Rules.Add(new PlacementRule
        {
            Monitor = new MonitorFilter(),
            Kind = PlacementKind.Fullscreen,
        });
        RebuildRules();
    }

    private FrameworkElement BuildRuleRow(PlacementRule rule, int index)
    {
        // 整行用一个 Border 卡片包裹;内部一个垂直 StackPanel。
        var body = new StackPanel { Spacing = 10 };

        // —— 头部:序号 + 上移/下移/删除 ——
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = $"规则 {index + 1}",
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        Grid.SetColumn(title, 0);

        var ops = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var upBtn = IconButton("", "上移");
        upBtn.IsEnabled = index > 0;
        upBtn.Click += (_, _) => MoveRule(index, -1);
        var downBtn = IconButton("", "下移");
        downBtn.IsEnabled = _work is not null && index < _work.Rules.Count - 1;
        downBtn.Click += (_, _) => MoveRule(index, +1);
        var delBtn = IconButton("", "删除");
        delBtn.Click += (_, _) => RemoveRule(index);
        ops.Children.Add(upBtn);
        ops.Children.Add(downBtn);
        ops.Children.Add(delBtn);
        Grid.SetColumn(ops, 1);

        header.Children.Add(title);
        header.Children.Add(ops);
        body.Children.Add(header);

        // —— 显示器条件 ——
        body.Children.Add(BuildMonitorSection(rule));

        // —— 动作 ——
        body.Children.Add(BuildActionSection(rule));

        // —— 避开任务栏 ——
        var workAreaCheck = new CheckBox
        {
            Content = "避开任务栏(使用工作区)",
            IsChecked = rule.UseWorkArea,
        };
        workAreaCheck.Checked += (_, _) => rule.UseWorkArea = true;
        workAreaCheck.Unchecked += (_, _) => rule.UseWorkArea = false;
        body.Children.Add(workAreaCheck);

        // —— 只定位(不调尺寸):用于渲染分辨率钉死在注册表的 Unity 游戏 ——
        var moveOnlyCheck = new CheckBox
        {
            Content = "只定位(不调尺寸)",
            IsChecked = rule.MoveOnly,
        };
        ToolTipService.SetToolTip(moveOnlyCheck,
            "只把窗口左上角移到目标位置,保持游戏自身尺寸不变(避免拉伸)。配合启动分辨率预设使用。");
        moveOnlyCheck.Checked += (_, _) => rule.MoveOnly = true;
        moveOnlyCheck.Unchecked += (_, _) => rule.MoveOnly = false;
        body.Children.Add(moveOnlyCheck);

        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = body,
        };
    }

    // 显示器条件:任意 / 指定分辨率(W、H + 从当前显示器选)
    private FrameworkElement BuildMonitorSection(PlacementRule rule)
    {
        var panel = new StackPanel { Spacing = 8 };

        var modeBox = new ComboBox { Header = "显示器条件", MinWidth = 200 };
        modeBox.Items.Add("任意显示器");
        modeBox.Items.Add("指定分辨率");
        bool specific = rule.Monitor.Width != 0 || rule.Monitor.Height != 0;
        modeBox.SelectedIndex = specific ? 1 : 0;

        var detail = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Visibility = specific ? Visibility.Visible : Visibility.Collapsed,
        };

        var wBox = new NumberBox
        {
            Header = "宽",
            Width = 130,
            Minimum = 0,
            Value = rule.Monitor.Width,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        wBox.ValueChanged += (s, a) =>
        {
            if (!double.IsNaN(a.NewValue)) rule.Monitor.Width = (int)a.NewValue;
        };

        var hBox = new NumberBox
        {
            Header = "高",
            Width = 130,
            Minimum = 0,
            Value = rule.Monitor.Height,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        hBox.ValueChanged += (s, a) =>
        {
            if (!double.IsNaN(a.NewValue)) rule.Monitor.Height = (int)a.NewValue;
        };

        var pickBox = new ComboBox { Header = "从当前显示器选", MinWidth = 220 };
        PopulateMonitorPicker(pickBox);
        pickBox.SelectionChanged += (s, a) =>
        {
            if (pickBox.SelectedItem is MonitorChoice mc)
            {
                rule.Monitor.Width = mc.Monitor.Width;
                rule.Monitor.Height = mc.Monitor.Height;
                wBox.Value = mc.Monitor.Width;
                hBox.Value = mc.Monitor.Height;
            }
        };

        detail.Children.Add(wBox);
        detail.Children.Add(hBox);
        detail.Children.Add(pickBox);

        modeBox.SelectionChanged += (s, a) =>
        {
            if (modeBox.SelectedIndex == 0)
            {
                rule.Monitor.Width = 0;
                rule.Monitor.Height = 0;
                wBox.Value = 0;
                hBox.Value = 0;
                detail.Visibility = Visibility.Collapsed;
            }
            else
            {
                detail.Visibility = Visibility.Visible;
            }
        };

        panel.Children.Add(modeBox);
        panel.Children.Add(detail);
        return panel;
    }

    // 动作:不动几何 / 铺满 / 套布局分区 / 自定义矩形
    private FrameworkElement BuildActionSection(PlacementRule rule)
    {
        var panel = new StackPanel { Spacing = 8 };

        var kindBox = new ComboBox { Header = "动作", MinWidth = 200 };
        kindBox.Items.Add("不动几何");
        kindBox.Items.Add("铺满");
        kindBox.Items.Add("套布局分区");
        kindBox.Items.Add("自定义矩形");
        kindBox.SelectedIndex = rule.Kind switch
        {
            PlacementKind.None => 0,
            PlacementKind.Fullscreen => 1,
            PlacementKind.Zone => 2,
            PlacementKind.CustomRect => 3,
            _ => 1,
        };

        // Zone 子区
        var zonePanel = BuildZonePanel(rule);
        // CustomRect 子区
        var rectPanel = BuildRectPanel(rule);

        void Sync()
        {
            zonePanel.Visibility = rule.Kind == PlacementKind.Zone ? Visibility.Visible : Visibility.Collapsed;
            rectPanel.Visibility = rule.Kind == PlacementKind.CustomRect ? Visibility.Visible : Visibility.Collapsed;
        }

        kindBox.SelectionChanged += (s, a) =>
        {
            rule.Kind = kindBox.SelectedIndex switch
            {
                0 => PlacementKind.None,
                2 => PlacementKind.Zone,
                3 => PlacementKind.CustomRect,
                _ => PlacementKind.Fullscreen,
            };
            if (rule.Kind == PlacementKind.CustomRect)
                rule.CustomRect ??= new RectPx();
            Sync();
        };

        panel.Children.Add(kindBox);
        panel.Children.Add(zonePanel);
        panel.Children.Add(rectPanel);
        Sync();
        return panel;
    }

    private FrameworkElement BuildZonePanel(PlacementRule rule)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        var layoutBox = new ComboBox { Header = "布局", MinWidth = 200 };
        var zoneBox = new ComboBox { Header = "分区", MinWidth = 200 };

        var layouts = ConfigService.Instance.Config.Layouts;
        foreach (var l in layouts)
            layoutBox.Items.Add(new LayoutChoice(l));

        void LoadZones(Layout? layout, string? selectZoneId)
        {
            zoneBox.Items.Clear();
            if (layout is null) return;
            foreach (var z in layout.Zones)
                zoneBox.Items.Add(new ZoneChoice(z));
            if (selectZoneId is not null)
            {
                for (int i = 0; i < zoneBox.Items.Count; i++)
                    if (zoneBox.Items[i] is ZoneChoice zc && zc.Zone.Id == selectZoneId)
                    {
                        zoneBox.SelectedIndex = i;
                        return;
                    }
            }
            if (zoneBox.Items.Count > 0) zoneBox.SelectedIndex = 0;
        }

        // 初始选中
        Layout? current = layouts.FirstOrDefault(l => l.Id == rule.LayoutId);
        if (current is not null)
        {
            for (int i = 0; i < layoutBox.Items.Count; i++)
                if (layoutBox.Items[i] is LayoutChoice lc && lc.Layout.Id == current.Id)
                {
                    layoutBox.SelectedIndex = i;
                    break;
                }
            LoadZones(current, rule.ZoneId);
        }
        else if (layoutBox.Items.Count > 0)
        {
            layoutBox.SelectedIndex = 0;
            var first = ((LayoutChoice)layoutBox.Items[0]).Layout;
            rule.LayoutId = first.Id;
            LoadZones(first, rule.ZoneId);
        }

        layoutBox.SelectionChanged += (s, a) =>
        {
            if (layoutBox.SelectedItem is LayoutChoice lc)
            {
                rule.LayoutId = lc.Layout.Id;
                rule.ZoneId = null;
                LoadZones(lc.Layout, null);
                // LoadZones 会自动选中首个分区,下面同步 ZoneId
                if (zoneBox.SelectedItem is ZoneChoice zc0) rule.ZoneId = zc0.Zone.Id;
            }
        };

        zoneBox.SelectionChanged += (s, a) =>
        {
            if (zoneBox.SelectedItem is ZoneChoice zc)
                rule.ZoneId = zc.Zone.Id;
        };

        if (layouts.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "尚未创建任何布局,请先到“布局”页面新建。",
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            panel.Children.Add(hint);
        }
        else
        {
            panel.Children.Add(layoutBox);
            panel.Children.Add(zoneBox);
        }
        return panel;
    }

    private FrameworkElement BuildRectPanel(PlacementRule rule)
    {
        rule.CustomRect ??= new RectPx();
        var rect = rule.CustomRect;

        var outer = new StackPanel { Spacing = 8 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        NumberBox Field(string header, int value, Action<int> set)
        {
            var nb = new NumberBox
            {
                Header = header,
                Width = 110,
                Value = value,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            nb.ValueChanged += (s, a) =>
            {
                if (!double.IsNaN(a.NewValue)) set((int)a.NewValue);
            };
            return nb;
        }

        var xBox = Field("X", rect.X, v => rect.X = v);
        var yBox = Field("Y", rect.Y, v => rect.Y = v);
        var wBox = Field("宽", rect.W, v => rect.W = v);
        var hBox = Field("高", rect.H, v => rect.H = v);
        row.Children.Add(xBox);
        row.Children.Add(yBox);
        row.Children.Add(wBox);
        row.Children.Add(hBox);

        var pickBtn = new Button { Content = "可视化选择…", HorizontalAlignment = HorizontalAlignment.Left };
        pickBtn.Click += async (_, _) =>
        {
            var monitor = await PickMonitorAsync();
            if (monitor is null) return;
            var picked = await RegionPickerWindow.PickAsync(monitor);
            if (picked is null) return;
            rect.X = picked.X;
            rect.Y = picked.Y;
            rect.W = picked.W;
            rect.H = picked.H;
            xBox.Value = picked.X;
            yBox.Value = picked.Y;
            wBox.Value = picked.W;
            hBox.Value = picked.H;
        };

        outer.Children.Add(row);
        outer.Children.Add(pickBtn);
        return outer;
    }

    // ---------- 规则重排 ----------

    private void MoveRule(int index, int delta)
    {
        if (_work is null) return;
        int target = index + delta;
        if (target < 0 || target >= _work.Rules.Count) return;
        var item = _work.Rules[index];
        _work.Rules.RemoveAt(index);
        _work.Rules.Insert(target, item);
        RebuildRules();
    }

    private void RemoveRule(int index)
    {
        if (_work is null) return;
        if (index < 0 || index >= _work.Rules.Count) return;
        _work.Rules.RemoveAt(index);
        RebuildRules();
    }

    // ---------- 显示器辅助 ----------

    private static void PopulateMonitorPicker(ComboBox box)
    {
        box.Items.Clear();
        foreach (var m in MonitorService.GetMonitors())
            box.Items.Add(new MonitorChoice(m));
    }

    // 自定义矩形“可视化选择”前先选一块显示器(多屏时弹对话框,单屏直接用)。
    private async Task<MonitorDesc?> PickMonitorAsync()
    {
        var monitors = MonitorService.GetMonitors();
        if (monitors.Count == 0) return null;
        if (monitors.Count == 1) return monitors[0];

        var combo = new ComboBox { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var m in monitors)
            combo.Items.Add(new MonitorChoice(m));
        combo.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "选择显示器",
            Content = combo,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return combo.SelectedItem is MonitorChoice mc ? mc.Monitor : null;
    }

    // ---------- 底部操作 ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_work is null || _realId is null)
        {
            if (Frame.CanGoBack) Frame.GoBack();
            return;
        }

        // 写回真实对象:保留其引用与列表实例不被替换(只改内容),引擎正在持有它。
        var real = ConfigService.Instance.Config.Profiles.FirstOrDefault(p => p.Id == _realId);
        if (real is not null)
        {
            real.Name = _work.Name;
            real.Enabled = _work.Enabled;
            real.MatchKind = _work.MatchKind;
            real.MatchValue = _work.MatchValue;
            real.ExePath = _work.ExePath;
            real.Borderless = _work.Borderless;
            real.Method = _work.Method;
            real.DelayMs = _work.DelayMs;
            real.Offsets.Left = _work.Offsets.Left;
            real.Offsets.Top = _work.Offsets.Top;
            real.Offsets.Right = _work.Offsets.Right;
            real.Offsets.Bottom = _work.Offsets.Bottom;
            real.Topmost = _work.Topmost;
            real.KeepAspectRatio = _work.KeepAspectRatio;
            real.PreserveClientArea = _work.PreserveClientArea;
            real.MuteInBackground = _work.MuteInBackground;
            real.ClipCursor = _work.ClipCursor;
            // 启动分辨率预设:整体克隆写回(可空)。
            real.ResolutionPreset = ClonePreset(_work.ResolutionPreset);
            // 规则整列替换为副本规则的再克隆(避免把副本对象漏给真实模型造成后续共享)。
            real.Rules = _work.Rules.Select(CloneRule).ToList();
            ConfigService.Instance.Save();
        }

        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 取消:丢弃副本,不写回、不保存。
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    // ---------- 下拉项包装 ----------

    private sealed class MonitorChoice
    {
        public MonitorDesc Monitor { get; }
        public MonitorChoice(MonitorDesc m) => Monitor = m;
        public override string ToString()
            => $"{Monitor.Width}×{Monitor.Height}{(Monitor.IsPrimary ? " (主屏)" : "")}";
    }

    private sealed class LayoutChoice
    {
        public Layout Layout { get; }
        public LayoutChoice(Layout l) => Layout = l;
        public override string ToString()
            => string.IsNullOrWhiteSpace(Layout.Name) ? "未命名布局" : Layout.Name;
    }

    private sealed class ZoneChoice
    {
        public Zone Zone { get; }
        public ZoneChoice(Zone z) => Zone = z;
        public override string ToString()
            => string.IsNullOrWhiteSpace(Zone.Name) ? "未命名分区" : Zone.Name;
    }

    private static Button IconButton(string glyph, string tooltip)
    {
        var btn = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(8),
        };
        ToolTipService.SetToolTip(btn, tooltip);
        return btn;
    }
}
