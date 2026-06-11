using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Reframe.Core;
using Layout = Reframe.Core.Layout;
using Reframe.Services;

namespace Reframe.UI;

public sealed partial class ProfileEditorPage : Page
{
    // "Takes effect only on Save": on entry, deep-copy the real Profile and apply all edits to the _work copy;
    // on save, write _work's fields back onto the real object (keeping its reference, not replacing the list element), then Save().
    // The engine reads the real object, so unsaved copy edits are invisible to it.
    private string? _realId;     // the real Profile's Id (used to locate the write-back target on save)
    private Profile? _work;      // the editing copy; all UI events touch only this
    // Suppress control event write-backs during loading, so initializing SelectedIndex/Text doesn't pollute the model.
    private bool _loading;

    public ProfileEditorPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Localize the back-button tooltip from code-behind (attached-property x:Uid is brittle in MRT Core).
        ToolTipService.SetToolTip(BackButton, Loc.T("ProfileEditorPage/BackButton.ToolTip"));

        // ToggleSwitch On/OffContent are set in code (per the i18n spec they aren't reliable via x:Uid); reuse the shared Common/On|Off words.
        string on = Loc.T("Common/On");
        string off = Loc.T("Common/Off");
        foreach (var ts in new[] { ResEnabledToggle, BorderlessToggle, TopmostToggle, AspectToggle, MuteToggle, ClipToggle, ClientToggle })
        {
            ts.OnContent = on;
            ts.OffContent = off;
        }
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
        _work = Clone(real);     // deep copy: editing touches only the copy
        LoadFromModel(_work);
    }

    // ---------- Deep copy (all Ids preserved; references are keyed by Id) ----------
    private static Profile Clone(Profile src) => new()
    {
        Id = src.Id,
        Name = src.Name,
        Enabled = src.Enabled,
        MatchKind = src.MatchKind,
        MatchValue = src.MatchValue,
        ExePath = src.ExePath,
        LaunchCommand = src.LaunchCommand,
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
        LaunchCommandBox.Text = p.LaunchCommand ?? "";

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

        // Startup resolution preset (nullable: when absent, show the default disabled state)
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

    // ---------- Startup resolution preset (Unity) ----------

    /// <summary>Ensure _work.ResolutionPreset exists (created on demand when any of its fields is edited).</summary>
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

    // ---------- Basic section ----------

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
            0 => Loc.T("ProfileEditorPage/MatchPlaceholderProcess"),
            1 => Loc.T("ProfileEditorPage/MatchPlaceholderTitle"),
            2 => Loc.T("ProfileEditorPage/MatchPlaceholderTitleRegex"),
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
        // Normalize an empty string to null (to avoid both an empty-string and a "not set" state on disk).
        string v = ExePathBox.Text?.Trim() ?? "";
        _work.ExePath = string.IsNullOrEmpty(v) ? null : v;
    }

    private void LaunchCommandBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _work is null) return;
        // Normalize an empty string to null (empty means "run the executable directly", synonymous with not set).
        string v = LaunchCommandBox.Text?.Trim() ?? "";
        _work.LaunchCommand = string.IsNullOrEmpty(v) ? null : v;
    }

    // Browse for an .exe: on WinUI 3 desktop, FileOpenPicker must InitializeWithWindow against the main window handle (otherwise it throws a COM exception).
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

            ExePathBox.Text = file.Path; // triggers ExePathBox_TextChanged -> writes _work.ExePath
        }
        catch { /* pick failed / cancelled: ignore */ }
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

    // ---------- Advanced section ----------

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

    // ---------- Rules list ----------

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
        // Wrap the whole row in a Border card; inside it, a vertical StackPanel.
        var body = new StackPanel { Spacing = 10 };

        // -- Header: index + move up/down/delete --
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = Loc.T("ProfileEditorPage/RuleTitleFormat", index + 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        Grid.SetColumn(title, 0);

        var ops = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var upBtn = IconButton("", Loc.T("ProfileEditorPage/RuleMoveUp"));
        upBtn.IsEnabled = index > 0;
        upBtn.Click += (_, _) => MoveRule(index, -1);
        var downBtn = IconButton("", Loc.T("ProfileEditorPage/RuleMoveDown"));
        downBtn.IsEnabled = _work is not null && index < _work.Rules.Count - 1;
        downBtn.Click += (_, _) => MoveRule(index, +1);
        var delBtn = IconButton("", Loc.T("ProfileEditorPage/RuleDelete"));
        delBtn.Click += (_, _) => RemoveRule(index);
        ops.Children.Add(upBtn);
        ops.Children.Add(downBtn);
        ops.Children.Add(delBtn);
        Grid.SetColumn(ops, 1);

        header.Children.Add(title);
        header.Children.Add(ops);
        body.Children.Add(header);

        // -- Monitor condition --
        body.Children.Add(BuildMonitorSection(rule));

        // -- Action --
        body.Children.Add(BuildActionSection(rule));

        // -- Avoid the taskbar --
        var workAreaCheck = new CheckBox
        {
            Content = Loc.T("ProfileEditorPage/UseWorkAreaCheck"),
            IsChecked = rule.UseWorkArea,
        };
        workAreaCheck.Checked += (_, _) => rule.UseWorkArea = true;
        workAreaCheck.Unchecked += (_, _) => rule.UseWorkArea = false;
        body.Children.Add(workAreaCheck);

        // -- Position only (don't resize): for Unity games whose render resolution is pinned in the registry --
        var moveOnlyCheck = new CheckBox
        {
            Content = Loc.T("ProfileEditorPage/MoveOnlyCheck"),
            IsChecked = rule.MoveOnly,
        };
        ToolTipService.SetToolTip(moveOnlyCheck, Loc.T("ProfileEditorPage/MoveOnlyTooltip"));
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

    // Monitor condition: any / specific resolution (W, H + pick from a connected monitor)
    private FrameworkElement BuildMonitorSection(PlacementRule rule)
    {
        var panel = new StackPanel { Spacing = 8 };

        var modeBox = new ComboBox { Header = Loc.T("ProfileEditorPage/MonitorConditionHeader"), MinWidth = 200 };
        modeBox.Items.Add(Loc.T("ProfileEditorPage/MonitorAny"));
        modeBox.Items.Add(Loc.T("ProfileEditorPage/MonitorSpecific"));
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
            Header = Loc.T("ProfileEditorPage/MonitorWidth"),
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
            Header = Loc.T("ProfileEditorPage/MonitorHeight"),
            Width = 130,
            Minimum = 0,
            Value = rule.Monitor.Height,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        hBox.ValueChanged += (s, a) =>
        {
            if (!double.IsNaN(a.NewValue)) rule.Monitor.Height = (int)a.NewValue;
        };

        var pickBox = new ComboBox { Header = Loc.T("ProfileEditorPage/MonitorPickHeader"), MinWidth = 220 };
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

    // Action: leave geometry as-is / fill / snap to layout zone / custom rectangle
    private FrameworkElement BuildActionSection(PlacementRule rule)
    {
        var panel = new StackPanel { Spacing = 8 };

        var kindBox = new ComboBox { Header = Loc.T("ProfileEditorPage/ActionHeader"), MinWidth = 200 };
        kindBox.Items.Add(Loc.T("ProfileEditorPage/ActionNone"));
        kindBox.Items.Add(Loc.T("ProfileEditorPage/ActionFullscreen"));
        kindBox.Items.Add(Loc.T("ProfileEditorPage/ActionZone"));
        kindBox.Items.Add(Loc.T("ProfileEditorPage/ActionCustomRect"));
        kindBox.SelectedIndex = rule.Kind switch
        {
            PlacementKind.None => 0,
            PlacementKind.Fullscreen => 1,
            PlacementKind.Zone => 2,
            PlacementKind.CustomRect => 3,
            _ => 1,
        };

        // Zone subsection
        var zonePanel = BuildZonePanel(rule);
        // CustomRect subsection
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

        var layoutBox = new ComboBox { Header = Loc.T("ProfileEditorPage/ZoneLayoutHeader"), MinWidth = 200 };
        var zoneBox = new ComboBox { Header = Loc.T("ProfileEditorPage/ZoneHeader"), MinWidth = 200 };

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

        // Initial selection
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
                // LoadZones auto-selects the first zone; sync ZoneId below.
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
                Text = Loc.T("ProfileEditorPage/NoLayoutsHint"),
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
        // Don't preallocate CustomRect here: this panel is built for every rule (only shown/hidden by Kind),
        // so an unconditional ??= new RectPx() would also stamp an empty rectangle onto non-CustomRect rules, leaving noise on disk.
        // Display values are read from the existing CustomRect (0 if absent); it's only created on demand when the user actually edits.
        // (Switching to the "custom rectangle" action already creates it in BuildActionSection; the Ensure here is a fallback covering paths like visual picking.)
        RectPx Ensure() => rule.CustomRect ??= new RectPx();
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

        var xBox = Field(Loc.T("ProfileEditorPage/RectX"), rect?.X ?? 0, v => Ensure().X = v);
        var yBox = Field(Loc.T("ProfileEditorPage/RectY"), rect?.Y ?? 0, v => Ensure().Y = v);
        var wBox = Field(Loc.T("ProfileEditorPage/RectW"), rect?.W ?? 0, v => Ensure().W = v);
        var hBox = Field(Loc.T("ProfileEditorPage/RectH"), rect?.H ?? 0, v => Ensure().H = v);
        row.Children.Add(xBox);
        row.Children.Add(yBox);
        row.Children.Add(wBox);
        row.Children.Add(hBox);

        var pickBtn = new Button { Content = Loc.T("ProfileEditorPage/PickRegionButton"), HorizontalAlignment = HorizontalAlignment.Left };
        pickBtn.Click += async (_, _) =>
        {
            var monitor = await PickMonitorAsync();
            if (monitor is null) return;
            var picked = await RegionPickerWindow.PickAsync(monitor);
            if (picked is null) return;
            var r = Ensure();
            r.X = picked.X;
            r.Y = picked.Y;
            r.W = picked.W;
            r.H = picked.H;
            xBox.Value = picked.X;
            yBox.Value = picked.Y;
            wBox.Value = picked.W;
            hBox.Value = picked.H;
        };

        outer.Children.Add(row);
        outer.Children.Add(pickBtn);
        return outer;
    }

    // ---------- Rule reordering ----------

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

    // ---------- Monitor helpers ----------

    private static void PopulateMonitorPicker(ComboBox box)
    {
        box.Items.Clear();
        foreach (var m in MonitorService.GetMonitors())
            box.Items.Add(new MonitorChoice(m));
    }

    // Before the custom rectangle's "visual pick", choose a monitor first (show a dialog when multi-monitor; use it directly when single).
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
            Title = Loc.T("ProfileEditorPage/PickMonitorTitle"),
            Content = combo,
            PrimaryButtonText = Loc.T("ProfileEditorPage/PickMonitorPrimary"),
            CloseButtonText = Loc.T("ProfileEditorPage/PickMonitorClose"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return combo.SelectedItem is MonitorChoice mc ? mc.Monitor : null;
    }

    // ---------- Bottom actions ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_work is null || _realId is null)
        {
            if (Frame.CanGoBack) Frame.GoBack();
            return;
        }

        // Write back onto the real object: keep its reference and its list element from being replaced (only change the contents), since the engine is holding it.
        var real = ConfigService.Instance.Config.Profiles.FirstOrDefault(p => p.Id == _realId);
        if (real is not null)
        {
            real.Name = _work.Name;
            real.Enabled = _work.Enabled;
            real.MatchKind = _work.MatchKind;
            real.MatchValue = _work.MatchValue;
            real.ExePath = _work.ExePath;
            real.LaunchCommand = _work.LaunchCommand;
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
            // Startup resolution preset: clone the whole thing back (nullable).
            real.ResolutionPreset = ClonePreset(_work.ResolutionPreset);
            // Replace the whole rules list with a re-clone of the copy's rules (to avoid leaking copy objects into the real model and sharing them afterward).
            real.Rules = _work.Rules.Select(CloneRule).ToList();
            ConfigService.Instance.Save();

            // Re-apply under the new rules: first restore all of this profile's windows (reset borderless / topmost / Clip / Mute),
            // and the engine will re-take over still-matching windows on its next tick using the rules just written back. Otherwise the old rectangle / old Clip
            // would linger until the window is rebuilt — saving releases, which keeps the semantics clean (a brief flicker is acceptable).
            Reframe.App.Engine.ReleaseProfile(real.Id);
        }

        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Cancel: discard the copy; no write-back, no save.
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    // ---------- Dropdown item wrappers ----------

    private sealed class MonitorChoice
    {
        public MonitorDesc Monitor { get; }
        public MonitorChoice(MonitorDesc m) => Monitor = m;
        public override string ToString()
            => $"{Monitor.Width}×{Monitor.Height}{(Monitor.IsPrimary ? Loc.T("ProfileEditorPage/MonitorPrimarySuffix") : "")}";
    }

    private sealed class LayoutChoice
    {
        public Layout Layout { get; }
        public LayoutChoice(Layout l) => Layout = l;
        public override string ToString()
            => string.IsNullOrWhiteSpace(Layout.Name) ? Loc.T("ProfileEditorPage/UnnamedLayout") : Layout.Name;
    }

    private sealed class ZoneChoice
    {
        public Zone Zone { get; }
        public ZoneChoice(Zone z) => Zone = z;
        public override string ToString()
            => string.IsNullOrWhiteSpace(Zone.Name) ? Loc.T("ProfileEditorPage/UnnamedZone") : Zone.Name;
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
