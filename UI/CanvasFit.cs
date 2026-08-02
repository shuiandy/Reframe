namespace Reframe.UI;

/// <summary>
/// Pure geometry for the layout editor's canvas: "given the available box and the layout's reference
/// resolution, how big is the canvas?". Deliberately free of any WinUI type (plain doubles in, plain
/// doubles out), so the rule can be linked into <c>Tests\Reframe.Core.Tests.csproj</c> and unit-tested
/// without a UI thread — see <c>Tests\CanvasFitTests.cs</c>.
///
/// <para><b>Why this exists (GitHub issue #1).</b> The sizing rule itself was never wrong; what was
/// wrong was the <i>input</i> the caller fed it. <see cref="Fit"/>'s <c>availH</c> used to be derived
/// from the canvas host's own <c>ActualHeight</c>. The host is <c>VerticalAlignment="Top"</c>, so its
/// height <i>is</i> its content's height plus padding and border — i.e. a function of the very canvas
/// height being computed. That closed a positive feedback loop: each layout pass raised the height
/// budget by exactly one <c>BorderThickness</c>, so the canvas ratcheted up ~2 DIP per pass instead of
/// jumping to its target. A large aspect-ratio change (e.g. typing a new reference width) needed
/// hundreds of passes and exhausted WinUI's layout-iteration budget:
/// <c>LayoutCycleException: Layout cycle detected</c>.</para>
///
/// <para><b>The invariant to preserve:</b> the available box handed to <see cref="Fit"/> must be a
/// function of the <i>layout slot</i> only, never of the canvas size that <see cref="Fit"/> returns.
/// <c>CanvasFitTests.Settles_In_One_Pass_*</c> pins this down.</para>
/// </summary>
internal static class CanvasFit
{
    /// <summary>Canvas width used before the host has been measured; a real SizeChanged replaces it.</summary>
    public const double FallbackWidth = 900;

    /// <summary>
    /// Size writes smaller than this (DIP) are skipped. Setting a <c>Width</c>/<c>Height</c> to a value
    /// that differs only by float noise still invalidates measure, which costs an extra layout pass for
    /// no visible change — and, given a feedback path, is exactly how layout cycles are sustained.
    /// </summary>
    public const double Epsilon = 0.5;

    /// <summary>Reference aspect ratio, guarding against a zero/negative reference resolution.</summary>
    public static double Aspect(int refW, int refH)
        => (refW > 0 && refH > 0) ? (double)refW / refH : 16.0 / 9.0;

    /// <summary>
    /// Content box of a bordered, padded container: the outer extent minus padding and border on both
    /// sides. Note both are subtracted — the pre-fix code subtracted padding only, which is what made
    /// the height budget overshoot by one border thickness every pass.
    /// </summary>
    public static double Content(double outer, double padStart, double padEnd,
                                 double borderStart, double borderEnd)
        => outer - padStart - padEnd - borderStart - borderEnd;

    /// <summary>
    /// Fit a canvas of the reference aspect ratio into <paramref name="availW"/> ×
    /// <paramref name="availH"/>: fill the width and take the height from the ratio, but if that
    /// overflows the available height, constrain by height instead and narrow the width to match.
    /// Always centered by the caller.
    /// </summary>
    public static (double W, double H) Fit(double availW, double availH, int refW, int refH)
    {
        double aspect = Aspect(refW, refH);

        // Before the host has been measured, availW is 0/negative: use the fallback; a later
        // SizeChanged recomputes exactly.
        double w = availW > 1 ? availW : FallbackWidth;
        double h = w / aspect;

        if (availH > 1 && h > availH)
        {
            h = availH;
            w = h * aspect;
        }
        return (w, h);
    }
}
