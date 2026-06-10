using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reframe.Core;
using Reframe.Services;

namespace Reframe.UI;

/// <summary>
/// 选取对话框的一行:运行中的某个顶层窗口。x:Bind 需要顶层 public 类(参考 ProfileRow)。
/// 进程图标尽力而为:拿不到就回落到默认字形(FallbackIcon 显隐互斥)。
/// </summary>
public sealed class WindowRow
{
    public required WindowInfo Window { get; init; }
    public string Title { get; init; } = "";
    public string ProcessLabel { get; init; } = "";   // 次要灰字:进程名.exe
    public string SizeLabel { get; init; } = "";       // 如 1280×860
    public ImageSource? Icon { get; init; }            // 取得到才有,否则 null → 显示回落字形

    public Visibility RealIconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackIconVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class WindowPickerDialog : ContentDialog
{
    private List<WindowRow> _all = new();

    /// <summary>点「创建」后,用户选中的窗口;取消则为 null。由调用方据此建 Profile。</summary>
    public WindowInfo? SelectedWindow { get; private set; }

    public WindowPickerDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadWindows();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadWindows();

    private void LoadWindows()
    {
        var rows = new List<WindowRow>();
        foreach (var w in WindowScanner.EnumerateTopLevel())
        {
            // 过滤掉 Reframe 自己(ProcessName 不含 .exe、小写)
            if (string.Equals(w.ProcessName, "reframe", StringComparison.OrdinalIgnoreCase))
                continue;

            rows.Add(new WindowRow
            {
                Window = w,
                Title = w.Title,
                ProcessLabel = string.IsNullOrEmpty(w.ProcessName) ? "(未知进程)" : w.ProcessName + ".exe",
                SizeLabel = SizeOf(w.Handle),
                // 图标提取已抽到共享的 IconCache;此处窗口都在运行,优先用 pid 入口(精确且顺便学路径)。
                Icon = IconCache.ByProcessId(w.ProcessId),
            });
        }

        // 标题排序,稳定可预期
        rows.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        _all = rows;
        ApplyFilter(SearchBox.Text);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    private void ApplyFilter(string? query)
    {
        IEnumerable<WindowRow> view = _all;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            view = _all.Where(r =>
                r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.ProcessLabel.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = view.ToList();
        WindowList.ItemsSource = list;
        bool empty = list.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        WindowList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = WindowList.SelectedItem is not null;
    }

    private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => IsPrimaryButtonEnabled = WindowList.SelectedItem is not null;

    private void OnCreateClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (WindowList.SelectedItem is WindowRow row)
            SelectedWindow = row.Window;
        else
            args.Cancel = true; // 没选中不应关闭
    }

    private static string SizeOf(IntPtr hwnd)
    {
        try
        {
            if (Interop.NativeMethods.GetWindowRect(hwnd, out var r))
            {
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w > 0 && h > 0) return $"{w}×{h}";
            }
        }
        catch { /* 忽略,显示空 */ }
        return "";
    }
}
