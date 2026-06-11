using Reframe.Core;
using Reframe.Interop;
using Xunit;
using RECT = Reframe.Interop.NativeMethods.RECT;

namespace Reframe.Core.Tests;

/// <summary>
/// PlacementResolver.ZoneToRect(Zone z, RECT basis) pure function (agent-A contract target).
/// Zone ratios (0..1, relative to basis) → an absolute rect (virtual-desktop physical pixels), per:
/// Left = basis.Left + Round(z.X·bw), Right = basis.Left + Round((z.X+z.W)·bw), Top/Bottom likewise.
/// This is the rounding core shared by ResolveRect / DragSnap / Hotkey; here its precision is pinned down separately.
/// No Win32: the basis rect is fed in directly.
/// </summary>
public class ZoneToRectTests
{
    private static RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    /// <summary>A basis rect with origin (ox,oy) and size w×h.</summary>
    private static RECT Basis(int ox, int oy, int w, int h) => R(ox, oy, ox + w, oy + h);

    private static Zone Z(double x, double y, double w, double h) => new() { X = x, Y = y, W = w, H = h };

    private static void AssertRect(RECT a, int l, int t, int r, int b)
        => Assert.Equal((l, t, r, b), (a.Left, a.Top, a.Right, a.Bottom));

    // ---- Precision of ratio × basis ----

    [Fact(DisplayName = "Full zone (0,0,1,1) → equals basis itself")]
    public void FullZone_EqualsBasis()
    {
        var basis = Basis(0, 0, 3840, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 1, 1), basis);
        AssertRect(rect, 0, 0, 3840, 2160);
    }

    [Fact(DisplayName = "Half-width zone (0,0,0.5,1) on 1920 wide → (0,0,960,1080)")]
    public void HalfWidth_OnFhd()
    {
        var basis = Basis(0, 0, 1920, 1080);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 0.5, 1), basis);
        AssertRect(rect, 0, 0, 960, 1080);
    }

    [Fact(DisplayName = "Game zone (0,0,2/3,1) on 7680 wide → (0,0,5120,2160)")]
    public void GameZone_TwoThirds_Of7680()
    {
        var basis = Basis(0, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 2.0 / 3, 1), basis);
        AssertRect(rect, 0, 0, 5120, 2160);
    }

    [Fact(DisplayName = "Secondary zone (2/3,0,1/3,1) on 7680 wide → (5120,0,7680,2160)")]
    public void SideZone_LastThird_Of7680()
    {
        var basis = Basis(0, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(2.0 / 3, 0, 1.0 / 3, 1), basis);
        AssertRect(rect, 5120, 0, 7680, 2160);
    }

    [Fact(DisplayName = "One of four quadrants (0.5,0.5,0.5,0.5) on 1920×1080 → (960,540,1920,1080)")]
    public void BottomRightQuadrant()
    {
        var basis = Basis(0, 0, 1920, 1080);
        var rect = PlacementResolver.ZoneToRect(Z(0.5, 0.5, 0.5, 0.5), basis);
        AssertRect(rect, 960, 540, 1920, 1080);
    }

    // ---- Non-zero-origin basis (monitor not at (0,0)) ----

    [Fact(DisplayName = "Non-zero-origin basis: monitor at x=5120, game zone 2/3 → (5120,0,10240,2160)")]
    public void NonZeroOrigin_GameZone()
    {
        // A second 7680 monitor to the right of the primary, top-left (5120,0)
        var basis = Basis(5120, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 2.0 / 3, 1), basis);
        // Left = 5120 + Round(0) = 5120; Right = 5120 + Round(2/3·7680=5120) = 10240
        AssertRect(rect, 5120, 0, 10240, 2160);
    }

    [Fact(DisplayName = "Non-zero-origin basis: with a Y offset, mid-range zone (0.25,0.25,0.5,0.5)")]
    public void NonZeroOrigin_WithYOffset()
    {
        var basis = Basis(100, 200, 1920, 1080);
        var rect = PlacementResolver.ZoneToRect(Z(0.25, 0.25, 0.5, 0.5), basis);
        // Left = 100 + Round(0.25·1920=480) = 580; Top = 200 + Round(0.25·1080=270) = 470
        // Right = 100 + Round(0.75·1920=1440) = 1540; Bottom = 200 + Round(0.75·1080=810) = 1010
        AssertRect(rect, 580, 470, 1540, 1010);
    }

    [Fact(DisplayName = "Negative-origin basis: monitor to the left of the primary x=-7680, game zone → (-7680,0,-2560,2160)")]
    public void NegativeOrigin_GameZone()
    {
        var basis = Basis(-7680, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 2.0 / 3, 1), basis);
        AssertRect(rect, -7680, 0, -2560, 2160);
    }

    // ---- 2/3 × 7680 = 5120 with no drift (floating-point Round precision) ----

    [Fact(DisplayName = "No drift: 0.6666...(2.0/3) × 7680 rounds exactly to 5120, not 5119/5121")]
    public void NoDrift_TwoThirds_Of7680_IsExactly5120()
    {
        var basis = Basis(0, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 2.0 / 3, 1), basis);
        Assert.Equal(5120, rect.Right);
    }

    [Fact(DisplayName = "No drift: secondary zone's left edge Round(2/3·7680) is likewise exactly 5120")]
    public void NoDrift_SideZoneLeftEdge_IsExactly5120()
    {
        var basis = Basis(0, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(2.0 / 3, 0, 1.0 / 3, 1), basis);
        Assert.Equal(5120, rect.Left);
    }

    // ---- Adjacent zones seamless (right edge == next zone's left edge, no 1px gap/overlap) ----

    [Fact(DisplayName = "Adjacent seamless: game zone's right edge == secondary zone's left edge (7680 wide)")]
    public void Adjacent_GameAndSide_Seamless_On7680()
    {
        var basis = Basis(0, 0, 7680, 2160);
        var game = PlacementResolver.ZoneToRect(Z(0, 0, 2.0 / 3, 1), basis);
        var side = PlacementResolver.ZoneToRect(Z(2.0 / 3, 0, 1.0 / 3, 1), basis);
        Assert.Equal(game.Right, side.Left);   // Seamless: no overlap, no gap
        Assert.Equal(5120, game.Right);
        Assert.Equal(7680, side.Right);         // Last zone flush to the screen's right
    }

    [Fact(DisplayName = "Adjacent seamless: three-way split of 3840 → edges 0|1280|2560|3840 meet pairwise")]
    public void Adjacent_Thirds_Of3840_Seamless()
    {
        var basis = Basis(0, 0, 3840, 2160);
        var z1 = PlacementResolver.ZoneToRect(Z(0,       0, 1.0 / 3, 1), basis);
        var z2 = PlacementResolver.ZoneToRect(Z(1.0 / 3, 0, 1.0 / 3, 1), basis);
        var z3 = PlacementResolver.ZoneToRect(Z(2.0 / 3, 0, 1.0 / 3, 1), basis);
        Assert.Equal((0, 1280), (z1.Left, z1.Right));
        Assert.Equal((1280, 2560), (z2.Left, z2.Right));
        Assert.Equal((2560, 3840), (z3.Left, z3.Right));
        // Meeting: previous zone's right == next zone's left
        Assert.Equal(z1.Right, z2.Left);
        Assert.Equal(z2.Right, z3.Left);
    }

    [Fact(DisplayName = "Adjacent seamless: vertical halves of 1080 high → top/bottom zones (…,540) and (540,…) meet")]
    public void Adjacent_VerticalHalves_Seamless()
    {
        var basis = Basis(0, 0, 1920, 1080);
        var top = PlacementResolver.ZoneToRect(Z(0, 0,   1, 0.5), basis);
        var bot = PlacementResolver.ZoneToRect(Z(0, 0.5, 1, 0.5), basis);
        Assert.Equal(540, top.Bottom);
        Assert.Equal(540, bot.Top);
        Assert.Equal(top.Bottom, bot.Top);
    }

    [Fact(DisplayName = "Adjacent seamless: a three-way split on a non-zero-origin monitor still meets pairwise (origin shift doesn't break the seams)")]
    public void Adjacent_Thirds_NonZeroOrigin_Seamless()
    {
        var basis = Basis(5120, 0, 7680, 2160);
        var z1 = PlacementResolver.ZoneToRect(Z(0,       0, 1.0 / 3, 1), basis);
        var z2 = PlacementResolver.ZoneToRect(Z(1.0 / 3, 0, 1.0 / 3, 1), basis);
        var z3 = PlacementResolver.ZoneToRect(Z(2.0 / 3, 0, 1.0 / 3, 1), basis);
        Assert.Equal(z1.Right, z2.Left);
        Assert.Equal(z2.Right, z3.Left);
        Assert.Equal(5120, z1.Left);              // First zone flush to the screen's left
        Assert.Equal(5120 + 7680, z3.Right);      // Last zone flush to the screen's right (12800)
    }
}
