using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

public class LayoutKeyTests
{
    // rcMonitor and rcWork are set equal unless a test specifically varies the work area.
    private static MonitorDesc Mon(int w, int h, int x, int y, bool primary = false, string name = "\\\\.\\DISPLAY1")
        => new(name, primary, x, y, w, h, x, y, w, h);

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
}
