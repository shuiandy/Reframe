using System;
using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// ThrashPolicy.Evaluate(ThrashState s, DateTime nowUtc, out bool warn) pure logic (agent-A contract target).
/// Semantics (see the ThrashPolicy comments):
/// <list type="bullet">
///   <item>Within a 10s sliding window, allow at most MaxApplies (=3) reapplies; from the 4th on, reject this round (return false).</item>
///   <item>When the window expires (now - WindowStart > 10s), open a new one and reset Count.</item>
///   <item>At most 1 warning per window; at most MaxWarns (=2) over the window's lifetime, silent thereafter.</item>
///   <item>Decay: when opening a new window, if the previous one didn't hit the cap (no sustained thrashing) and it's been &gt; 5min since the last warning, reset TotalWarns.</item>
/// </list>
/// A fixed nowUtc is fed in throughout; pure logic, no side effects.
/// </summary>
public class ThrashPolicyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Fresh state: window start set to nowUtc (simulating initialization on first takeover).</summary>
    private static ThrashState FreshAt(DateTime now) => new() { WindowStartUtc = now };

    // ---- Within-window allow/reject counting ----

    [Fact(DisplayName = "Within window: first 3 allowed, 4th rejected")]
    public void Window_FirstThreeAllow_FourthRejected()
    {
        var s = FreshAt(T0);

        Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out _));  // #1
        Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out _));  // #2
        Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(3), out _));  // #3 at cap
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(4), out _)); // #4 rejected (same 10s window)
    }

    [Fact(DisplayName = "Within window: after the cap, further calls always rejected (#5, #6 still false)")]
    public void Window_StaysRejectedAfterCap()
    {
        var s = FreshAt(T0);
        for (int i = 1; i <= 3; i++) Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(i), out _));
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(4), out _));
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(5), out _));
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(6), out _));
    }

    [Fact(DisplayName = "Count accumulates within the window: after the 3rd, Count == MaxApplies")]
    public void Window_CountReachesMax()
    {
        var s = FreshAt(T0);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(3), out _);
        Assert.Equal(ThrashPolicy.MaxApplies, s.Count);
    }

    // ---- Across windows: Count resets ----

    [Fact(DisplayName = "Across windows: after >10s a new window opens, Count resets, 3 more allowed")]
    public void CrossWindow_ResetsCount_AllowsAgain()
    {
        var s = FreshAt(T0);
        // Fill the first window
        ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(3), out _);
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(4), out _)); // at cap

        // Cross the 10s window (> 10s from T0): a new window opens, Count should reset
        var t2 = T0.AddSeconds(11);
        Assert.True(ThrashPolicy.Evaluate(s, t2, out _));                 // new window #1 allowed
        Assert.Equal(1, s.Count);
        Assert.Equal(t2, s.WindowStartUtc);                              // window start has reset
        Assert.True(ThrashPolicy.Evaluate(s, t2.AddSeconds(1), out _));  // #2
        Assert.True(ThrashPolicy.Evaluate(s, t2.AddSeconds(2), out _));  // #3
        Assert.False(ThrashPolicy.Evaluate(s, t2.AddSeconds(3), out _)); // #4 at cap again
    }

    [Fact(DisplayName = "Boundary: exactly 10s (== Window) is not expired (uses > comparison), still the same window")]
    public void CrossWindow_ExactlyWindowBoundary_NotExpired()
    {
        var s = FreshAt(T0);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(3), out _); // at cap
        // now - WindowStart == 10s, exactly not > Window, still the old window → rejected
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(10), out _));
        Assert.Equal(T0, s.WindowStartUtc); // no new window opened
    }

    // ---- Warnings: 1st/2nd warn=true, 3rd false (lifetime cap 2) ----

    [Fact(DisplayName = "Warnings: three consecutive saturated windows → 1st, 2nd warn=true, 3rd warn=false")]
    public void Warn_FirstTwoTrue_ThirdFalse()
    {
        var s = FreshAt(T0);

        // Saturate a window to the cap and return the warn of the cap-hitting call. Force a new window between
        // each with a >10s gap, and enter the next window immediately after a warning (< 5min) so decay isn't triggered.
        bool SaturateAndGetWarn(DateTime baseTime)
        {
            Assert.True(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(1), out _)); // #1
            Assert.True(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(2), out _)); // #2
            Assert.True(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(3), out _)); // #3 at cap
            Assert.False(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(4), out bool warn)); // #4 at cap → maybe warn
            return warn;
        }

        // Window 1: 11s gap, sustained thrashing (previous window hit the cap), no decay triggered
        bool w1 = SaturateAndGetWarn(T0);
        bool w2 = SaturateAndGetWarn(T0.AddSeconds(11));
        bool w3 = SaturateAndGetWarn(T0.AddSeconds(22));

        Assert.True(w1);   // accumulated #1
        Assert.True(w2);   // accumulated #2 (reaches the cap MaxWarns=2)
        Assert.False(w3);  // #3 silenced
        Assert.Equal(ThrashPolicy.MaxWarns, s.TotalWarns);
    }

    [Fact(DisplayName = "Warnings: multiple cap-hits within one window record only 1 (second cap-hit warn=false)")]
    public void Warn_OncePerWindow()
    {
        var s = FreshAt(T0);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out _);
        ThrashPolicy.Evaluate(s, T0.AddSeconds(3), out _);
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(4), out bool firstWarn));
        Assert.False(ThrashPolicy.Evaluate(s, T0.AddSeconds(5), out bool secondWarn));
        Assert.True(firstWarn);    // first cap-hit of this window records one
        Assert.False(secondWarn);  // hitting the cap again in the same window doesn't re-record
        Assert.Equal(1, s.TotalWarns);
    }

    [Fact(DisplayName = "Warnings: never warns while allowing (not at cap)")]
    public void Warn_NeverWhenAllowed()
    {
        var s = FreshAt(T0);
        Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(1), out bool w1));
        Assert.True(ThrashPolicy.Evaluate(s, T0.AddSeconds(2), out bool w2));
        Assert.False(w1);
        Assert.False(w2);
        Assert.Equal(0, s.TotalWarns);
    }

    // ---- Can warn again after 5min decay ----

    [Fact(DisplayName = "Decay: after going silent (at the cap), thrashing again following >5min idle → TotalWarns resets and warns again")]
    public void Decay_AfterFiveMinIdle_WarnsAgain()
    {
        var s = FreshAt(T0);

        bool SaturateAndGetWarn(DateTime baseTime)
        {
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(1), out _);
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(2), out _);
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(3), out _);
            Assert.False(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(4), out bool warn));
            return warn;
        }

        // Fill two windows, exhausting MaxWarns=2 (#1, #2); the third window is silent
        Assert.True(SaturateAndGetWarn(T0));
        Assert.True(SaturateAndGetWarn(T0.AddSeconds(11)));
        Assert.False(SaturateAndGetWarn(T0.AddSeconds(22)));
        Assert.Equal(ThrashPolicy.MaxWarns, s.TotalWarns);

        // Key: the previous window must be "below the cap" for decay to be allowed (quietLastWindow = Count < MaxApplies).
        // First a "quiet window" (only 1 allow, not at cap), and make it > 5min since the last warning.
        // The last warning happened around T0+22 (about T0+26s); open this quiet window 6 minutes later.
        var quiet = T0.AddMinutes(6);
        Assert.True(ThrashPolicy.Evaluate(s, quiet, out _)); // quiet window: only 1 allow, Count=1 < 3
        // Still not decayed at this moment (decay happens at the "open new window" decision and requires the previous window to be quiet — takes effect on the next new window)
        Assert.Equal(ThrashPolicy.MaxWarns, s.TotalWarns);

        // Open another new window (> 10s later): previous window quiet + > 5min since last warning → TotalWarns resets, can warn again
        var revived = quiet.AddSeconds(11);
        bool warnAgain = SaturateAndGetWarn(revived);
        Assert.True(warnAgain);            // warns again after decay
        Assert.Equal(1, s.TotalWarns);     // re-accumulated from 0 to 1
    }

    [Fact(DisplayName = "Decay not triggered: sustained thrashing (previous window at the cap) doesn't reset even across 5min")]
    public void Decay_NotTriggeredWhenStillThrashing()
    {
        var s = FreshAt(T0);

        bool SaturateAndGetWarn(DateTime baseTime)
        {
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(1), out _);
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(2), out _);
            ThrashPolicy.Evaluate(s, baseTime.AddSeconds(3), out _);
            Assert.False(ThrashPolicy.Evaluate(s, baseTime.AddSeconds(4), out bool warn));
            return warn;
        }

        // Exhaust the warning budget
        Assert.True(SaturateAndGetWarn(T0));
        Assert.True(SaturateAndGetWarn(T0.AddSeconds(11)));
        Assert.Equal(ThrashPolicy.MaxWarns, s.TotalWarns);

        // Even though it's been > 5min since the last warning, the next window "still hits the cap" (previous
        // window Count==MaxApplies, not quiet) → quietLastWindow isn't satisfied, no reset, stays silent.
        bool warn3 = SaturateAndGetWarn(T0.AddMinutes(6));
        Assert.False(warn3);
        Assert.Equal(ThrashPolicy.MaxWarns, s.TotalWarns); // still at the cap, not reset
    }
}
