using Reframe.Interop;

namespace Reframe.Core;

/// <summary>
/// The size of the desktop Windows fell back to during a display fallback episode: 1024×768 on current builds,
/// 800×600 on older ones (see <see cref="LayoutKey.IsTransient"/>). Physical pixels, anchored at the origin,
/// because the fallback desktop is a single primary at (0,0). <see cref="IsValid"/> is false for the default
/// value, which stands for "no fallback observed" and never licenses a move.
/// </summary>
public readonly record struct FallbackBox(int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// Decides whether a window that just <i>claimed</i> a remembered record (see <see cref="LayoutMemory.Capture"/>,
/// the adoption path) should actually be <b>moved</b> to that record, or merely tracked from where it is.
///
/// <para><b>Why identity and freshness are not enough.</b> The matcher can be sure a window is the one a
/// record describes (same process and class, a unique caption, or a 1:1 leftover) and the record can be
/// fresh (its window went away minutes ago), and the right answer is <i>still</i> "leave it alone" most of
/// the time. Close a browser window and open another one, close a chat and open the next, start an app at
/// login: every one of those is a 1:1 pairing inside <see cref="LayoutMemory.AdoptionWindow"/>, and the app
/// placed the new window perfectly well by itself. Moving it anyway is what made opening a normal-sized
/// window feel like a fight with Reframe, and a window that is already in the right place has nothing to
/// gain from a restore.</para>
///
/// <para><b>What adoption is actually for.</b> The one thing a fresh re-identification should repair is the
/// damage Windows does on its own when the only display goes away: the desktop collapses to the 1024×768
/// (or 800×600) no-display fallback and every window on it, and every window an app opens while it lasts,
/// is squeezed into that box at the origin. When the panel comes back the windows stay squeezed, and a window
/// that was closed and reopened across the episode has a new handle, so only identity can find it.</para>
///
/// <para><b>Two pieces of evidence, both required.</b> First, the <i>episode</i>: the engine must have watched
/// the display fall back and return recently (<see cref="LayoutMemory.NoteFallbackEpisodeEnded"/>, checked by
/// <see cref="LayoutMemory.Capture"/> against <see cref="LayoutMemory.AdoptionWindow"/>). Geometry alone cannot
/// tell a squeezed window from a small one the user opened in the corner — an 800×600 window at (100,100)
/// fits the fallback box whether or not a fallback ever happened — so without the episode nothing moves.
/// Second, the <i>geometry</i>, which is what this class judges: the window sits entirely inside the box the
/// desktop actually fell back to while its remembered rectangle does not. The box is the one observed, not an
/// assumed 1024×768: a 900×700 window fits 1024×768 and would read as undamaged, yet an 800×600 fallback
/// squeezes it. Both halves of the geometry are required: a remembered rectangle that fits the box too (a small
/// utility window that lives in the corner) means the squeeze changed nothing worth undoing.</para>
///
/// <para><b>Why not "much smaller than the current display".</b> On a 5120×2088 desktop almost every window
/// is much smaller than the display; a ratio would misfire on every chat window and dialog. The fallback box
/// is a fixed, system-defined shape that the squeeze always produces and a user's own placement rarely does.</para>
///
/// <para><b>What this costs.</b> A Reframe that was not running while the display was away has no episode to
/// point to, so windows squeezed in its absence are tracked where they are rather than moved, and their records
/// learn the squeezed shape like any other capture; the user drags them back once. Reframe starts with the
/// session, so in practice it is watching when the display goes away. That is the deliberate side of the trade:
/// never move a window on a guess.</para>
///
/// <para>The display-change restore (<see cref="LayoutMemory.GetRestorable"/>, driven by the engine's settle
/// path) is deliberately <b>not</b> gated here: it moves windows that were open throughout and are bound by
/// handle, after an actual topology event, which is the feature's whole purpose.</para>
///
/// <para>Pure function; unit-tested standalone.</para>
/// </summary>
public static class AdoptionGate
{
    /// <summary>
    /// Slack, in pixels, on every side of the fallback box. A resizable window's rectangle from
    /// <c>GetWindowRect</c> includes DWM's invisible resize border (7 px at 100 %, ~12 px at 175 %), so a
    /// window squeezed flush against the fallback desktop reports a frame that hangs a little outside it.
    /// </summary>
    public const int Slack = 32;

    /// <summary>
    /// Whether <paramref name="observed"/> (where the window is right now) looks squeezed into <paramref name="box"/>
    /// <b>and</b> <paramref name="remembered"/> (where the record says it belongs) does not fit in that box, so an
    /// adoption restore would undo a display-fallback squeeze rather than override the app's own placement.
    /// An invalid box (no fallback observed) never licenses a move.
    /// </summary>
    public static bool ShouldRestore(WindowRecord observed, WindowRecord remembered, FallbackBox box)
        => box.IsValid && Fits(observed, box) && !Fits(remembered, box);

    /// <summary>
    /// Whether the rectangle a restore would act on lies entirely inside <paramref name="box"/> (plus <see cref="Slack"/>).
    /// For a maximized window the on-screen rect is the whole monitor and says nothing, so its restore rectangle
    /// (<c>WINDOWPLACEMENT.rcNormalPosition</c>) is judged instead; that is also the rectangle the squeeze damaged.
    /// A degenerate (empty or inverted) rectangle never counts as fitting: there is nothing there to repair.
    /// </summary>
    public static bool Fits(WindowRecord r, FallbackBox box)
    {
        var (left, top, right, bottom) = r.ShowCmd == NativeMethods.SW_SHOWMAXIMIZED
            ? (r.NormLeft, r.NormTop, r.NormRight, r.NormBottom)
            : (r.Left, r.Top, r.Right, r.Bottom);
        return Fits(left, top, right, bottom, box);
    }

    /// <summary>Rectangle form of <see cref="Fits(WindowRecord, FallbackBox)"/>, in physical pixels of the virtual screen.</summary>
    public static bool Fits(int left, int top, int right, int bottom, FallbackBox box)
    {
        if (!box.IsValid) return false;
        if (right <= left || bottom <= top) return false;
        return left >= -Slack && top >= -Slack &&
               right <= box.Width + Slack && bottom <= box.Height + Slack;
    }
}
