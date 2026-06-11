using Windows.UI;
using Microsoft.UI;

namespace Reframe.UI.Controls;

/// <summary>Zone-block coloring: a fixed palette of hues cycled by index, so adjacent zones in the same
/// layout get different colors.</summary>
public static class ZoneColors
{
    // A set of well-separated hues with similar brightness; the caller chooses opacity.
    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 0x4F, 0x8A, 0xD8), // blue
        Color.FromArgb(255, 0x6F, 0xB8, 0x5F), // green
        Color.FromArgb(255, 0xE0, 0x8A, 0x3C), // orange
        Color.FromArgb(255, 0xB5, 0x6F, 0xD8), // purple
        Color.FromArgb(255, 0xD8, 0x5C, 0x7A), // rose
        Color.FromArgb(255, 0x3C, 0xB5, 0xB0), // teal
        Color.FromArgb(255, 0xC9, 0xB4, 0x3C), // yellow
        Color.FromArgb(255, 0x7A, 0x86, 0xD8), // indigo
    };

    /// <summary>Base color by index (wraps via modulo).</summary>
    public static Color Base(int index)
        => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];

    /// <summary>Semi-transparent fill color by index.</summary>
    public static Color Fill(int index, byte alpha = 0x66)
    {
        var c = Base(index);
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    /// <summary>Solid stroke color by index.</summary>
    public static Color Stroke(int index)
    {
        var c = Base(index);
        return Color.FromArgb(0xCC, c.R, c.G, c.B);
    }
}
