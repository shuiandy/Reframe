using Reframe.Core;
using Reframe.Interop;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// The rectangle rules behind <see cref="AdoptionGate"/>: when does a window that just claimed a remembered
/// record look <i>squeezed</i> into Windows' 1024×768 no-display fallback box, and when is an adoption restore
/// therefore worth doing. The store-level consequences (what <c>Capture</c> reports, what the record ends up
/// holding) live in <see cref="LayoutAdoptionTests"/>; this file pins the geometry.
/// </summary>
public class AdoptionGateTests
{
    private static WindowRecord Normal(int l, int t, int r, int b)
        => new(l, t, r, b, l, t, r, b, NativeMethods.SW_SHOWNORMAL);

    /// <summary>A maximized window on a 5120×2088 monitor: the screen rect is the monitor, the restore rect is the argument.</summary>
    private static WindowRecord Maximized(int nl, int nt, int nr, int nb)
        => new(0, 0, 5120, 2088, nl, nt, nr, nb, NativeMethods.SW_SHOWMAXIMIZED);

    [Theory]
    [InlineData(0, 0, 1024, 768, true)]         // the whole fallback desktop
    [InlineData(0, 0, 800, 600, true)]          // the older fallback mode
    [InlineData(100, 50, 900, 700, true)]       // somewhere inside it
    [InlineData(-8, -8, 1032, 776, true)]       // DWM's invisible borders hanging over the edge
    [InlineData(-32, -32, 1056, 800, true)]     // exactly the slack
    [InlineData(-33, 0, 1024, 768, false)]      // one pixel past it
    [InlineData(0, 0, 1057, 768, false)]
    [InlineData(0, 0, 1024, 801, false)]
    [InlineData(1200, 300, 2000, 900, false)]   // a normal window on a big desktop
    [InlineData(0, 0, 1600, 1000, false)]       // at the origin, but too big to have been squeezed
    [InlineData(5000, 2000, 5800, 2600, false)]
    [InlineData(100, 100, 100, 700, false)]     // degenerate: zero width
    [InlineData(100, 700, 900, 100, false)]     // inverted
    public void Fits_the_fallback_box(int l, int t, int r, int b, bool fits)
        => Assert.Equal(fits, AdoptionGate.FitsFallbackBox(l, t, r, b));

    [Fact]
    public void Restore_only_when_the_window_is_squeezed_and_the_record_is_not()
    {
        var big = Normal(1200, 300, 2800, 1300);
        var squeezed = Normal(0, 0, 800, 600);
        var smallCorner = Normal(100, 80, 900, 680);

        Assert.True(AdoptionGate.ShouldRestore(observed: squeezed, remembered: big));           // the squeeze: undo it
        Assert.False(AdoptionGate.ShouldRestore(observed: big, remembered: big));               // came back fine
        Assert.False(AdoptionGate.ShouldRestore(observed: Normal(2400, 500, 4000, 1500), remembered: big)); // elsewhere, by the app's choice
        Assert.False(AdoptionGate.ShouldRestore(observed: squeezed, remembered: smallCorner));  // both small: nothing to undo
        Assert.False(AdoptionGate.ShouldRestore(observed: big, remembered: squeezed));          // never shrink a good window onto a squeezed memory
    }

    [Fact]
    public void A_maximized_window_is_judged_by_its_restore_rectangle()
    {
        Assert.True(AdoptionGate.FitsFallbackBox(Maximized(0, 0, 1024, 740)));          // the full-monitor screen rect is ignored
        Assert.False(AdoptionGate.FitsFallbackBox(Maximized(1000, 200, 3400, 1700)));
        Assert.True(AdoptionGate.ShouldRestore(Maximized(0, 0, 1024, 740), Maximized(1000, 200, 3400, 1700)));
        Assert.False(AdoptionGate.ShouldRestore(Maximized(1200, 300, 3000, 1600), Maximized(1000, 200, 3400, 1700)));
    }

    [Fact]
    public void A_normal_window_is_judged_by_its_screen_rectangle()
    {
        // The restore path moves a visible normal window by its screen rect, so that is the rectangle whose
        // squeeze matters — a stale rcNormalPosition must not decide for it either way.
        var screenSqueezedNormStale = new WindowRecord(0, 0, 800, 600, 1200, 300, 2800, 1300, NativeMethods.SW_SHOWNORMAL);
        Assert.True(AdoptionGate.FitsFallbackBox(screenSqueezedNormStale));

        var screenFineNormSmall = new WindowRecord(1200, 300, 2800, 1300, 0, 0, 800, 600, NativeMethods.SW_SHOWNORMAL);
        Assert.False(AdoptionGate.FitsFallbackBox(screenFineNormSmall));
    }
}
