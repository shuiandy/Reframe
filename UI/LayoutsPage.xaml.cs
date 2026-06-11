using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Reframe.Core;
using Layout = Reframe.Core.Layout;
using Reframe.Services;

namespace Reframe.UI;

/// <summary>List-item wrapper: the GridView template binds to Model/summary via x:Bind.</summary>
public sealed class LayoutItem
{
    public LayoutItem(Layout model) => Model = model;
    public Layout Model { get; }
    public string SummaryText =>
        Loc.T("LayoutsPage/SummaryFormat", Model.Zones.Count, Model.RefWidth, Model.RefHeight);
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

    // Changed can fire on any thread; the UI marshals itself back onto the UI thread.
    private void OnConfigChanged() => _dispatcher.TryEnqueue(Refresh);

    private void Refresh()
    {
        string? keepId = SelectedLayout?.Id;

        _items.Clear();
        foreach (var l in ConfigService.Instance.Config.Layouts)
            _items.Add(new LayoutItem(l));

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Preserve the previously selected item where possible.
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

    // Double-click = open the editor. Walk up from the event source to the GridViewItem hosting the
    // item, then read its Content to get the corresponding layout.
    private void LayoutsGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = ItemFromEventSource(e.OriginalSource as DependencyObject);
        if (item is not null)
            Frame.Navigate(typeof(LayoutEditorPage), item.Model.Id);
    }

    // Right-click: select the card before the ContextFlyout opens, so the edit/duplicate/delete
    // handlers (which reuse SelectedLayout) act on the correct item.
    private void LayoutCard_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LayoutItem item)
            LayoutsGrid.SelectedItem = item;
    }

    private LayoutItem? ItemFromEventSource(DependencyObject? src)
    {
        while (src is not null && src is not GridViewItem)
            src = VisualTreeHelper.GetParent(src);
        return (src as GridViewItem)?.Content as LayoutItem;
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayout is { } l)
            Frame.Navigate(typeof(LayoutEditorPage), l.Id);
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        // Default name and the seed zone name are localized at creation time and written into user
        // data as-is; existing data is never re-translated (see docs/dev/I18N.md section 8).
        var layout = new Layout
        {
            Name = Loc.T("LayoutsPage/NewLayoutName"),
            Zones = { new Zone { Name = Loc.T("LayoutsPage/NewLayoutZoneName"), X = 0, Y = 0, W = 1, H = 1 } }
        };
        ConfigService.Instance.Config.Layouts.Add(layout);
        ConfigService.Instance.Save();   // raises Changed -> Refresh
        // Jump straight into the editor for the newly created layout.
        Frame.Navigate(typeof(LayoutEditorPage), layout.Id);
    }

    private void DuplicateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayout is not { } src) return;

        var copy = new Layout
        {
            Name = Loc.T("LayoutsPage/DuplicateNameFormat", src.Name),
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

        var cfg = ConfigService.Instance.Config;
        // Count affected profiles: any profile with a rule referencing this layout.
        int affected = cfg.Profiles.Count(p => p.Rules.Any(r => r.LayoutId == target.Id));

        string body = affected == 0
            ? Loc.T("LayoutsPage/DeleteConfirm", target.Name)
            : Loc.T("LayoutsPage/DeleteConfirmCascade", target.Name, affected);

        var dialog = new ContentDialog
        {
            Title = Loc.T("LayoutsPage/DeleteDialogTitle"),
            Content = body,
            PrimaryButtonText = Loc.T("Common/Delete"),
            CloseButtonText = Loc.T("Common/Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // Cascade cleanup: remove whole rules that reference this layout (no dangling references),
        // then delete the layout, and save once at the end.
        foreach (var p in cfg.Profiles)
            p.Rules.RemoveAll(r => r.LayoutId == target.Id);
        cfg.Layouts.RemoveAll(l => l.Id == target.Id);
        ConfigService.Instance.Save();
    }
}
