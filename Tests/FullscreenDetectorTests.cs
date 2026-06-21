using Reframe.Core;
using Reframe.Interop;
using Xunit;
using RECT = Reframe.Interop.NativeMethods.RECT;

namespace Reframe.Core.Tests;

/// <summary>
/// FullscreenDetector.IsCoveringMonitor pure geometry test: does a window's outer rect cover its monitor rect
/// to within a per-edge tolerance? Covers exact coverage, within-tolerance shortfall, a clearly-smaller window,
/// overscan, and a secondary monitor at a negative virtual-desktop origin. The SHQuery / foreground probes are
/// Win32 and not unit-tested.
/// </summary>
public class FullscreenDetectorTests
{
    private static RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Fact(DisplayName = "Exact coverage: window rect == monitor rect")]
    public void ExactCover()
    {
        var mon = R(0, 0, 1920, 1080);
        Assert.True(FullscreenDetector.IsCoveringMonitor(mon, mon));
    }

    [Fact(DisplayName = "Within tolerance: a few pixels inset on each edge still counts as covering")]
    public void WithinTolerance()
    {
        var mon = R(0, 0, 1920, 1080);
        // Inset by 2px on every edge; default tol is 4 → still covers.
        var win = R(2, 2, 1918, 1078);
        Assert.True(FullscreenDetector.IsCoveringMonitor(win, mon));
        // Right at the tolerance boundary (4px inset) → still covers.
        Assert.True(FullscreenDetector.IsCoveringMonitor(R(4, 4, 1916, 1076), mon));
    }

    [Fact(DisplayName = "Just past tolerance: 5px inset on an edge fails")]
    public void JustPastTolerance()
    {
        var mon = R(0, 0, 1920, 1080);
        // Left inset by 5px (> tol 4) → not covering.
        Assert.False(FullscreenDetector.IsCoveringMonitor(R(5, 0, 1920, 1080), mon));
        // Bottom inset by 5px → not covering.
        Assert.False(FullscreenDetector.IsCoveringMonitor(R(0, 0, 1920, 1075), mon));
    }

    [Fact(DisplayName = "Clearly smaller window (a snapped half) does not count")]
    public void ClearlySmaller()
    {
        var mon = R(0, 0, 1920, 1080);
        var leftHalf = R(0, 0, 960, 1080);
        Assert.False(FullscreenDetector.IsCoveringMonitor(leftHalf, mon));

        var smallCentered = R(400, 200, 1520, 880);
        Assert.False(FullscreenDetector.IsCoveringMonitor(smallCentered, mon));
    }

    [Fact(DisplayName = "Overscan: a window spilling beyond the monitor still covers it")]
    public void Overscan()
    {
        var mon = R(0, 0, 1920, 1080);
        // Window extends past every edge.
        var win = R(-10, -10, 1930, 1090);
        Assert.True(FullscreenDetector.IsCoveringMonitor(win, mon));
    }

    [Fact(DisplayName = "Secondary monitor at a negative origin: coverage uses differences, not absolute signs")]
    public void NegativeOriginSecondaryMonitor()
    {
        // A 2560x1440 monitor to the left of the primary, origin at (-2560, 0).
        var mon = R(-2560, 0, 0, 1440);
        // Exact cover.
        Assert.True(FullscreenDetector.IsCoveringMonitor(mon, mon));
        // Within tolerance inset.
        Assert.True(FullscreenDetector.IsCoveringMonitor(R(-2558, 2, -2, 1438), mon));
        // A half-width window on that monitor does not cover.
        Assert.False(FullscreenDetector.IsCoveringMonitor(R(-2560, 0, -1280, 1440), mon));
        // Overscan on the negative-origin monitor still covers.
        Assert.True(FullscreenDetector.IsCoveringMonitor(R(-2570, -5, 10, 1445), mon));
    }

    [Fact(DisplayName = "Custom tolerance is honoured")]
    public void CustomTolerance()
    {
        var mon = R(0, 0, 1920, 1080);
        var win = R(8, 8, 1912, 1072); // 8px inset on each edge
        Assert.False(FullscreenDetector.IsCoveringMonitor(win, mon, tol: 4));
        Assert.True(FullscreenDetector.IsCoveringMonitor(win, mon, tol: 8));
    }
}
