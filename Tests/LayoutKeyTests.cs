using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

public class LayoutKeyTests
{
    // rcMonitor and rcWork are set equal unless a test specifically varies the work area.
    // dpi defaults to 96 (100%), matching MonitorDesc's own default.
    private static MonitorDesc Mon(int w, int h, int x, int y, bool primary = false, string name = "\\\\.\\DISPLAY1",
                                   int dpi = 96)
        => new(name, primary, x, y, w, h, x, y, w, h, dpi);

    [Fact]
    public void Null_or_empty_yields_none()
    {
        Assert.Equal(LayoutKey.None, LayoutKey.Compute(null));
        Assert.Equal(LayoutKey.None, LayoutKey.Compute(new MonitorDesc[0]));
    }

    [Fact]
    public void Enumeration_order_does_not_matter()
    {
        var a = new[] { Mon(7680, 2160, 0, 0, primary: true), Mon(2560, 1440, 7680, 0) };
        var b = new[] { Mon(2560, 1440, 7680, 0), Mon(7680, 2160, 0, 0, primary: true) };
        Assert.Equal(LayoutKey.Compute(a), LayoutKey.Compute(b));
    }

    [Fact]
    public void Single_and_multi_monitor_differ()
    {
        var single = new[] { Mon(1920, 1080, 0, 0, primary: true) };
        var multi = new[] { Mon(1920, 1080, 0, 0, primary: true), Mon(1920, 1080, 1920, 0) };
        Assert.NotEqual(LayoutKey.Compute(single), LayoutKey.Compute(multi));
    }

    [Fact]
    public void Resolution_change_changes_key()
    {
        var hi = new[] { Mon(7680, 2160, 0, 0, primary: true) };
        var lo = new[] { Mon(3840, 2160, 0, 0, primary: true) };
        Assert.NotEqual(LayoutKey.Compute(hi), LayoutKey.Compute(lo));
    }

    [Fact]
    public void Position_change_changes_key()
    {
        var left = new[] { Mon(1920, 1080, 0, 0, primary: true), Mon(2560, 1440, 1920, 0) };
        var right = new[] { Mon(1920, 1080, 0, 0, primary: true), Mon(2560, 1440, -2560, 0) };
        Assert.NotEqual(LayoutKey.Compute(left), LayoutKey.Compute(right));
    }

    [Fact]
    public void Device_name_is_ignored()
    {
        // \\.\DISPLAYn drifts across reconnect; the same geometry under a different name must bucket together.
        var d1 = new[] { Mon(2560, 1440, 0, 0, primary: true, name: "\\\\.\\DISPLAY1") };
        var d2 = new[] { Mon(2560, 1440, 0, 0, primary: true, name: "\\\\.\\DISPLAY3") };
        Assert.Equal(LayoutKey.Compute(d1), LayoutKey.Compute(d2));
    }

    [Fact]
    public void Work_area_is_ignored()
    {
        // Same rcMonitor, different rcWork (e.g. taskbar auto-hide toggled) must not re-bucket.
        var withTaskbar = new[]
        {
            new MonitorDesc("\\\\.\\DISPLAY1", true, 0, 0, 1920, 1080, 0, 0, 1920, 1040),
        };
        var noTaskbar = new[]
        {
            new MonitorDesc("\\\\.\\DISPLAY1", true, 0, 0, 1920, 1080, 0, 0, 1920, 1080),
        };
        Assert.Equal(LayoutKey.Compute(withTaskbar), LayoutKey.Compute(noTaskbar));
    }

    [Fact]
    public void Primary_flag_distinguishes_otherwise_identical_layouts()
    {
        // Two identical-geometry monitors; which one is primary is part of the configuration.
        var primaryLeft = new[] { Mon(1920, 1080, 0, 0, primary: true), Mon(1920, 1080, 1920, 0) };
        var primaryRight = new[] { Mon(1920, 1080, 0, 0), Mon(1920, 1080, 1920, 0, primary: true) };
        Assert.NotEqual(LayoutKey.Compute(primaryLeft), LayoutKey.Compute(primaryRight));
    }

    // ---- DPI scaling is part of the key ----

    [Fact]
    public void Scaling_change_at_identical_resolution_changes_the_key()
    {
        // THE regression this segment exists for: the user switches the system scale 150% → 175%. The panel
        // is still 7680x2160 at the same origin, so a geometry-only key did not move — no restore fired, and
        // the next capture wrote the scrambled layout over the good one. Different scale ⇒ different bucket.
        var at150 = new[] { Mon(7680, 2160, 0, 0, primary: true, dpi: 144) };
        var at175 = new[] { Mon(7680, 2160, 0, 0, primary: true, dpi: 168) };
        Assert.NotEqual(LayoutKey.Compute(at150), LayoutKey.Compute(at175));
    }

    [Fact]
    public void Same_geometry_and_same_dpi_yields_the_same_key_in_any_order()
    {
        var a = new[] { Mon(7680, 2160, 0, 0, primary: true, dpi: 168), Mon(2560, 1440, 7680, 0, dpi: 96) };
        var b = new[] { Mon(2560, 1440, 7680, 0, dpi: 96), Mon(7680, 2160, 0, 0, primary: true, dpi: 168) };
        Assert.Equal(LayoutKey.Compute(a), LayoutKey.Compute(b));
    }

    [Fact]
    public void Each_monitors_own_dpi_reaches_the_key()
    {
        // Mixed-scaling desktops are normal (4K primary at 150%, 1080p secondary at 100%). Re-scaling only
        // the secondary must re-bucket too, so the per-monitor value — not just the primary's — is in the key.
        var baseline = new[] { Mon(3840, 2160, 0, 0, primary: true, dpi: 144), Mon(1920, 1080, 3840, 0, dpi: 96) };
        var secondaryRescaled = new[] { Mon(3840, 2160, 0, 0, primary: true, dpi: 144), Mon(1920, 1080, 3840, 0, dpi: 120) };
        Assert.NotEqual(LayoutKey.Compute(baseline), LayoutKey.Compute(secondaryRescaled));

        // Both values are actually present, not just "something differs".
        Assert.Contains("#144", LayoutKey.Compute(baseline));
        Assert.Contains("#96", LayoutKey.Compute(baseline));
    }

    [Fact]
    public void Token_format_places_the_dpi_segment_before_the_primary_marker()
    {
        // The token layout is part of the on-disk key format: {W}x{H}@{X},{Y}#{Dpi} then a trailing '*' for
        // the primary. Pin it exactly — a reordering would silently re-bucket every user's saved layouts.
        Assert.Equal("7680x2160@0,0#168*", LayoutKey.Compute(new[] { Mon(7680, 2160, 0, 0, primary: true, dpi: 168) }));
        Assert.Equal("2560x1440@7680,0#96", LayoutKey.Compute(new[] { Mon(2560, 1440, 7680, 0, dpi: 96) }));

        // Multi-monitor: sorted ordinally, joined with '|'; every token carries its own '#dpi'.
        Assert.Equal("1920x1080@1920,0#96|3840x2160@0,0#144*",
            LayoutKey.Compute(new[] { Mon(3840, 2160, 0, 0, primary: true, dpi: 144), Mon(1920, 1080, 1920, 0) }));
    }

    [Fact]
    public void Dpi_defaults_to_96_when_the_caller_does_not_supply_it()
    {
        // MonitorDesc's default (and MonitorService's fallback when GetDpiForMonitor fails) is 96, so a
        // description built without a DPI still produces a well-formed, unscaled token.
        var implicitDefault = new MonitorDesc("\\\\.\\DISPLAY1", true, 0, 0, 1920, 1080, 0, 0, 1920, 1080);
        Assert.Equal(96, implicitDefault.Dpi);
        Assert.Equal("1920x1080@0,0#96*", LayoutKey.Compute(new[] { implicitDefault }));
        Assert.Equal(LayoutKey.Compute(new[] { implicitDefault }), LayoutKey.Compute(new[] { Mon(1920, 1080, 0, 0, primary: true, dpi: 96) }));
    }

    [Fact]
    public void A_new_key_can_never_collide_with_a_pre_dpi_key()
    {
        // Old buckets look like "7680x2160@0,0*" (no '#'). Nothing computed today can equal one, which is
        // what makes leaving them to age out — rather than migrating by guesswork — safe.
        Assert.False("7680x2160@0,0*".Contains('#'));
        Assert.NotEqual("7680x2160@0,0*", LayoutKey.Compute(new[] { Mon(7680, 2160, 0, 0, primary: true, dpi: 144) }));
        Assert.NotEqual("7680x2160@0,0*", LayoutKey.Compute(new[] { Mon(7680, 2160, 0, 0, primary: true, dpi: 96) }));
    }

    // ---- Transient (fallback) topologies ----
    //
    // The real-world episode: a 57" 7680x2160 panel fed from several inputs. Switching the TV back to the PC
    // makes the panel drop off the bus for a few seconds; Windows has no EDID, brings the desktop up on the
    // Basic Display path at 1024x768, squeezes every window into it, then the panel returns. Three such
    // buckets (1024x768@0,0*, #96*, #120*) were found in the user's real window-layouts.json holding five to
    // six mangled window records each.

    [Fact]
    public void Single_primary_fallback_display_at_the_origin_is_transient()
    {
        Assert.True(LayoutKey.IsTransient(new[] { Mon(1024, 768, 0, 0, primary: true) }));
        Assert.True(LayoutKey.IsTransient(new[] { Mon(800, 600, 0, 0, primary: true) }));
        // The scale factor Windows reports for the fallback varies (96 / 120 / 168 all seen); it is irrelevant.
        Assert.True(LayoutKey.IsTransient(new[] { Mon(1024, 768, 0, 0, primary: true, dpi: 120) }));
        Assert.True(LayoutKey.IsTransient(new[] { Mon(1024, 768, 0, 0, primary: true, dpi: 168) }));
    }

    [Fact]
    public void A_fallback_sized_display_that_is_not_primary_or_not_at_the_origin_is_not_transient()
    {
        Assert.False(LayoutKey.IsTransient(new[] { Mon(1024, 768, 0, 0) }));            // not primary
        Assert.False(LayoutKey.IsTransient(new[] { Mon(1024, 768, 100, 0, primary: true) }));
        Assert.False(LayoutKey.IsTransient(new[] { Mon(1024, 768, 0, 200, primary: true) }));
        Assert.False(LayoutKey.IsTransient(new[] { Mon(800, 600, -800, 0, primary: true) }));
    }

    [Fact]
    public void A_second_monitor_means_a_real_configuration_however_small_the_first_is()
    {
        // THE "don't generalise" nail. A 1024x768 panel with company is somebody's small secondary screen /
        // KVM preview, arranged on purpose — the fallback never has company, it exists because nothing else is
        // there. Persisting must keep working for these.
        Assert.False(LayoutKey.IsTransient(new[]
        {
            Mon(1024, 768, 0, 0, primary: true),
            Mon(7680, 2160, 1024, 0, dpi: 168),
        }));
        Assert.False(LayoutKey.IsTransient(new[]
        {
            Mon(7680, 2160, 0, 0, primary: true, dpi: 168),   // real primary...
            Mon(1024, 768, 7680, 0),                          // ...plus a small secondary
        }));
    }

    [Fact]
    public void Ordinary_and_streaming_resolutions_are_never_transient()
    {
        // Not "any small resolution": a streaming virtual display (Sunshine's VDD and friends) is routinely
        // 1280x800 / 1920x1080 / 1920x1200 and is a first-class configuration whose layout must be remembered.
        Assert.False(LayoutKey.IsTransient(new[] { Mon(1920, 1080, 0, 0, primary: true) }));
        Assert.False(LayoutKey.IsTransient(new[] { Mon(1280, 800, 0, 0, primary: true) }));
        Assert.False(LayoutKey.IsTransient(new[] { Mon(1920, 1200, 0, 0, primary: true) }));
        Assert.False(LayoutKey.IsTransient(new[] { Mon(1024, 1024, 0, 0, primary: true) })); // right width, wrong height
        Assert.False(LayoutKey.IsTransient(new[] { Mon(768, 1024, 0, 0, primary: true) }));  // rotated: not the fallback
        Assert.False(LayoutKey.IsTransient(new[] { Mon(7680, 2160, 0, 0, primary: true, dpi: 168) }));
    }

    [Fact]
    public void No_monitors_is_not_transient_it_is_the_none_sentinel()
    {
        // Empty is already handled everywhere by LayoutKey.None; conflating the two would hide it.
        Assert.False(LayoutKey.IsTransient(null));
        Assert.False(LayoutKey.IsTransient(new MonitorDesc[0]));
    }

    [Fact]
    public void A_transient_topology_still_computes_an_ordinary_key()
    {
        // Compute stays total: the fallback hashes to a normal-looking key. It is never *used* — the engine
        // and LayoutMemory refuse to act on it — but keeping one shape means IsTransientKey can recognise the
        // buckets earlier builds already wrote.
        Assert.Equal("1024x768@0,0#96*", LayoutKey.Compute(new[] { Mon(1024, 768, 0, 0, primary: true) }));
        Assert.Equal("1024x768@0,0#120*", LayoutKey.Compute(new[] { Mon(1024, 768, 0, 0, primary: true, dpi: 120) }));
    }

    // ---- IsTransientKey: the same judgement from a key string ----

    [Theory]
    [InlineData("1024x768@0,0*")]        // pre-DPI shape, as found in the real file
    [InlineData("1024x768@0,0#120*")]
    [InlineData("1024x768@0,0#96*")]
    [InlineData("1024x768@0,0#168*")]
    [InlineData("800x600@0,0#96*")]
    [InlineData("800x600@0,0*")]
    public void Transient_keys_are_recognised_in_both_the_old_and_new_formats(string key)
        => Assert.True(LayoutKey.IsTransientKey(key));

    [Theory]
    [InlineData("1024x768@0,0#96*|7680x2160@1024,0#168")] // a second monitor ⇒ a real configuration
    [InlineData("7680x2160@0,0#168*")]                    // the real panel
    [InlineData("1920x1080@0,0#96*")]
    [InlineData("1024x768@100,0#96*")]                    // not at the origin
    [InlineData("1024x768@0,-768#96*")]
    [InlineData("1024x768@0,0#96")]                       // not the primary
    [InlineData("1024x768@0,0")]
    [InlineData("768x1024@0,0#96*")]                      // rotated: not the fallback mode
    public void Non_transient_keys_are_left_alone(string key)
        => Assert.False(LayoutKey.IsTransientKey(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("*")]
    [InlineData("desk")]
    [InlineData("1024x768*")]
    [InlineData("1024x768@*")]
    [InlineData("1024x768@0*")]
    [InlineData("1024x768@0,0#*")]
    [InlineData("1024x768@0,0#9a6*")]
    [InlineData("1024x768@ 0,0#96*")]
    [InlineData("1024 x768@0,0#96*")]
    [InlineData("1024x768@0,0#96#96*")]
    [InlineData("99999999999999x768@0,0#96*")]
    [InlineData("{\"not\":\"a key\"}*")]
    public void Junk_key_strings_are_not_transient_and_never_throw(string? key)
        => Assert.False(LayoutKey.IsTransientKey(key));

    [Fact]
    public void IsTransient_and_IsTransientKey_agree_on_what_Compute_produces()
    {
        // The two entry points must not drift: whatever the pure-topology test says, the key form must say too.
        var cases = new[]
        {
            new[] { Mon(1024, 768, 0, 0, primary: true) },
            new[] { Mon(800, 600, 0, 0, primary: true, dpi: 120) },
            new[] { Mon(1920, 1080, 0, 0, primary: true) },
            new[] { Mon(1024, 768, 0, 0, primary: true), Mon(7680, 2160, 1024, 0, dpi: 168) },
            new[] { Mon(1024, 768, 100, 0, primary: true) },
            new[] { Mon(1024, 768, 0, 0) },
        };
        foreach (var c in cases)
            Assert.Equal(LayoutKey.IsTransient(c), LayoutKey.IsTransientKey(LayoutKey.Compute(c)));
    }
}
