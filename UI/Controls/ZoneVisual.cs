using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Reframe.Core;
using Windows.UI;

namespace Reframe.UI.Controls;

/// <summary>
/// 编辑器画布上的一个分区视觉:可拖动的本体 + 名称/像素标签 + 8 个缩放手柄。
/// 几何状态以 Zone 的 0..1 比例为权威;交互时在画布像素空间计算,结束/进行中写回比例。
/// 取整只发生在像素标签显示,比例本身保持 double 不漂移。
/// </summary>
internal sealed class ZoneVisual
{
    private readonly LayoutEditorPage _page;
    private readonly int _index;

    // 本体既是色块也是移动手柄(Thumb 自带 DragDelta,delta 为 DIP)。
    private readonly Thumb _body = new();
    private readonly TextBlock _nameLabel = new();
    private readonly TextBlock _sizeLabel = new();
    private readonly List<Thumb> _handles = new();

    // 拖拽期间的画布像素矩形(权威值在 Zone 比例,这里是临时工作值)。
    private double _l, _t, _w, _h;

    public Zone Zone { get; }

    private const double HandleSize = 12;

    public ZoneVisual(LayoutEditorPage page, Zone zone, int index)
    {
        _page = page;
        Zone = zone;
        _index = index;
        BuildBody();
        BuildHandles();
    }

    public void AddTo(Canvas canvas)
    {
        canvas.Children.Add(_body);
        canvas.Children.Add(_nameLabel);
        canvas.Children.Add(_sizeLabel);
        foreach (var h in _handles) canvas.Children.Add(h);
    }

    // ---------- 构建本体 ----------
    private void BuildBody()
    {
        var fill = new SolidColorBrush(ZoneColors.Fill(_index, 0x55));
        var stroke = new SolidColorBrush(ZoneColors.Stroke(_index));

        // 用一个模板:Thumb 内部就是个矩形面板。WinUI Thumb 默认有视觉,
        // 这里替换成自定义,避免默认按钮外观。
        _body.Template = BuildBodyTemplate();
        _body.Background = fill;
        _body.BorderBrush = stroke;
        _body.ManipulationMode = ManipulationModes.None;

        _body.DragStarted += (_, _) => OnDragStart();
        _body.DragDelta += Body_DragDelta;
        _body.DragCompleted += (_, _) => OnDragEnd();
        // 点击本体即选中。
        _body.PointerPressed += (_, _) => _page.SelectZone(Zone);

        _nameLabel.Foreground = new SolidColorBrush(Colors.White);
        _nameLabel.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _nameLabel.IsHitTestVisible = false;
        _nameLabel.TextTrimming = TextTrimming.CharacterEllipsis;

        _sizeLabel.Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));
        _sizeLabel.FontSize = 11;
        _sizeLabel.IsHitTestVisible = false;
    }

    private static ControlTemplate BuildBodyTemplate()
    {
        // 边框色走 TemplateBinding,选中时直接改 Thumb.BorderBrush 即可,无需 GetTemplateChild。
        string xaml = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 TargetType='Thumb'>
  <Border Background='{TemplateBinding Background}'
          BorderBrush='{TemplateBinding BorderBrush}'
          BorderThickness='2' CornerRadius='3'/>
</ControlTemplate>";
        return (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    // ---------- 构建 8 个手柄 ----------
    // 方位:0=NW 1=N 2=NE 3=E 4=SE 5=S 6=SW 7=W
    private void BuildHandles()
    {
        for (int dir = 0; dir < 8; dir++)
        {
            int d = dir;
            var thumb = new Thumb
            {
                Width = HandleSize,
                Height = HandleSize,
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(ZoneColors.Stroke(_index)),
                Template = BuildHandleTemplate(),
            };
            thumb.DragStarted += (_, _) => OnDragStart();
            thumb.DragDelta += (s, e) => Handle_DragDelta(d, e);
            thumb.DragCompleted += (_, _) => OnDragEnd();
            thumb.PointerPressed += (_, _) => _page.SelectZone(Zone);
            ProtectedCursorFor(thumb, d);
            _handles.Add(thumb);
        }
    }

    private static ControlTemplate BuildHandleTemplate()
    {
        string xaml = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                 TargetType='Thumb'>
  <Border Background='{TemplateBinding Background}'
          BorderBrush='{TemplateBinding BorderBrush}'
          BorderThickness='1' CornerRadius='2'/>
</ControlTemplate>";
        return (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private static void ProtectedCursorFor(Thumb t, int dir)
    {
        var shape = dir switch
        {
            0 or 4 => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
            2 or 6 => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
            1 or 5 => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
            _      => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
        };
        // Thumb 没有公开 ProtectedCursor,用 PointerEntered 走 InputCursor 间接设置较繁琐,
        // 这里保持默认箭头即可(行为已正确)。预留 dir/shape 以便后续接入。
        _ = shape;
    }

    // ---------- 布局到画布像素 ----------
    public void Layout(double cw, double ch)
    {
        _l = Zone.X * cw;
        _t = Zone.Y * ch;
        _w = Zone.W * cw;
        _h = Zone.H * ch;
        ApplyToVisuals();
    }

    private void ApplyToVisuals()
    {
        _body.Width = Math.Max(1, _w);
        _body.Height = Math.Max(1, _h);
        Canvas.SetLeft(_body, _l);
        Canvas.SetTop(_body, _t);

        PlaceHandles();
        UpdateLabelsInternal();
    }

    private void PlaceHandles()
    {
        double half = HandleSize / 2;
        double cx = _l + _w / 2, cy = _t + _h / 2;
        double r = _l + _w, b = _t + _h;
        (double x, double y)[] pos =
        {
            (_l, _t),       // NW
            (cx, _t),       // N
            (r, _t),        // NE
            (r, cy),        // E
            (r, b),         // SE
            (cx, b),        // S
            (_l, b),        // SW
            (_l, cy),       // W
        };
        for (int i = 0; i < _handles.Count; i++)
        {
            Canvas.SetLeft(_handles[i], pos[i].x - half);
            Canvas.SetTop(_handles[i], pos[i].y - half);
            Canvas.SetZIndex(_handles[i], 50);
        }
        Canvas.SetZIndex(_body, 10);
    }

    public void UpdateLabels(double cw, double ch) => UpdateLabelsInternal();

    private void UpdateLabelsInternal()
    {
        _nameLabel.Text = Zone.Name;
        _nameLabel.MaxWidth = Math.Max(10, _w - 8);
        _nameLabel.Measure(new Windows.Foundation.Size(_w, _h));
        Canvas.SetLeft(_nameLabel, _l + (_w - _nameLabel.DesiredSize.Width) / 2);
        Canvas.SetTop(_nameLabel, _t + (_h - _nameLabel.DesiredSize.Height) / 2);
        Canvas.SetZIndex(_nameLabel, 20);

        // 右下角:换算后的像素尺寸(比例 × Ref,四舍五入)。
        int pw = (int)Math.Round(Zone.W * _page.RefWidthOf());
        int ph = (int)Math.Round(Zone.H * _page.RefHeightOf());
        _sizeLabel.Text = $"{pw}×{ph}";
        _sizeLabel.Measure(new Windows.Foundation.Size(_w, _h));
        Canvas.SetLeft(_sizeLabel, _l + _w - _sizeLabel.DesiredSize.Width - 4);
        Canvas.SetTop(_sizeLabel, _t + _h - _sizeLabel.DesiredSize.Height - 2);
        Canvas.SetZIndex(_sizeLabel, 20);
    }

    public void SetSelected(bool sel)
    {
        _body.BorderBrush = sel
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(ZoneColors.Stroke(_index));
        foreach (var h in _handles)
            h.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 拖拽 ----------
    private void OnDragStart()
    {
        _page.SelectZone(Zone);
        // 用当前比例重置工作矩形,避免累积误差。
        _l = Zone.X * _page.CanvasW;
        _t = Zone.Y * _page.CanvasH;
        _w = Zone.W * _page.CanvasW;
        _h = Zone.H * _page.CanvasH;
    }

    private void Body_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double cw = _page.CanvasW, ch = _page.CanvasH;
        double nl = _l + e.HorizontalChange;
        double nt = _t + e.VerticalChange;

        // 钳制在画布内。
        nl = Math.Clamp(nl, 0, cw - _w);
        nt = Math.Clamp(nt, 0, ch - _h);

        // 吸附:左右两边都尝试,取吸到的那条作参考线。
        var hEdges = _page.OtherEdges(Zone, horizontal: true);
        var vEdges = _page.OtherEdges(Zone, horizontal: false);

        double snappedL = _page.Snap(nl, cw, hEdges, out double? gL);
        double snappedR = _page.Snap(nl + _w, cw, hEdges, out double? gR);
        double? guideX = null;
        if (gL is not null) { nl = snappedL; guideX = gL; }
        else if (gR is not null) { nl = snappedR - _w; guideX = gR; }

        double snappedT = _page.Snap(nt, ch, vEdges, out double? gT);
        double snappedB = _page.Snap(nt + _h, ch, vEdges, out double? gB);
        double? guideY = null;
        if (gT is not null) { nt = snappedT; guideY = gT; }
        else if (gB is not null) { nt = snappedB - _h; guideY = gB; }

        nl = Math.Clamp(nl, 0, cw - _w);
        nt = Math.Clamp(nt, 0, ch - _h);

        _l = nl; _t = nt;
        CommitToZone();
        ApplyToVisuals();
        _page.ShowGuides(guideX, guideY);
    }

    private void Handle_DragDelta(int dir, DragDeltaEventArgs e)
    {
        double cw = _page.CanvasW, ch = _page.CanvasH;
        double left = _l, top = _t, right = _l + _w, bottom = _t + _h;

        bool west = dir is 0 or 6 or 7;
        bool east = dir is 2 or 3 or 4;
        bool north = dir is 0 or 1 or 2;
        bool south = dir is 4 or 5 or 6;

        var hEdges = _page.OtherEdges(Zone, horizontal: true);
        var vEdges = _page.OtherEdges(Zone, horizontal: false);
        double? guideX = null, guideY = null;

        if (west)
        {
            left = Math.Clamp(left + e.HorizontalChange, 0, right - 12);
            left = _page.Snap(left, cw, hEdges, out guideX);
            left = Math.Clamp(left, 0, right - 12);
        }
        if (east)
        {
            right = Math.Clamp(right + e.HorizontalChange, left + 12, cw);
            right = _page.Snap(right, cw, hEdges, out guideX);
            right = Math.Clamp(right, left + 12, cw);
        }
        if (north)
        {
            top = Math.Clamp(top + e.VerticalChange, 0, bottom - 12);
            top = _page.Snap(top, ch, vEdges, out guideY);
            top = Math.Clamp(top, 0, bottom - 12);
        }
        if (south)
        {
            bottom = Math.Clamp(bottom + e.VerticalChange, top + 12, ch);
            bottom = _page.Snap(bottom, ch, vEdges, out guideY);
            bottom = Math.Clamp(bottom, top + 12, ch);
        }

        _l = left; _t = top; _w = right - left; _h = bottom - top;
        CommitToZone();
        ApplyToVisuals();
        _page.ShowGuides(guideX, guideY);
    }

    private void OnDragEnd()
    {
        _page.ClearGuides();
        CommitToZone();
        _page.OnZoneGeometryChanged(Zone);
    }

    // 画布像素 → 0..1 比例写回(仅此处做除法,显示像素另行四舍五入)。
    private void CommitToZone()
    {
        double cw = _page.CanvasW, ch = _page.CanvasH;
        Zone.X = _l / cw;
        Zone.Y = _t / ch;
        Zone.W = _w / cw;
        Zone.H = _h / ch;
        _page.OnZoneGeometryChanged(Zone);
    }
}
