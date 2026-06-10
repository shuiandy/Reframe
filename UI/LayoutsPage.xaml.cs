using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Reframe.Core;
using Layout = Reframe.Core.Layout;
using Reframe.Services;

namespace Reframe.UI;

/// <summary>列表项包装:GridView 模板用 x:Bind 取 Model/摘要。</summary>
public sealed class LayoutItem
{
    public LayoutItem(Layout model) => Model = model;
    public Layout Model { get; }
    public string SummaryText => $"{Model.Zones.Count} 个分区 · {Model.RefWidth}×{Model.RefHeight}";
}

public sealed partial class LayoutsPage : Page
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<LayoutItem> _items = new();

    public LayoutsPage()
    {
        InitializeComponent();
        LayoutsGrid.ItemsSource = _items;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ConfigService.Instance.Changed += OnConfigChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ConfigService.Instance.Changed -= OnConfigChanged;
    }

    // Changed 可能在任意线程触发,UI 自行回到 UI 线程。
    private void OnConfigChanged() => _dispatcher.TryEnqueue(Refresh);

    private void Refresh()
    {
        string? keepId = SelectedLayout?.Id;

        _items.Clear();
        foreach (var l in ConfigService.Instance.Config.Layouts)
            _items.Add(new LayoutItem(l));

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 尽量保持原选中项。
        if (keepId is not null)
        {
            var again = _items.FirstOrDefault(i => i.Model.Id == keepId);
            if (again is not null) LayoutsGrid.SelectedItem = again;
        }
        UpdateCommandState();
    }

    private Layout? SelectedLayout => (LayoutsGrid.SelectedItem as LayoutItem)?.Model;

    private void UpdateCommandState()
    {
        bool has = SelectedLayout is not null;
        DuplicateButton.IsEnabled = has;
        EditButton.IsEnabled = has;
        DeleteButton.IsEnabled = has;
    }

    private void LayoutsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateCommandState();

    private void LayoutsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LayoutItem item)
            Frame.Navigate(typeof(LayoutEditorPage), item.Model.Id);
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayout is { } l)
            Frame.Navigate(typeof(LayoutEditorPage), l.Id);
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        var layout = new Layout
        {
            Name = "新布局",
            Zones = { new Zone { Name = "全屏", X = 0, Y = 0, W = 1, H = 1 } }
        };
        ConfigService.Instance.Config.Layouts.Add(layout);
        ConfigService.Instance.Save();   // 触发 Changed → Refresh
        // 直接进编辑器编辑新建的布局。
        Frame.Navigate(typeof(LayoutEditorPage), layout.Id);
    }

    private void DuplicateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayout is not { } src) return;

        var copy = new Layout
        {
            Name = src.Name + " 副本",
            RefWidth = src.RefWidth,
            RefHeight = src.RefHeight,
            Zones = src.Zones
                .Select(z => new Zone { Name = z.Name, X = z.X, Y = z.Y, W = z.W, H = z.H })
                .ToList()
        };
        ConfigService.Instance.Config.Layouts.Add(copy);
        ConfigService.Instance.Save();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayout is not { } target) return;

        var dialog = new ContentDialog
        {
            Title = "删除布局",
            Content = $"确定删除布局「{target.Name}」吗?引用它的规则会失效。此操作无法撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ConfigService.Instance.Config.Layouts.RemoveAll(l => l.Id == target.Id);
            ConfigService.Instance.Save();
        }
    }
}
