using Reframe.Core;
using Reframe.Interop;
using Xunit;
using RECT = Reframe.Interop.NativeMethods.RECT;

namespace Reframe.Core.Tests;

/// <summary>
/// PlacementResolver.ZoneToRect(Zone z, RECT basis) 纯函数(agent-A 合同靶点)。
/// zone 比例(0..1,相对 basis)→ 绝对矩形(virtual-desktop 物理像素),口径:
/// Left = basis.Left + Round(z.X·bw),Right = basis.Left + Round((z.X+z.W)·bw),Top/Bottom 同理。
/// 这是 ResolveRect / DragSnap / Hotkey 三处共用的取整核;此处单独钉死它的精确性。
/// 不碰任何 Win32:直接喂入 basis 矩形。
/// </summary>
public class ZoneToRectTests
{
    private static RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    /// <summary>原点在 (ox,oy)、尺寸 w×h 的基准矩形。</summary>
    private static RECT Basis(int ox, int oy, int w, int h) => R(ox, oy, ox + w, oy + h);

    private static Zone Z(double x, double y, double w, double h) => new() { X = x, Y = y, W = w, H = h };

    private static void AssertRect(RECT a, int l, int t, int r, int b)
        => Assert.Equal((l, t, r, b), (a.Left, a.Top, a.Right, a.Bottom));

    // ---- 比例 × basis 的精确性 ----

    [Fact(DisplayName = "整块 zone (0,0,1,1) → 等于 basis 本身")]
    public void FullZone_EqualsBasis()
    {
        var basis = Basis(0, 0, 3840, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 1, 1), basis);
        AssertRect(rect, 0, 0, 3840, 2160);
    }

    [Fact(DisplayName = "半屏 zone (0,0,0.5,1) on 1920 宽 → (0,0,960,1080)")]
    public void HalfWidth_OnFhd()
    {
        var basis = Basis(0, 0, 1920, 1080);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 0.5, 1), basis);
        AssertRect(rect, 0, 0, 960, 1080);
    }

    [Fact(DisplayName = "游戏区 (0,0,2/3,1) on 7680 宽 → (0,0,5120,2160)")]
    public void GameZone_TwoThirds_Of7680()
    {
        var basis = Basis(0, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 2.0 / 3, 1), basis);
        AssertRect(rect, 0, 0, 5120, 2160);
    }

    [Fact(DisplayName = "副屏区 (2/3,0,1/3,1) on 7680 宽 → (5120,0,7680,2160)")]
    public void SideZone_LastThird_Of7680()
    {
        var basis = Basis(0, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(2.0 / 3, 0, 1.0 / 3, 1), basis);
        AssertRect(rect, 5120, 0, 7680, 2160);
    }

    [Fact(DisplayName = "四象限之一 (0.5,0.5,0.5,0.5) on 1920×1080 → (960,540,1920,1080)")]
    public void BottomRightQuadrant()
    {
        var basis = Basis(0, 0, 1920, 1080);
        var rect = PlacementResolver.ZoneToRect(Z(0.5, 0.5, 0.5, 0.5), basis);
        AssertRect(rect, 960, 540, 1920, 1080);
    }

    // ---- 非零原点 basis(屏不在 (0,0))----

    [Fact(DisplayName = "非零原点 basis:屏在 x=5120,游戏区 2/3 → (5120,0,10240,2160)")]
    public void NonZeroOrigin_GameZone()
    {
        // 主屏右侧第二块 7680 屏,左上角 (5120,0)
        var basis = Basis(5120, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 2.0 / 3, 1), basis);
        // Left = 5120 + Round(0) = 5120;Right = 5120 + Round(2/3·7680=5120) = 10240
        AssertRect(rect, 5120, 0, 10240, 2160);
    }

    [Fact(DisplayName = "非零原点 basis:含 Y 偏移,中段 zone (0.25,0.25,0.5,0.5)")]
    public void NonZeroOrigin_WithYOffset()
    {
        var basis = Basis(100, 200, 1920, 1080);
        var rect = PlacementResolver.ZoneToRect(Z(0.25, 0.25, 0.5, 0.5), basis);
        // Left = 100 + Round(0.25·1920=480) = 580;Top = 200 + Round(0.25·1080=270) = 470
        // Right = 100 + Round(0.75·1920=1440) = 1540;Bottom = 200 + Round(0.75·1080=810) = 1010
        AssertRect(rect, 580, 470, 1540, 1010);
    }

    [Fact(DisplayName = "负原点 basis:屏在主屏左侧 x=-7680,游戏区 → (-7680,0,-2560,2160)")]
    public void NegativeOrigin_GameZone()
    {
        var basis = Basis(-7680, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 2.0 / 3, 1), basis);
        AssertRect(rect, -7680, 0, -2560, 2160);
    }

    // ---- 2/3 × 7680 = 5120 无漂移(浮点 Round 精确性)----

    [Fact(DisplayName = "无漂移:0.6666...(2.0/3)× 7680 经 Round 精确得 5120,不是 5119/5121")]
    public void NoDrift_TwoThirds_Of7680_IsExactly5120()
    {
        var basis = Basis(0, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(0, 0, 2.0 / 3, 1), basis);
        Assert.Equal(5120, rect.Right);
    }

    [Fact(DisplayName = "无漂移:副屏区左边界 Round(2/3·7680) 同样精确得 5120")]
    public void NoDrift_SideZoneLeftEdge_IsExactly5120()
    {
        var basis = Basis(0, 0, 7680, 2160);
        var rect = PlacementResolver.ZoneToRect(Z(2.0 / 3, 0, 1.0 / 3, 1), basis);
        Assert.Equal(5120, rect.Left);
    }

    // ---- 相邻 zone 无缝(右边界 == 下一块左边界,无 1px 缝隙/重叠)----

    [Fact(DisplayName = "相邻无缝:游戏区右边界 == 副屏区左边界(7680 宽)")]
    public void Adjacent_GameAndSide_Seamless_On7680()
    {
        var basis = Basis(0, 0, 7680, 2160);
        var game = PlacementResolver.ZoneToRect(Z(0, 0, 2.0 / 3, 1), basis);
        var side = PlacementResolver.ZoneToRect(Z(2.0 / 3, 0, 1.0 / 3, 1), basis);
        Assert.Equal(game.Right, side.Left);   // 无缝:不重叠也不留缝
        Assert.Equal(5120, game.Right);
        Assert.Equal(7680, side.Right);         // 末块贴到屏右
    }

    [Fact(DisplayName = "相邻无缝:三等分 3840 → 边界 0|1280|2560|3840 两两相接")]
    public void Adjacent_Thirds_Of3840_Seamless()
    {
        var basis = Basis(0, 0, 3840, 2160);
        var z1 = PlacementResolver.ZoneToRect(Z(0,       0, 1.0 / 3, 1), basis);
        var z2 = PlacementResolver.ZoneToRect(Z(1.0 / 3, 0, 1.0 / 3, 1), basis);
        var z3 = PlacementResolver.ZoneToRect(Z(2.0 / 3, 0, 1.0 / 3, 1), basis);
        Assert.Equal((0, 1280), (z1.Left, z1.Right));
        Assert.Equal((1280, 2560), (z2.Left, z2.Right));
        Assert.Equal((2560, 3840), (z3.Left, z3.Right));
        // 相接:前块右 == 后块左
        Assert.Equal(z1.Right, z2.Left);
        Assert.Equal(z2.Right, z3.Left);
    }

    [Fact(DisplayName = "相邻无缝:竖直二等分 1080 高 → 上下块 (…,540) 与 (540,…) 相接")]
    public void Adjacent_VerticalHalves_Seamless()
    {
        var basis = Basis(0, 0, 1920, 1080);
        var top = PlacementResolver.ZoneToRect(Z(0, 0,   1, 0.5), basis);
        var bot = PlacementResolver.ZoneToRect(Z(0, 0.5, 1, 0.5), basis);
        Assert.Equal(540, top.Bottom);
        Assert.Equal(540, bot.Top);
        Assert.Equal(top.Bottom, bot.Top);
    }

    [Fact(DisplayName = "相邻无缝:非零原点屏上三等分仍两两相接(原点平移不破坏接缝)")]
    public void Adjacent_Thirds_NonZeroOrigin_Seamless()
    {
        var basis = Basis(5120, 0, 7680, 2160);
        var z1 = PlacementResolver.ZoneToRect(Z(0,       0, 1.0 / 3, 1), basis);
        var z2 = PlacementResolver.ZoneToRect(Z(1.0 / 3, 0, 1.0 / 3, 1), basis);
        var z3 = PlacementResolver.ZoneToRect(Z(2.0 / 3, 0, 1.0 / 3, 1), basis);
        Assert.Equal(z1.Right, z2.Left);
        Assert.Equal(z2.Right, z3.Left);
        Assert.Equal(5120, z1.Left);              // 首块贴屏左
        Assert.Equal(5120 + 7680, z3.Right);      // 末块贴屏右(12800)
    }
}
