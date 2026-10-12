namespace Reframe.Core;

/// <summary>
/// Anti-thrash state (one per managed window). Mutable; held by the caller and passed to
/// <see cref="ThrashPolicy.Evaluate"/> in a serialized context (<see cref="Watcher"/>'s per-handle lock).
/// Fields are public to allow unit testing and serializable diagnostics.
/// </summary>
public sealed class ThrashState
{
    /// <summary>Start of the current 10s counting window (UTC).</summary>
    public DateTime WindowStartUtc;
    /// <summary>Number of reapplies in the current window.</summary>
    public int Count;
    /// <summary>Total warnings recorded over this window's lifetime (silenced once the cap is hit, until decay or rebuild).</summary>
    public int TotalWarns;
    /// <summary>Time the most recent warning was recorded (UTC); used for the 5min decay. The default value means never warned.</summary>
    public DateTime LastWarnUtc;
    /// <summary>How many counting windows in a row have hit the cap (reset by a window that closes below it, or an idle gap).</summary>
    public int ConsecutiveCapped;
    /// <summary>The current counting window has already hit the cap (so it is counted in <see cref="ConsecutiveCapped"/> once).</summary>
    public bool CappedThisWindow;
    /// <summary>Terminal: the window kept undoing every reapply, so the engine stopped re-applying to it (until the state is dropped).</summary>
    public bool GaveUp;
}

/// <summary>
/// The "reapply" throttle policy for an already-managed window (pure logic extracted from
/// <see cref="Watcher"/> for unit testing).
/// Rules:
/// <list type="bullet">
/// <item>Within a 10s sliding window, allow at most <see cref="MaxApplies"/> (=3) reapplies; beyond that,
///       back off this round (return false).</item>
/// <item>At most 1 warning per 10s window; at most <see cref="MaxWarns"/> (=2) over the window's whole
///       lifetime, silent thereafter.</item>
/// <item>Decay: when opening a new counting window, if it's been longer than <see cref="WarnDecay"/> (=5min)
///       since the last warning AND the previous window didn't hit the cap (i.e. no sustained thrashing),
///       reset <see cref="ThrashState.TotalWarns"/> to 0, to avoid going permanently silent after long uptime.</item>
/// <item>Give up: when <see cref="GiveUpAfterCappedWindows"/> (=3) back-to-back counting windows all hit the cap, the
///       window is fighting every reapply (e.g. a game that re-adds its own caption ~200ms after the border is
///       stripped). Backing off for the rest of each 10s window is not enough there: it still meant 3 frame flips
///       every 10s, forever, each one visibly stretching the game. So the state turns terminal
///       (<see cref="ThrashState.GaveUp"/>) and every later call is rejected; the caller drops the state (window
///       rebuilt, profile edited/disabled, engine toggled) to try again.</item>
/// </list>
/// No side effects, no Win32, no logging; whether to warn is returned via <paramref name="warn"/> for the
/// caller to decide how to surface it.
/// </summary>
public static class ThrashPolicy
{
    /// <summary>Counting-window length: 10s.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(10000);
    /// <summary>Max reapplies allowed within a single 10s window.</summary>
    public const int MaxApplies = 3;
    /// <summary>Max warnings recorded over the window's lifetime.</summary>
    public const int MaxWarns = 2;
    /// <summary>Warning decay duration: if it's been longer than this since the last warning with no thrashing in between, reset the accumulated warning count.</summary>
    public static readonly TimeSpan WarnDecay = TimeSpan.FromMinutes(5);
    /// <summary>Back-to-back capped counting windows after which the engine stops re-applying to the window for good.</summary>
    public const int GiveUpAfterCappedWindows = 3;

    /// <summary>
    /// Decide whether to allow a reapply this time. <paramref name="warn"/> returns "should a warning be
    /// recorded this time". The caller must invoke this on the same <paramref name="s"/> within a serialized
    /// context (the state is not thread-safe).
    /// </summary>
    /// <returns>true = allow this reapply; false = at the cap, back off this round.</returns>
    public static bool Evaluate(ThrashState s, DateTime nowUtc, out bool warn)
        => Evaluate(s, nowUtc, out warn, out _);

    /// <summary>
    /// As <see cref="Evaluate(ThrashState, DateTime, out bool)"/>; <paramref name="gaveUp"/> is true only on the
    /// one call that turns the state terminal (<see cref="ThrashState.GaveUp"/>), so the caller can say so once.
    /// </summary>
    public static bool Evaluate(ThrashState s, DateTime nowUtc, out bool warn, out bool gaveUp)
    {
        warn = false;
        gaveUp = false;

        if (s.GaveUp) return false;

        // The current window has expired: open a new one. If the previous window didn't hit the cap
        // (Count < MaxApplies, i.e. no sustained thrashing) and it's been longer than the decay threshold
        // since the last warning, reset the accumulated warnings so it can warn again after long uptime.
        if (nowUtc - s.WindowStartUtc > Window)
        {
            bool quietLastWindow = s.Count < MaxApplies;
            if (quietLastWindow && s.TotalWarns > 0 &&
                s.LastWarnUtc != default && nowUtc - s.LastWarnUtc > WarnDecay)
            {
                s.TotalWarns = 0;
            }
            // The capped streak only continues into an adjacent window: a window that stayed below the cap, or a
            // whole idle window in between (nothing needed reapplying), means the fight stopped.
            if (!s.CappedThisWindow || nowUtc - s.WindowStartUtc > Window + Window)
                s.ConsecutiveCapped = 0;
            s.WindowStartUtc = nowUtc;
            s.Count = 0;
            s.CappedThisWindow = false;
        }

        if (s.Count >= MaxApplies)
        {
            if (!s.CappedThisWindow)
            {
                s.CappedThisWindow = true;
                if (++s.ConsecutiveCapped >= GiveUpAfterCappedWindows)
                {
                    s.GaveUp = true;
                    gaveUp = true;
                    return false;
                }
            }

            // This window has hit the cap: record a warning only if this window hasn't warned yet
            // (LastWarnUtc isn't within this window) and the accumulated count is below the cap.
            bool warnedThisWindow = s.LastWarnUtc >= s.WindowStartUtc;
            if (!warnedThisWindow && s.TotalWarns < MaxWarns)
            {
                warn = true;
                s.TotalWarns++;
                s.LastWarnUtc = nowUtc;
            }
            return false;
        }

        s.Count++;
        return true;
    }
}
