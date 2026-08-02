using Reframe.UI;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// Regression tests for the layout-editor canvas sizing (GitHub issue #1: the editor crashed with
/// <c>LayoutCycleException</c> when the reference width was changed).
///
/// <para>The bug was never in the fitting formula — it was in the <i>input</i>. The available height
/// was read from the canvas host's own <c>ActualHeight</c>, but the host is
/// <c>VerticalAlignment="Top"</c>, so that height is <c>canvasHeight + padding + border</c>: a function
/// of the value being computed. Each layout pass therefore raised the height budget by exactly one
/// border thickness, and a large aspect change needed hundreds of passes to converge — more than
/// WinUI's layout-iteration budget allows.</para>
///
/// <para>These tests simulate the arrange feedback both ways: with the fixed input (budget from the
/// stable layout slot) it settles in one pass; with the old input it demonstrably does not. Numbers
/// come from the reproduction captured on 2026-08-02 (1700×1100 window, 7680×2160 default layout).</para>
/// </summary>
public class CanvasFitTests
{
    // Geometry of the real page: CanvasHost has Padding=16 on all sides and BorderThickness=1.
    private const double Pad = 16;
    private const double Border = 1;

    private static double ContentH(double outer) => CanvasFit.Content(outer, Pad, Pad, Border, Border);
    private static double ContentW(double outer) => CanvasFit.Content(outer, Pad, Pad, Border, Border);

    // ---------- the formula itself ----------

    [Fact(DisplayName = "Width-limited: canvas fills the width and takes its height from the ratio")]
    public void Fit_WidthLimited()
    {
        var (w, h) = CanvasFit.Fit(availW: 1000, availH: 900, refW: 1920, refH: 1080);
        Assert.Equal(1000, w, 3);
        Assert.Equal(1000 * 1080.0 / 1920, h, 3);
    }

    [Fact(DisplayName = "Height-limited: canvas is clamped by height and the width narrows to match")]
    public void Fit_HeightLimited()
    {
        var (w, h) = CanvasFit.Fit(availW: 1000, availH: 200, refW: 1920, refH: 1080);
        Assert.Equal(200, h, 3);
        Assert.Equal(200 * 1920.0 / 1080, w, 3);
        Assert.True(w <= 1000);
    }

    [Fact(DisplayName = "Aspect ratio is preserved in both branches")]
    public void Fit_PreservesAspect()
    {
        foreach (var (aw, ah) in new[] { (1000.0, 900.0), (1000.0, 200.0), (400.0, 4000.0) })
        {
            var (w, h) = CanvasFit.Fit(aw, ah, 7680, 2160);
            Assert.Equal(7680.0 / 2160, w / h, 6);
        }
    }

    [Fact(DisplayName = "Unmeasured host falls back to the fallback width instead of collapsing")]
    public void Fit_UnmeasuredHost_UsesFallback()
    {
        var (w, h) = CanvasFit.Fit(availW: 0, availH: 0, refW: 7680, refH: 2160);
        Assert.Equal(CanvasFit.FallbackWidth, w, 3);
        Assert.Equal(CanvasFit.FallbackWidth / (7680.0 / 2160), h, 3);
    }

    [Theory(DisplayName = "A zero/negative reference resolution degrades to 16:9 instead of dividing by zero")]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, -1)]
    public void Aspect_GuardsAgainstZero(int refW, int refH)
    {
        Assert.Equal(16.0 / 9, CanvasFit.Aspect(refW, refH), 6);
        var (w, h) = CanvasFit.Fit(1000, 5000, refW, refH);
        Assert.True(double.IsFinite(w) && double.IsFinite(h) && h > 0);
    }

    [Fact(DisplayName = "Content box subtracts BOTH padding and border (the pre-fix off-by-one-border)")]
    public void Content_SubtractsPaddingAndBorder()
    {
        Assert.Equal(411.4 - 32 - 2, ContentW(411.4), 6);
    }

    // ---------- the invariant that actually prevents the crash ----------

    /// <summary>
    /// Drive the fit the way WinUI's arrange loop does: start from the canvas the editor was already
    /// showing for <paramref name="fromRefW"/>×<paramref name="fromRefH"/>, switch the reference
    /// resolution to <paramref name="toRefW"/>×<paramref name="toRefH"/>, then keep re-running the fit
    /// (each run being one layout pass) until the canvas stops moving. Returns how many passes actually
    /// changed the canvas — 1 means "jumped straight to the answer", which is what a healthy layout
    /// does; a large number is the ratchet that exhausts WinUI's iteration budget.
    ///
    /// <para><paramref name="availHFromHost"/> selects which input the caller uses for the height
    /// budget: <c>true</c> reproduces the pre-fix behaviour (derived from the host's own ActualHeight,
    /// which is the previous canvas height plus chrome), <c>false</c> is the fix (the stable slot).</para>
    /// </summary>
    private static int PassesToSettle(double fromHostW, double toHostW, double slotH,
                                      int fromRefW, int fromRefH, int toRefW, int toRefH,
                                      bool availHFromHost, int cap = 5000)
    {
        // Converged state before the change.
        var (canvasW, canvasH) = CanvasFit.Fit(ContentW(fromHostW), ContentH(slotH), fromRefW, fromRefH);

        int changed = 0;
        for (int pass = 1; pass <= cap; pass++)
        {
            double availW = ContentW(toHostW);
            double availH = availHFromHost
                // Pre-fix: CanvasHost is VerticalAlignment=Top, so ActualHeight == canvasH + padding +
                // border, and only the padding was subtracted back off — leaving canvasH + border, i.e.
                // a budget that grows with its own output.
                ? (canvasH + 2 * Pad + 2 * Border) - 2 * Pad
                // Fixed: the "*" body row, which no canvas size can influence.
                : ContentH(slotH);

            var (w, h) = CanvasFit.Fit(availW, availH, toRefW, toRefH);
            if (System.Math.Abs(w - canvasW) <= CanvasFit.Epsilon &&
                System.Math.Abs(h - canvasH) <= CanvasFit.Epsilon)
                return changed;                 // settled
            canvasW = w; canvasH = h; changed++;
        }
        return int.MaxValue;
    }

    // Captured from the isolated reproduction: 1700x1100 window -> CanvasHost 411.4 DIP wide,
    // body row ~553 DIP tall, editor opened on the default 7680x2160 layout (canvas 379.4 x 106.7).
    private const double HostW = 411.4;
    private const double SlotH = 553;

    [Theory(DisplayName = "Fixed budget: any reference-resolution change settles in one pass")]
    // The exact issue #1 repro: 7680x2160 -> width typed as 1920 (aspect collapses 3.56 -> 0.89).
    [InlineData(1920, 2160)]
    // Picking 1920x1080 from the reference-resolution dropdown.
    [InlineData(1920, 1080)]
    // Changing only the height (the direction the reporter said never crashed).
    [InlineData(7680, 1080)]
    // Extremes on both sides of the NumberBox range.
    [InlineData(1, 34560)]
    [InlineData(61440, 1)]
    public void Settles_In_One_Pass_WithSlotDerivedHeight(int refW, int refH)
    {
        int passes = PassesToSettle(HostW, HostW, SlotH, 7680, 2160, refW, refH, availHFromHost: false);
        Assert.Equal(1, passes);
    }

    [Fact(DisplayName = "Fixed budget: a large window resize also settles in one pass")]
    public void Settles_In_One_Pass_OnWindowResize()
    {
        // The other captured reproduction: the host width jumped 171.4 -> 1040 DIP in one step while the
        // canvas was still tiny. Pre-fix that alone crashed the editor without touching any control.
        Assert.Equal(1, PassesToSettle(fromHostW: 171.4, toHostW: 1040, slotH: SlotH,
                                       7680, 2160, 7680, 2160, availHFromHost: false));
        Assert.True(PassesToSettle(fromHostW: 171.4, toHostW: 1040, slotH: SlotH,
                                   7680, 2160, 7680, 2160, availHFromHost: true) > 50);
    }

    [Fact(DisplayName = "Pre-fix budget ratchets: reproduces the runaway that blew the layout budget")]
    public void PreFix_HostDerivedHeight_Ratchets()
    {
        // Typing width 1920 over 7680x2160. Pre-fix this crawled up one border thickness per pass; WinUI
        // gives up long before that converges, which is the LayoutCycleException the users hit.
        int passes = PassesToSettle(HostW, HostW, SlotH, 7680, 2160, 1920, 2160, availHFromHost: true);
        Assert.True(passes > 100,
            $"expected the pre-fix input to need a runaway number of passes, got {passes}");

        // And the fix turns exactly that case into a single pass.
        Assert.Equal(1, PassesToSettle(HostW, HostW, SlotH, 7680, 2160, 1920, 2160, availHFromHost: false));
    }

    [Fact(DisplayName = "Shrinking the canvas never ratcheted — this is the width/height asymmetry")]
    public void PreFix_ShrinkingDirection_WasAlwaysFine()
    {
        // Height 2160 -> 1080 widens the aspect, so the canvas gets shorter: the height clamp is never
        // taken and even the pre-fix input settled immediately. That is why "changing the height doesn't
        // crash, changing the width always does" — the default layout is 7680x2160, so users only ever
        // reduce the width (aspect down -> canvas grows -> ratchet) or reduce the height (aspect up ->
        // canvas shrinks -> no ratchet).
        Assert.Equal(1, PassesToSettle(HostW, HostW, SlotH, 7680, 2160, 7680, 1080, availHFromHost: true));
    }

    [Fact(DisplayName = "The fitted canvas always fits inside the slot-derived budget")]
    public void Fit_NeverOverflowsTheSlot()
    {
        double availW = ContentW(HostW), availH = ContentH(SlotH);
        foreach (var (rw, rh) in new[] { (7680, 2160), (1920, 1080), (1920, 2160), (1, 34560), (61440, 1) })
        {
            var (w, h) = CanvasFit.Fit(availW, availH, rw, rh);
            Assert.True(h <= availH + CanvasFit.Epsilon, $"{rw}x{rh}: height {h} > budget {availH}");
            Assert.True(w <= availW + CanvasFit.Epsilon, $"{rw}x{rh}: width {w} > budget {availW}");
        }
    }
}
