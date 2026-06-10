using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Reframe.Core;
using Layout = Reframe.Core.Layout;

namespace Reframe.UI.Controls;

/// <summary>
/// 布局迷你预览:按 RefWidth:RefHeight 比例在给定框内画一个矩形,
/// 内部用半透明色块 + 名字画出各 Zone。纯代码绘制,放进列表卡片即可。
/// </summary>
public sealed class LayoutPreview : ContentControl
{
    private readonly Canvas _canvas = new();
    private readonly Border _frame;

    public LayoutPreview()
    {
        _frame = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.25 },
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = _canvas,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Content = _frame;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _canvas.SizeChanged += (_, _) => Render();
    }

    public static readonly DependencyProperty LayoutProperty =
        DependencyProperty.Register(nameof(Layout), typeof(Layout), typeof(LayoutPreview),
            new PropertyMetadata(null, (d, _) => ((LayoutPreview)d).Render()));

    public Layout? Layout
    {
        get => (Layout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>预览里是否绘制 zone 名称(列表小卡片可关掉省地方)。</summary>
    public bool ShowLabels { get; set; } = true;

    private void Render()
    {
        _canvas.Children.Clear();

        var layout = Layout;
        double cw = _canvas.ActualWidth, ch = _canvas.ActualHeight;
        if (layout is null || cw <= 0 || ch <= 0) return;

        // 在画布内按 Ref 宽高比 letterbox 出实际绘制区。
        double refW = layout.RefWidth > 0 ? layout.RefWidth : 16;
        double refH = layout.RefHeight > 0 ? layout.RefHeight : 9;
        double aspect = refW / refH;

        double boxW = cw, boxH = cw / aspect;
        if (boxH > ch) { boxH = ch; boxW = ch * aspect; }
        double ox = (cw - boxW) / 2, oy = (ch - boxH) / 2;

        for (int i = 0; i < layout.Zones.Count; i++)
        {
            var z = layout.Zones[i];
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = Math.Max(1, z.W * boxW),
                Height = Math.Max(1, z.H * boxH),
                Fill = new SolidColorBrush(ZoneColors.Fill(i, 0x70)),
                Stroke = new SolidColorBrush(ZoneColors.Stroke(i)),
                StrokeThickness = 1,
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(rect, ox + z.X * boxW);
            Canvas.SetTop(rect, oy + z.Y * boxH);
            _canvas.Children.Add(rect);

            if (ShowLabels && !string.IsNullOrEmpty(z.Name) && z.W * boxW > 24 && z.H * boxH > 14)
            {
                var label = new TextBlock
                {
                    Text = z.Name,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = z.W * boxW - 4,
                    HorizontalTextAlignment = TextAlignment.Center,
                };
                // 粗略居中(测量后再调,避免依赖布局过程)。
                label.Measure(new Windows.Foundation.Size(z.W * boxW, z.H * boxH));
                double lx = ox + z.X * boxW + (z.W * boxW - label.DesiredSize.Width) / 2;
                double ly = oy + z.Y * boxH + (z.H * boxH - label.DesiredSize.Height) / 2;
                Canvas.SetLeft(label, lx);
                Canvas.SetTop(label, ly);
                _canvas.Children.Add(label);
            }
        }
    }
}
