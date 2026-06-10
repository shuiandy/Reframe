using Reframe.Core;
using Reframe.Interop;
using Xunit;
using RECT = Reframe.Interop.NativeMethods.RECT;

namespace Reframe.Core.Tests;

/// <summary>
/// PlacementResolver.ResolveRect 纯几何核(M3 契约靶点)。
/// 不碰任何 Win32:直接喂入 rcMonitor / rcWork / currentWindowRect。
/// 验证:Fullscreen、UseWorkArea、Zone 比例→像素、非零原点偏移、CustomRect、
/// 规则表顺序、Offsets 叠加、KeepAspectRatio letterbox、无累计漂移。
/// </summary>
public class ResolveRectTests
{
    // ---- 构造辅助 ----

    private static RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    /// <summary>一块原点在 (ox,oy)、尺寸 w×h 的屏。</summary>
    private static RECT Mon(int ox, int oy, int w, int h) => R(ox, oy, ox + w, oy + h);

    private const string LayoutId = "LAYOUT-A";
    private const string GameZoneId = "ZONE-GAME";   // (0,0,2/3,1)
    private const string SideZoneId = "ZONE-SIDE";   // (2/3,0,1/3,1)

    /// <summary>含 57″ 布局(游戏区 2/3 宽 + 副屏区 1/3 宽)的配置。</summary>
    private static AppConfig CfgWithZones() => new()
    {
        Layouts =
        {
            new Layout
            {
                Id = LayoutId,
                RefWidth = 7680,
                RefHeight = 2160,
                Zones =
                {
                    new Zone { Id = GameZoneId, Name = "游戏区", X = 0,       Y = 0, W = 2.0 / 3, H = 1 },
                    new Zone { Id = SideZoneId, Name = "副屏区", X = 2.0 / 3, Y = 0, W = 1.0 / 3, H = 1 },
                }
            }
        }
    };

    private static Profile ProfWithRules(params PlacementRule[] rules)
    {
        var p = new Profile { Borderless = true };
        p.Rules.AddRange(rules);
        return p;
    }

    private static PlacementRule FullscreenRule(int w = 0, int h = 0, bool useWork = false)
        => new() { Monitor = new MonitorFilter { Width = w, Height = h }, Kind = PlacementKind.Fullscreen, UseWorkArea = useWork };

    private static PlacementRule ZoneRule(string zoneId, int w = 0, int h = 0, bool useWork = false, bool moveOnly = false)
        => new()
        {
            Monitor = new MonitorFilter { Width = w, Height = h },
            Kind = PlacementKind.Zone,
            LayoutId = LayoutId,
            ZoneId = zoneId,
            UseWorkArea = useWork,
            MoveOnly = moveOnly
        };

    private static void AssertRect(RECT? actual, int l, int t, int r, int b)
    {
        Assert.NotNull(actual);
        var a = actual!.Value;
        Assert.Equal((l, t, r, b), (a.Left, a.Top, a.Right, a.Bottom));
    }

    // ---- Fullscreen / WorkArea ----

    [Fact(DisplayName = "Fullscreen:整屏铺满 rcMonitor")]
    public void Fullscreen_WholeMonitor()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var work = R(0, 0, 7680, 2112);
        var p = ProfWithRules(FullscreenRule());
        var rect = PlacementResolver.ResolveRect(mon, work, R(0, 0, 100, 100), p, new AppConfig());
        AssertRect(rect, 0, 0, 7680, 2160);
    }

    [Fact(DisplayName = "Fullscreen + UseWorkArea:铺满工作区(避开任务栏)")]
    public void Fullscreen_UseWorkArea()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var work = R(0, 0, 7680, 2112); // 任务栏 48px
        var p = ProfWithRules(FullscreenRule(useWork: true));
        var rect = PlacementResolver.ResolveRect(mon, work, R(0, 0, 100, 100), p, new AppConfig());
        AssertRect(rect, 0, 0, 7680, 2112);
    }

    // ---- Zone 比例 → 像素 ----

    [Fact(DisplayName = "Zone 游戏区:7680×2160 + 2/3 宽 → (0,0,5120,2160)")]
    public void Zone_GameArea_FullMonitor()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, CfgWithZones());
        AssertRect(rect, 0, 0, 5120, 2160);
    }

    [Fact(DisplayName = "Zone 副屏区:7680×2160 + 1/3 宽起于 2/3 → (5120,0,7680,2160)")]
    public void Zone_SideArea_FullMonitor()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(SideZoneId));
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, CfgWithZones());
        // Left = Round(2/3 * 7680) = 5120;Right = Round(1 * 7680) = 7680
        AssertRect(rect, 5120, 0, 7680, 2160);
    }

    [Fact(DisplayName = "Zone + UseWorkArea:高度按 rcWork(2160-48=2112)")]
    public void Zone_UseWorkArea_HeightFromWork()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var work = R(0, 0, 7680, 2112);
        var p = ProfWithRules(ZoneRule(GameZoneId, useWork: true));
        var rect = PlacementResolver.ResolveRect(mon, work, R(0, 0, 100, 100), p, CfgWithZones());
        // 宽度仍按工作区宽 7680 算(此处工作区宽==屏宽);高度到 2112
        AssertRect(rect, 0, 0, 5120, 2112);
    }

    [Fact(DisplayName = "Zone 在非零原点显示器:偏移正确(屏置于 x=5120)")]
    public void Zone_NonZeroOriginMonitor()
    {
        // 一块 7680 宽的屏,左上角在 (5120,0)——例如主屏右侧
        var mon = Mon(5120, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        var rect = PlacementResolver.ResolveRect(mon, mon, R(5120, 0, 5220, 100), p, CfgWithZones());
        // Left = 5120 + Round(0) = 5120;Right = 5120 + Round(2/3*7680=5120) = 10240
        AssertRect(rect, 5120, 0, 10240, 2160);
    }

    [Fact(DisplayName = "Zone 在负原点显示器(屏在主屏左侧 x=-7680)")]
    public void Zone_NegativeOriginMonitor()
    {
        var mon = Mon(-7680, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        var rect = PlacementResolver.ResolveRect(mon, mon, R(-7680, 0, -7580, 100), p, CfgWithZones());
        AssertRect(rect, -7680, 0, -2560, 2160);
    }

    // ---- CustomRect ----

    [Fact(DisplayName = "CustomRect:相对屏原点的绝对矩形(零原点)")]
    public void CustomRect_ZeroOrigin()
    {
        var mon = Mon(0, 0, 3840, 2160);
        var p = ProfWithRules(new PlacementRule
        {
            Monitor = new MonitorFilter(),
            Kind = PlacementKind.CustomRect,
            CustomRect = new RectPx { X = 100, Y = 50, W = 1280, H = 720 }
        });
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        AssertRect(rect, 100, 50, 1380, 770);
    }

    [Fact(DisplayName = "CustomRect:在非零原点屏上叠加屏原点")]
    public void CustomRect_NonZeroOrigin()
    {
        var mon = Mon(5120, 0, 3840, 2160);
        var p = ProfWithRules(new PlacementRule
        {
            Monitor = new MonitorFilter(),
            Kind = PlacementKind.CustomRect,
            CustomRect = new RectPx { X = 10, Y = 20, W = 200, H = 100 }
        });
        var rect = PlacementResolver.ResolveRect(mon, mon, R(5120, 0, 5220, 100), p, new AppConfig());
        AssertRect(rect, 5130, 20, 5330, 120);
    }

    // ---- None / 无规则 ----

    [Fact(DisplayName = "Kind=None:命中但不产出矩形 → null")]
    public void None_ReturnsNull()
    {
        var mon = Mon(0, 0, 1920, 1080);
        var p = ProfWithRules(new PlacementRule { Monitor = new MonitorFilter(), Kind = PlacementKind.None });
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        Assert.Null(rect);
    }

    [Fact(DisplayName = "无规则命中:所有规则 Monitor 都不匹配 → null")]
    public void NoRuleMatches_ReturnsNull()
    {
        var mon = Mon(0, 0, 1920, 1080);
        var p = ProfWithRules(FullscreenRule(7680, 2160)); // 只对 7680×2160 生效
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        Assert.Null(rect);
    }

    [Fact(DisplayName = "空规则表 → null")]
    public void EmptyRules_ReturnsNull()
    {
        var mon = Mon(0, 0, 1920, 1080);
        var p = ProfWithRules();
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        Assert.Null(rect);
    }

    // ---- 规则表顺序:第一条命中生效 ----

    [Fact(DisplayName = "规则顺序:本地 57″ 命中首条 Zone 规则(而非末条 Fullscreen)")]
    public void RuleOrder_FirstMatchWins_Local()
    {
        var cfg = CfgWithZones();
        var p = ProfWithRules(
            ZoneRule(GameZoneId, 7680, 2160, useWork: true), // 仅本地 57″
            FullscreenRule());                                // 任意屏兜底
        var mon = Mon(0, 0, 7680, 2160);
        var work = R(0, 0, 7680, 2112);
        var rect = PlacementResolver.ResolveRect(mon, work, R(0, 0, 100, 100), p, cfg);
        // 首条 Zone+UseWorkArea 生效
        AssertRect(rect, 0, 0, 5120, 2112);
    }

    [Fact(DisplayName = "规则顺序:VDD 其它分辨率落到末条 Fullscreen 兜底")]
    public void RuleOrder_FallbackFullscreen_Vdd()
    {
        var cfg = CfgWithZones();
        var p = ProfWithRules(
            ZoneRule(GameZoneId, 7680, 2160, useWork: true),
            FullscreenRule());
        var mon = Mon(0, 0, 2560, 1440); // VDD 串流分辨率,首条不命中
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, cfg);
        AssertRect(rect, 0, 0, 2560, 1440);
    }

    [Fact(DisplayName = "规则顺序:两条都命中时取靠前那条")]
    public void RuleOrder_BothMatch_TakesFirst()
    {
        var cfg = CfgWithZones();
        var p = ProfWithRules(
            ZoneRule(GameZoneId),     // Monitor 0×0 任意,先命中
            FullscreenRule());        // 也任意,但在后
        var mon = Mon(0, 0, 7680, 2160);
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, cfg);
        AssertRect(rect, 0, 0, 5120, 2160); // Zone 生效,不是 Fullscreen
    }

    // ---- Zone 规则但找不到 Zone(脏配置)----

    [Fact(DisplayName = "Zone 规则但 LayoutId/ZoneId 找不到 → null(不崩)")]
    public void Zone_MissingZone_ReturnsNull()
    {
        var p = ProfWithRules(new PlacementRule
        {
            Monitor = new MonitorFilter(),
            Kind = PlacementKind.Zone,
            LayoutId = "NOPE",
            ZoneId = "NOPE"
        });
        var mon = Mon(0, 0, 7680, 2160);
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, CfgWithZones());
        Assert.Null(rect);
    }

    // ---- Offsets 四边叠加 ----

    [Fact(DisplayName = "Offsets:四边偏移分别叠加到 L/T/R/B")]
    public void Offsets_AppliedToAllFourEdges()
    {
        var mon = Mon(0, 0, 1920, 1080);
        var p = ProfWithRules(FullscreenRule());
        p.Offsets = new Offsets { Left = 10, Top = 20, Right = -30, Bottom = -40 };
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        // (0+10, 0+20, 1920-30, 1080-40)
        AssertRect(rect, 10, 20, 1890, 1040);
    }

    [Fact(DisplayName = "Offsets:Zone 之上叠加偏移")]
    public void Offsets_OnTopOfZone()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        p.Offsets = new Offsets { Left = 5, Top = 0, Right = 0, Bottom = -8 };
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, CfgWithZones());
        AssertRect(rect, 5, 0, 5120, 2152);
    }

    [Fact(DisplayName = "Offsets:None 不产出矩形时偏移无作用(仍 null)")]
    public void Offsets_NoEffectWhenNull()
    {
        var mon = Mon(0, 0, 1920, 1080);
        var p = ProfWithRules(new PlacementRule { Monitor = new MonitorFilter(), Kind = PlacementKind.None });
        p.Offsets = new Offsets { Left = 10, Top = 10, Right = 10, Bottom = 10 };
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        Assert.Null(rect);
    }

    // ---- KeepAspectRatio letterbox ----

    [Fact(DisplayName = "KeepAspectRatio:16:9 窗口放进 5120×2160(≈21:9)zone → 等比 3840×2160 居中")]
    public void KeepAspectRatio_16x9_Into_UltraWide()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        p.KeepAspectRatio = true;
        // 当前窗口是 16:9(1920×1080)
        var cur = R(0, 0, 1920, 1080);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, CfgWithZones());
        // zone 5120×2160;16:9 等比塞入:高度顶满 2160,宽 = 2160*16/9 = 3840
        // 居中:left = (5120-3840)/2 = 640
        AssertRect(rect, 640, 0, 4480, 2160);
    }

    [Fact(DisplayName = "KeepAspectRatio:窗口比 zone 更宽 → 宽度顶满,上下黑边")]
    public void KeepAspectRatio_WideContent_VerticalLetterbox()
    {
        var mon = Mon(0, 0, 3840, 2160);
        var p = ProfWithRules(FullscreenRule()); // zone = 3840×2160 (16:9)
        p.KeepAspectRatio = true;
        // 当前窗口是 32:9(3840×1080)——比 16:9 更宽
        var cur = R(0, 0, 3840, 1080);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, new AppConfig());
        // 宽度顶满 3840,高 = 3840 * 1080/3840 = 1080;居中 top=(2160-1080)/2=540
        AssertRect(rect, 0, 540, 3840, 1620);
    }

    [Fact(DisplayName = "KeepAspectRatio:同比例窗口 → 充满不变形")]
    public void KeepAspectRatio_SameRatio_FillsExactly()
    {
        var mon = Mon(0, 0, 3840, 2160);
        var p = ProfWithRules(FullscreenRule());
        p.KeepAspectRatio = true;
        var cur = R(0, 0, 1920, 1080); // 16:9 == zone 16:9
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, new AppConfig());
        AssertRect(rect, 0, 0, 3840, 2160);
    }

    [Fact(DisplayName = "KeepAspectRatio:当前窗口宽高非法(0)时原样返回目标矩形")]
    public void KeepAspectRatio_DegenerateCurrent_ReturnsOuter()
    {
        var mon = Mon(0, 0, 3840, 2160);
        var p = ProfWithRules(FullscreenRule());
        p.KeepAspectRatio = true;
        var cur = R(0, 0, 0, 0); // 宽高 0
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, new AppConfig());
        AssertRect(rect, 0, 0, 3840, 2160);
    }

    [Fact(DisplayName = "KeepAspectRatio=false:不做 letterbox,直接铺满")]
    public void KeepAspectRatio_Off_NoLetterbox()
    {
        var mon = Mon(0, 0, 3840, 2160);
        var p = ProfWithRules(FullscreenRule());
        p.KeepAspectRatio = false;
        var cur = R(0, 0, 1920, 1080);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, new AppConfig());
        AssertRect(rect, 0, 0, 3840, 2160);
    }

    // ---- 比例换算无累计漂移 ----

    [Fact(DisplayName = "无累计漂移:0.6666... × 7680 经 Round 精确得 5120")]
    public void NoFloatDrift_TwoThirds_Of_7680()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, CfgWithZones());
        Assert.NotNull(rect);
        // 右边界精确 5120,不是 5119/5121
        Assert.Equal(5120, rect!.Value.Right);
    }

    [Fact(DisplayName = "无累计漂移:游戏区 + 副屏区右边界相接无缝/不重叠(5120 | 5120→7680)")]
    public void NoFloatDrift_AdjacentZonesTileExactly()
    {
        var cfg = CfgWithZones();
        var mon = Mon(0, 0, 7680, 2160);

        var game = ProfWithRules(ZoneRule(GameZoneId));
        var side = ProfWithRules(ZoneRule(SideZoneId));

        var g = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), game, cfg)!.Value;
        var s = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), side, cfg)!.Value;

        // 游戏区右边界 == 副屏区左边界:无缝平铺、无 1px 缝隙或重叠
        Assert.Equal(g.Right, s.Left);
        Assert.Equal(5120, g.Right);
        Assert.Equal(7680, s.Right); // 副屏区一直贴到屏右
    }

    [Fact(DisplayName = "无累计漂移:三等分屏(各 1/3)右边界 1280/2560/3840 精确")]
    public void NoFloatDrift_ThirdsOf3840()
    {
        const string L = "L3", Z1 = "z1", Z2 = "z2", Z3 = "z3";
        var cfg = new AppConfig
        {
            Layouts =
            {
                new Layout
                {
                    Id = L,
                    Zones =
                    {
                        new Zone { Id = Z1, X = 0,       W = 1.0 / 3, H = 1 },
                        new Zone { Id = Z2, X = 1.0 / 3, W = 1.0 / 3, H = 1 },
                        new Zone { Id = Z3, X = 2.0 / 3, W = 1.0 / 3, H = 1 },
                    }
                }
            }
        };
        var mon = Mon(0, 0, 3840, 2160);
        RECT Resolve(string z)
        {
            var p = new Profile();
            p.Rules.Add(new PlacementRule { Monitor = new MonitorFilter(), Kind = PlacementKind.Zone, LayoutId = L, ZoneId = z });
            return PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, cfg)!.Value;
        }
        var r1 = Resolve(Z1);
        var r2 = Resolve(Z2);
        var r3 = Resolve(Z3);
        Assert.Equal((0, 1280), (r1.Left, r1.Right));
        Assert.Equal((1280, 2560), (r2.Left, r2.Right));
        Assert.Equal((2560, 3840), (r3.Left, r3.Right));
    }

    // ---- MoveOnly:只定位不调尺寸(Unity 固定渲染分辨率游戏) ----

    [Fact(DisplayName = "MoveOnly:Zone 命中 → 位置=zone 左上角、尺寸=窗口当前尺寸(不缩放)")]
    public void MoveOnly_Zone_PositionAtZoneOrigin_KeepsCurrentSize()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId, moveOnly: true));
        // 游戏窗口当前是 5120×2088(注册表渲染分辨率),恰好不在 zone 左上角
        var cur = R(300, 120, 300 + 5120, 120 + 2088);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, CfgWithZones());
        // zone 游戏区左上角 = (0,0);尺寸保持 5120×2088
        AssertRect(rect, 0, 0, 5120, 2088);
    }

    [Fact(DisplayName = "MoveOnly:非零原点屏上,位置=zone 左上角(含屏原点),尺寸不变")]
    public void MoveOnly_NonZeroOrigin_PositionIncludesMonitorOrigin()
    {
        // 屏在 x=5120,zone 游戏区左上角 = 屏原点 (5120,0)
        var mon = Mon(5120, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId, moveOnly: true));
        var cur = R(9999, 50, 9999 + 5120, 50 + 2088); // 当前乱放
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, CfgWithZones());
        AssertRect(rect, 5120, 0, 5120 + 5120, 0 + 2088);
    }

    [Fact(DisplayName = "MoveOnly + Offsets:偏移挪动目标左上角,尺寸仍取窗口当前尺寸")]
    public void MoveOnly_WithOffsets_OffsetsMoveTopLeftOnly()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId, moveOnly: true));
        p.Offsets = new Offsets { Left = 10, Top = 20, Right = -30, Bottom = -40 };
        var cur = R(0, 0, 5120, 2088);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, CfgWithZones());
        // zone 左上角 (0,0) 加 Left/Top 偏移 → (10,20);尺寸保持 5120×2088,Right/Bottom 偏移不参与尺寸
        AssertRect(rect, 10, 20, 10 + 5120, 20 + 2088);
    }

    [Fact(DisplayName = "MoveOnly + KeepAspectRatio:MoveOnly 优先(不 letterbox,只挪位置)")]
    public void MoveOnly_TakesPrecedenceOver_KeepAspectRatio()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId, moveOnly: true));
        p.KeepAspectRatio = true; // 同时开,应被 MoveOnly 压制
        var cur = R(200, 100, 200 + 5120, 100 + 2088);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, CfgWithZones());
        // 若 letterbox 生效会得到居中缩放后的矩形;MoveOnly 优先 → 直接 zone 左上角 + 原尺寸
        AssertRect(rect, 0, 0, 5120, 2088);
    }

    [Fact(DisplayName = "MoveOnly + Fullscreen:位置=屏左上角,尺寸保持窗口当前尺寸")]
    public void MoveOnly_Fullscreen_PositionAtMonitorOrigin()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var rule = FullscreenRule();
        rule.MoveOnly = true;
        var p = ProfWithRules(rule);
        var cur = R(500, 300, 500 + 5120, 300 + 2088);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, new AppConfig());
        AssertRect(rect, 0, 0, 5120, 2088);
    }
}
