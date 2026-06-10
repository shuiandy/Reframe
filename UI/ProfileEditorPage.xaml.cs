using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Reframe.Core;
using Layout = Reframe.Core.Layout;
using Reframe.Services;

namespace Reframe.UI;

public sealed partial class ProfileEditorPage : Page
{
    private Profile? _profile;
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
        _profile = id is null
            ? null
            : ConfigService.Instance.Config.Profiles.FirstOrDefault(p => p.Id == id);

        if (_profile is null)
        {
            MissingHint.Visibility = Visibility.Visible;
            SaveButton.IsEnabled = false;
            return;
        }

        LoadFromModel(_profile);
    }

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

        _loading = false;

        RebuildRules();
    }

    // ---------- 基本区 ----------

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _profile is null) return;
        _profile.Name = NameBox.Text;
    }

    private void MatchKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _profile is null) return;
        _profile.MatchKind = MatchKindBox.SelectedIndex switch
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
        if (_loading || _profile is null) return;
        _profile.MatchValue = MatchValueBox.Text;
    }

    private void BorderlessToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _profile is null) return;
        _profile.Borderless = BorderlessToggle.IsOn;
    }

    private void DelayBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _profile is null) return;
        if (!double.IsNaN(args.NewValue))
            _profile.DelayMs = (int)args.NewValue;
    }

    // ---------- 高级区 ----------

    private void Offset_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _profile is null) return;
        int v = double.IsNaN(args.NewValue) ? 0 : (int)args.NewValue;
        if (sender == OffLeftBox) _profile.Offsets.Left = v;
        else if (sender == OffTopBox) _profile.Offsets.Top = v;
        else if (sender == OffRightBox) _profile.Offsets.Right = v;
        else if (sender == OffBottomBox) _profile.Offsets.Bottom = v;
    }

    private void M3Toggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _profile is null) return;
        _profile.Topmost = TopmostToggle.IsOn;
        _profile.KeepAspectRatio = AspectToggle.IsOn;
        _profile.PreserveClientArea = ClientToggle.IsOn;
        _profile.MuteInBackground = MuteToggle.IsOn;
        _profile.ClipCursor = ClipToggle.IsOn;
    }

    // ---------- 规则表 ----------

    private void RebuildRules()
    {
        RulesPanel.Children.Clear();
        if (_profile is null) return;
        for (int i = 0; i < _profile.Rules.Count; i++)
            RulesPanel.Children.Add(BuildRuleRow(_profile.Rules[i], i));
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        _profile.Rules.Add(new PlacementRule
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
        downBtn.IsEnabled = _profile is not null && index < _profile.Rules.Count - 1;
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
        if (_profile is null) return;
        int target = index + delta;
        if (target < 0 || target >= _profile.Rules.Count) return;
        var item = _profile.Rules[index];
        _profile.Rules.RemoveAt(index);
        _profile.Rules.Insert(target, item);
        RebuildRules();
    }

    private void RemoveRule(int index)
    {
        if (_profile is null) return;
        if (index < 0 || index >= _profile.Rules.Count) return;
        _profile.Rules.RemoveAt(index);
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
        if (_profile is null)
        {
            Frame.GoBack();
            return;
        }
        ConfigService.Instance.Save();
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
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
