using Reframe.Core;
using Reframe.Interop;
using Xunit;
using RECT = Reframe.Interop.NativeMethods.RECT;

namespace Reframe.Core.Tests;

/// <summary>
/// PlacementResolver.ResolveRect pure geometry core (the M3 contract target).
/// No Win32: rcMonitor / rcWork / currentWindowRect are fed in directly.
/// Verifies: Fullscreen, UseWorkArea, Zone ratio→pixels, non-zero-origin offset, CustomRect,
/// rule-table ordering, Offsets stacking, KeepAspectRatio letterbox, no cumulative drift.
/// </summary>
public class ResolveRectTests
{
    // ---- Construction helpers ----

    private static RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    /// <summary>A monitor with origin (ox,oy) and size w×h.</summary>
    private static RECT Mon(int ox, int oy, int w, int h) => R(ox, oy, ox + w, oy + h);

    private const string LayoutId = "LAYOUT-A";
    private const string GameZoneId = "ZONE-GAME";   // (0,0,2/3,1)
    private const string SideZoneId = "ZONE-SIDE";   // (2/3,0,1/3,1)

    /// <summary>A config with the 57″ layout (game zone 2/3 wide + secondary zone 1/3 wide).</summary>
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

    [Fact(DisplayName = "Fullscreen: fills the whole rcMonitor")]
    public void Fullscreen_WholeMonitor()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var work = R(0, 0, 7680, 2112);
        var p = ProfWithRules(FullscreenRule());
        var rect = PlacementResolver.ResolveRect(mon, work, R(0, 0, 100, 100), p, new AppConfig());
        AssertRect(rect, 0, 0, 7680, 2160);
    }

    [Fact(DisplayName = "Fullscreen + UseWorkArea: fills the work area (avoiding the taskbar)")]
    public void Fullscreen_UseWorkArea()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var work = R(0, 0, 7680, 2112); // taskbar 48px
        var p = ProfWithRules(FullscreenRule(useWork: true));
        var rect = PlacementResolver.ResolveRect(mon, work, R(0, 0, 100, 100), p, new AppConfig());
        AssertRect(rect, 0, 0, 7680, 2112);
    }

    // ---- Zone ratio → pixels ----

    [Fact(DisplayName = "Zone game area: 7680×2160 + 2/3 wide → (0,0,5120,2160)")]
    public void Zone_GameArea_FullMonitor()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, CfgWithZones());
        AssertRect(rect, 0, 0, 5120, 2160);
    }

    [Fact(DisplayName = "Zone secondary area: 7680×2160 + 1/3 wide starting at 2/3 → (5120,0,7680,2160)")]
    public void Zone_SideArea_FullMonitor()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(SideZoneId));
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, CfgWithZones());
        // Left = Round(2/3 * 7680) = 5120; Right = Round(1 * 7680) = 7680
        AssertRect(rect, 5120, 0, 7680, 2160);
    }

    [Fact(DisplayName = "Zone + UseWorkArea: height from rcWork (2160-48=2112)")]
    public void Zone_UseWorkArea_HeightFromWork()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var work = R(0, 0, 7680, 2112);
        var p = ProfWithRules(ZoneRule(GameZoneId, useWork: true));
        var rect = PlacementResolver.ResolveRect(mon, work, R(0, 0, 100, 100), p, CfgWithZones());
        // Width still computed from the work-area width 7680 (here work-area width == screen width); height down to 2112
        AssertRect(rect, 0, 0, 5120, 2112);
    }

    [Fact(DisplayName = "Zone on a non-zero-origin monitor: offset correct (monitor placed at x=5120)")]
    public void Zone_NonZeroOriginMonitor()
    {
        // A 7680-wide monitor with top-left at (5120,0) — e.g. to the right of the primary
        var mon = Mon(5120, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        var rect = PlacementResolver.ResolveRect(mon, mon, R(5120, 0, 5220, 100), p, CfgWithZones());
        // Left = 5120 + Round(0) = 5120; Right = 5120 + Round(2/3*7680=5120) = 10240
        AssertRect(rect, 5120, 0, 10240, 2160);
    }

    [Fact(DisplayName = "Zone on a negative-origin monitor (monitor to the left of the primary, x=-7680)")]
    public void Zone_NegativeOriginMonitor()
    {
        var mon = Mon(-7680, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        var rect = PlacementResolver.ResolveRect(mon, mon, R(-7680, 0, -7580, 100), p, CfgWithZones());
        AssertRect(rect, -7680, 0, -2560, 2160);
    }

    // ---- CustomRect ----

    [Fact(DisplayName = "CustomRect: absolute rect relative to the monitor origin (zero origin)")]
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

    [Fact(DisplayName = "CustomRect: adds the monitor origin on a non-zero-origin monitor")]
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

    // ---- None / no rule ----

    [Fact(DisplayName = "Kind=None: matches but produces no rect → null")]
    public void None_ReturnsNull()
    {
        var mon = Mon(0, 0, 1920, 1080);
        var p = ProfWithRules(new PlacementRule { Monitor = new MonitorFilter(), Kind = PlacementKind.None });
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        Assert.Null(rect);
    }

    [Fact(DisplayName = "No rule matches: no rule's Monitor matches → null")]
    public void NoRuleMatches_ReturnsNull()
    {
        var mon = Mon(0, 0, 1920, 1080);
        var p = ProfWithRules(FullscreenRule(7680, 2160)); // Only applies to 7680×2160
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        Assert.Null(rect);
    }

    [Fact(DisplayName = "Empty rule table → null")]
    public void EmptyRules_ReturnsNull()
    {
        var mon = Mon(0, 0, 1920, 1080);
        var p = ProfWithRules();
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        Assert.Null(rect);
    }

    // ---- Rule-table ordering: the first match wins ----

    [Fact(DisplayName = "Rule order: local 57″ matches the first Zone rule (not the last Fullscreen)")]
    public void RuleOrder_FirstMatchWins_Local()
    {
        var cfg = CfgWithZones();
        var p = ProfWithRules(
            ZoneRule(GameZoneId, 7680, 2160, useWork: true), // Local 57″ only
            FullscreenRule());                                // Any-monitor fallback
        var mon = Mon(0, 0, 7680, 2160);
        var work = R(0, 0, 7680, 2112);
        var rect = PlacementResolver.ResolveRect(mon, work, R(0, 0, 100, 100), p, cfg);
        // The first Zone+UseWorkArea wins
        AssertRect(rect, 0, 0, 5120, 2112);
    }

    [Fact(DisplayName = "Rule order: a different VDD resolution falls to the last Fullscreen fallback")]
    public void RuleOrder_FallbackFullscreen_Vdd()
    {
        var cfg = CfgWithZones();
        var p = ProfWithRules(
            ZoneRule(GameZoneId, 7680, 2160, useWork: true),
            FullscreenRule());
        var mon = Mon(0, 0, 2560, 1440); // VDD streaming resolution; the first rule doesn't match
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, cfg);
        AssertRect(rect, 0, 0, 2560, 1440);
    }

    [Fact(DisplayName = "Rule order: when both match, take the earlier one")]
    public void RuleOrder_BothMatch_TakesFirst()
    {
        var cfg = CfgWithZones();
        var p = ProfWithRules(
            ZoneRule(GameZoneId),     // Monitor 0×0 (any), matches first
            FullscreenRule());        // Also any, but later
        var mon = Mon(0, 0, 7680, 2160);
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, cfg);
        AssertRect(rect, 0, 0, 5120, 2160); // Zone wins, not Fullscreen
    }

    // ---- Zone rule but the Zone can't be found (dirty config) ----

    [Fact(DisplayName = "Zone rule but LayoutId/ZoneId not found → null (no crash)")]
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

    // ---- Offsets stacking on all four edges ----

    [Fact(DisplayName = "Offsets: each edge offset stacks onto L/T/R/B")]
    public void Offsets_AppliedToAllFourEdges()
    {
        var mon = Mon(0, 0, 1920, 1080);
        var p = ProfWithRules(FullscreenRule());
        p.Offsets = new Offsets { Left = 10, Top = 20, Right = -30, Bottom = -40 };
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        // (0+10, 0+20, 1920-30, 1080-40)
        AssertRect(rect, 10, 20, 1890, 1040);
    }

    [Fact(DisplayName = "Offsets: stacks on top of a Zone")]
    public void Offsets_OnTopOfZone()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        p.Offsets = new Offsets { Left = 5, Top = 0, Right = 0, Bottom = -8 };
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, CfgWithZones());
        AssertRect(rect, 5, 0, 5120, 2152);
    }

    [Fact(DisplayName = "Offsets: no effect when None produces no rect (still null)")]
    public void Offsets_NoEffectWhenNull()
    {
        var mon = Mon(0, 0, 1920, 1080);
        var p = ProfWithRules(new PlacementRule { Monitor = new MonitorFilter(), Kind = PlacementKind.None });
        p.Offsets = new Offsets { Left = 10, Top = 10, Right = 10, Bottom = 10 };
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, new AppConfig());
        Assert.Null(rect);
    }

    // ---- KeepAspectRatio letterbox ----

    [Fact(DisplayName = "KeepAspectRatio: a 16:9 window into a 5120×2160 (≈21:9) zone → proportional 3840×2160 centered")]
    public void KeepAspectRatio_16x9_Into_UltraWide()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        p.KeepAspectRatio = true;
        // The current window is 16:9 (1920×1080)
        var cur = R(0, 0, 1920, 1080);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, CfgWithZones());
        // zone 5120×2160; 16:9 fitted proportionally: height fills 2160, width = 2160*16/9 = 3840
        // centered: left = (5120-3840)/2 = 640
        AssertRect(rect, 640, 0, 4480, 2160);
    }

    [Fact(DisplayName = "KeepAspectRatio: window wider than the zone → width fills, black bars top and bottom")]
    public void KeepAspectRatio_WideContent_VerticalLetterbox()
    {
        var mon = Mon(0, 0, 3840, 2160);
        var p = ProfWithRules(FullscreenRule()); // zone = 3840×2160 (16:9)
        p.KeepAspectRatio = true;
        // The current window is 32:9 (3840×1080) — wider than 16:9
        var cur = R(0, 0, 3840, 1080);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, new AppConfig());
        // width fills 3840, height = 3840 * 1080/3840 = 1080; centered top=(2160-1080)/2=540
        AssertRect(rect, 0, 540, 3840, 1620);
    }

    [Fact(DisplayName = "KeepAspectRatio: same-ratio window → fills exactly without distortion")]
    public void KeepAspectRatio_SameRatio_FillsExactly()
    {
        var mon = Mon(0, 0, 3840, 2160);
        var p = ProfWithRules(FullscreenRule());
        p.KeepAspectRatio = true;
        var cur = R(0, 0, 1920, 1080); // 16:9 == zone 16:9
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, new AppConfig());
        AssertRect(rect, 0, 0, 3840, 2160);
    }

    [Fact(DisplayName = "KeepAspectRatio: when the current window's width/height is invalid (0), return the target rect unchanged")]
    public void KeepAspectRatio_DegenerateCurrent_ReturnsOuter()
    {
        var mon = Mon(0, 0, 3840, 2160);
        var p = ProfWithRules(FullscreenRule());
        p.KeepAspectRatio = true;
        var cur = R(0, 0, 0, 0); // width/height 0
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, new AppConfig());
        AssertRect(rect, 0, 0, 3840, 2160);
    }

    [Fact(DisplayName = "KeepAspectRatio=false: no letterbox, fills directly")]
    public void KeepAspectRatio_Off_NoLetterbox()
    {
        var mon = Mon(0, 0, 3840, 2160);
        var p = ProfWithRules(FullscreenRule());
        p.KeepAspectRatio = false;
        var cur = R(0, 0, 1920, 1080);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, new AppConfig());
        AssertRect(rect, 0, 0, 3840, 2160);
    }

    // ---- Ratio conversion has no cumulative drift ----

    [Fact(DisplayName = "No cumulative drift: 0.6666... × 7680 rounds exactly to 5120")]
    public void NoFloatDrift_TwoThirds_Of_7680()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId));
        var rect = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), p, CfgWithZones());
        Assert.NotNull(rect);
        // Right edge exactly 5120, not 5119/5121
        Assert.Equal(5120, rect!.Value.Right);
    }

    [Fact(DisplayName = "No cumulative drift: game and secondary zones' edges meet seamlessly / don't overlap (5120 | 5120→7680)")]
    public void NoFloatDrift_AdjacentZonesTileExactly()
    {
        var cfg = CfgWithZones();
        var mon = Mon(0, 0, 7680, 2160);

        var game = ProfWithRules(ZoneRule(GameZoneId));
        var side = ProfWithRules(ZoneRule(SideZoneId));

        var g = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), game, cfg)!.Value;
        var s = PlacementResolver.ResolveRect(mon, mon, R(0, 0, 100, 100), side, cfg)!.Value;

        // Game zone's right edge == secondary zone's left edge: seamless tiling, no 1px gap or overlap
        Assert.Equal(g.Right, s.Left);
        Assert.Equal(5120, g.Right);
        Assert.Equal(7680, s.Right); // Secondary zone goes all the way to the screen's right
    }

    [Fact(DisplayName = "No cumulative drift: a three-way split (each 1/3) has exact right edges 1280/2560/3840")]
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

    // ---- MoveOnly: position only, don't change size (Unity fixed-render-resolution games) ----

    [Fact(DisplayName = "MoveOnly: Zone matches → position=zone top-left, size=window's current size (no scaling)")]
    public void MoveOnly_Zone_PositionAtZoneOrigin_KeepsCurrentSize()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId, moveOnly: true));
        // The game window is currently 5120×2088 (registry render resolution), and happens not to be at the zone's top-left
        var cur = R(300, 120, 300 + 5120, 120 + 2088);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, CfgWithZones());
        // Game-zone top-left = (0,0); size stays 5120×2088
        AssertRect(rect, 0, 0, 5120, 2088);
    }

    [Fact(DisplayName = "MoveOnly: on a non-zero-origin monitor, position=zone top-left (including the monitor origin), size unchanged")]
    public void MoveOnly_NonZeroOrigin_PositionIncludesMonitorOrigin()
    {
        // Monitor at x=5120; game-zone top-left = monitor origin (5120,0)
        var mon = Mon(5120, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId, moveOnly: true));
        var cur = R(9999, 50, 9999 + 5120, 50 + 2088); // Currently placed arbitrarily
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, CfgWithZones());
        AssertRect(rect, 5120, 0, 5120 + 5120, 0 + 2088);
    }

    [Fact(DisplayName = "MoveOnly + Offsets: offsets move the target's top-left, size still taken from the window's current size")]
    public void MoveOnly_WithOffsets_OffsetsMoveTopLeftOnly()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId, moveOnly: true));
        p.Offsets = new Offsets { Left = 10, Top = 20, Right = -30, Bottom = -40 };
        var cur = R(0, 0, 5120, 2088);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, CfgWithZones());
        // Zone top-left (0,0) plus Left/Top offsets → (10,20); size stays 5120×2088, Right/Bottom offsets don't affect size
        AssertRect(rect, 10, 20, 10 + 5120, 20 + 2088);
    }

    [Fact(DisplayName = "MoveOnly + KeepAspectRatio: MoveOnly wins (no letterbox, only move)")]
    public void MoveOnly_TakesPrecedenceOver_KeepAspectRatio()
    {
        var mon = Mon(0, 0, 7680, 2160);
        var p = ProfWithRules(ZoneRule(GameZoneId, moveOnly: true));
        p.KeepAspectRatio = true; // Both on; should be suppressed by MoveOnly
        var cur = R(200, 100, 200 + 5120, 100 + 2088);
        var rect = PlacementResolver.ResolveRect(mon, mon, cur, p, CfgWithZones());
        // If letterbox applied we'd get a centered, scaled rect; MoveOnly wins → straight to zone top-left + original size
        AssertRect(rect, 0, 0, 5120, 2088);
    }

    [Fact(DisplayName = "MoveOnly + Fullscreen: position=monitor top-left, size keeps the window's current size")]
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
