using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Reframe.Core;
using Reframe.Services;
using Windows.Foundation;
using Windows.Graphics;

namespace Reframe.UI;

/// <summary>
/// 截图式选区:在指定显示器上开全屏无边框置顶遮罩,拖拽出矩形。
/// 返回相对该显示器左上角的物理像素矩形;Esc / 右键取消 → null。
/// </summary>
public sealed partial class RegionPickerWindow : Window
{
    private readonly MonitorDesc _monitor;
    private readonly TaskCompletionSource<RectPx?> _tcs = new();

    private bool _dragging;
    private Point _startDip;     // 起点(DIP,相对 Root)
    private Point _currentDip;

    private RegionPickerWindow(MonitorDesc monitor)
    {
        InitializeComponent();
        _monitor = monitor;

        ConfigureWindow();

        // 十字光标。
        Root.SetCursor(InputSystemCursor.Create(InputSystemCursorShape.Cross));

        // 键盘:Esc 取消。需要焦点,Activated 后设。
        Root.KeyDown += Root_KeyDown;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    /// <summary>在指定显示器上全屏遮罩拖拽选区。返回物理像素矩形;取消 → null。</summary>
    public static Task<RectPx?> PickAsync(MonitorDesc monitor)
    {
        var win = new RegionPickerWindow(monitor);
        win.Activate();
        return win._tcs.Task;
    }

    private void ConfigureWindow()
    {
        var appWindow = AppWindow;

        // 无边框 + 置顶 + 不可调。
        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }

        // 覆盖整块屏(物理像素 = MonitorDesc 的 rcMonitor)。
        appWindow.MoveAndResize(new RectInt32(_monitor.X, _monitor.Y, _monitor.Width, _monitor.Height));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // 确保能接收键盘事件。
        Root.Focus(FocusState.Programmatic);
    }

    // 当前 DPI 缩放(DIP → 物理像素)。窗口覆盖整屏,Root 与窗口同尺寸,
    // 所以 Root 内 DIP 偏移 × scale = 屏内物理像素偏移。
    private double Scale => Root.XamlRoot?.RasterizationScale ?? 1.0;

    // ---------- 指针 ----------
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(Root);
        // 仅左键开始框选(右键交给 RightTapped 取消)。
        if (pt.Properties.IsRightButtonPressed) return;

        _dragging = true;
        _startDip = pt.Position;
        _currentDip = pt.Position;
        Root.CapturePointer(e.Pointer);
        UpdateSelectionVisual();
        SelectionRect.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _currentDip = e.GetCurrentPoint(Root).Position;
        UpdateSelectionVisual();
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);
        _currentDip = e.GetCurrentPoint(Root).Position;

        var result = ToPhysicalRect();
        // 太小视为误触 → 取消。
        if (result is null || result.W < 2 || result.H < 2)
            Finish(null);
        else
            Finish(result);
    }

    private void Root_RightTapped(object sender, RightTappedRoutedEventArgs e) => Finish(null);

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Finish(null);
        }
    }

    // ---------- 视觉 ----------
    private void UpdateSelectionVisual()
    {
        double x = Math.Min(_startDip.X, _currentDip.X);
        double y = Math.Min(_startDip.Y, _currentDip.Y);
        double w = Math.Abs(_currentDip.X - _startDip.X);
        double h = Math.Abs(_currentDip.Y - _startDip.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;

        // 徽标显示物理像素尺寸。
        double scale = Scale;
        int pw = (int)Math.Round(w * scale);
        int ph = (int)Math.Round(h * scale);
        SizeText.Text = $"宽 {pw} × 高 {ph} px";

        // 徽标跟随选区右下角(不出界)。
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bx = x + w + 8;
        double by = y + h + 8;
        double rootW = Root.ActualWidth, rootH = Root.ActualHeight;
        if (bx + SizeBadge.DesiredSize.Width > rootW) bx = x + w - SizeBadge.DesiredSize.Width;
        if (by + SizeBadge.DesiredSize.Height > rootH) by = y + h - SizeBadge.DesiredSize.Height - 4;
        Canvas.SetLeft(SizeBadge, Math.Max(0, bx));
        Canvas.SetTop(SizeBadge, Math.Max(0, by));
    }

    // DIP 选区 → 物理像素矩形(相对该显示器左上角)。
    private RectPx? ToPhysicalRect()
    {
        double scale = Scale;
        double x = Math.Min(_startDip.X, _currentDip.X);
        double y = Math.Min(_startDip.Y, _currentDip.Y);
        double w = Math.Abs(_currentDip.X - _startDip.X);
        double h = Math.Abs(_currentDip.Y - _startDip.Y);

        // 钳制在屏内(物理像素)。
        int px = (int)Math.Round(x * scale);
        int py = (int)Math.Round(y * scale);
        int pw = (int)Math.Round(w * scale);
        int ph = (int)Math.Round(h * scale);

        px = Math.Clamp(px, 0, _monitor.Width);
        py = Math.Clamp(py, 0, _monitor.Height);
        pw = Math.Clamp(pw, 0, _monitor.Width - px);
        ph = Math.Clamp(ph, 0, _monitor.Height - py);

        return new RectPx { X = px, Y = py, W = pw, H = ph };
    }

    // ---------- 收尾 ----------
    private void Finish(RectPx? result)
    {
        // 防重复(释放与按键可能并发)。
        if (_tcs.Task.IsCompleted) { Close(); return; }
        _tcs.TrySetResult(result);
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // 若未结果就关掉(例如外部 Close),按取消处理。
        _tcs.TrySetResult(null);
    }
}
