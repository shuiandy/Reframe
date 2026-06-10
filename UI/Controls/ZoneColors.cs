using Windows.UI;
using Microsoft.UI;

namespace Reframe.UI.Controls;

/// <summary>分区色块取色:一组预定义色相按索引轮换,保证同一布局内相邻 zone 不同色。</summary>
public static class ZoneColors
{
    // 取一组区分度高、明度接近的色相;不透明度由调用方决定。
    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 0x4F, 0x8A, 0xD8), // 蓝
        Color.FromArgb(255, 0x6F, 0xB8, 0x5F), // 绿
        Color.FromArgb(255, 0xE0, 0x8A, 0x3C), // 橙
        Color.FromArgb(255, 0xB5, 0x6F, 0xD8), // 紫
        Color.FromArgb(255, 0xD8, 0x5C, 0x7A), // 玫红
        Color.FromArgb(255, 0x3C, 0xB5, 0xB0), // 青
        Color.FromArgb(255, 0xC9, 0xB4, 0x3C), // 黄
        Color.FromArgb(255, 0x7A, 0x86, 0xD8), // 靛
    };

    /// <summary>按索引取基色(自动取模轮换)。</summary>
    public static Color Base(int index)
        => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];

    /// <summary>按索引取半透明填充色。</summary>
    public static Color Fill(int index, byte alpha = 0x66)
    {
        var c = Base(index);
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    /// <summary>按索引取实色边框。</summary>
    public static Color Stroke(int index)
    {
        var c = Base(index);
        return Color.FromArgb(0xCC, c.R, c.G, c.B);
    }
}
