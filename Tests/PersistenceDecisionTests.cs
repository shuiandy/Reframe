using Reframe.Core;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// The persistence state machine's two branching points, exercised as pure functions. The engine around them
/// needs Win32 and timers and is not unit-tested; these are the decisions that actually caused the bug —
/// capturing under, and settling on, a display topology that only existed because the panel had briefly
/// dropped off the bus.
/// </summary>
public class PersistenceDecisionTests
{
    private const string Real = "7680x2160@0,0#168*";
    private const string Other = "3840x2160@0,0#144*";
    private const string Fallback = "1024x768@0,0#120*";

    // ---- ForDisplayChanged ----

    [Fact]
    public void A_display_change_message_observes_the_topology_before_freezing()
    {
        Assert.Equal(DisplayChangedDecision.Ignore, PersistenceDecision.ForDisplayChanged(enabled: false, transient: true));
        Assert.Equal(DisplayChangedDecision.Freeze, PersistenceDecision.ForDisplayChanged(enabled: true, transient: false));
        Assert.Equal(DisplayChangedDecision.NoteTransientThenFreeze,
            PersistenceDecision.ForDisplayChanged(enabled: true, transient: true));
    }

    /// <summary>
    /// Drives the three decision tables the way the engine composes them, carrying the one piece of state that
    /// links them (<c>_sawTransient</c>). Lets a whole episode be written as a sequence of events.
    /// </summary>
    private sealed class EngineModel
    {
        public bool SawTransient;
        public string CurrentKey = Real;

        public void DisplayChanged(bool transient)
        {
            if (PersistenceDecision.ForDisplayChanged(enabled: true, transient) == DisplayChangedDecision.NoteTransientThenFreeze)
                SawTransient = true;
        }

        public CaptureDecision CaptureTick(bool idle, bool transient, string liveKey)
        {
            var d = PersistenceDecision.ForCapture(true, idle, transient, SawTransient, liveKey, CurrentKey);
            if (d == CaptureDecision.PauseTransient) SawTransient = true;
            return d;
        }

        public SettleDecision Settle(bool transient, string liveKey, bool hasSnapshot, bool engineBusy = false)
        {
            var d = PersistenceDecision.ForSettle(frozen: true, enabled: true, engineBusy: engineBusy,
                                                  transient: transient, retriesLeft: true, sawTransient: SawTransient,
                                                  keyChanged: liveKey != CurrentKey, hasSnapshot: hasSnapshot);
            if (transient) SawTransient = true;
            if (d is SettleDecision.Restore or SettleDecision.Idle) { SawTransient = false; CurrentKey = liveKey; }
            return d;
        }
    }

    [Fact]
    public void A_blackout_shorter_than_the_settle_debounce_is_still_caught_and_still_owes_a_restore()
    {
        // The sub-debounce race. Panel drops at t=0 and is back at t=300ms, so BOTH display-change messages
        // land inside the 800 ms settle window and only ever re-arm the timer — no capture tick and no settle
        // ever runs while the topology is transient. Without observing at message time the episode is
        // completely invisible, the settle wakes to an unchanged key, and the squeezed windows become truth.
        var e = new EngineModel();

        e.DisplayChanged(transient: true);   // t=0     panel gone: THE only look we get
        e.DisplayChanged(transient: false);  // t=300ms panel back, settle re-armed
        Assert.True(e.SawTransient);

        // t=1100ms: the settle finally fires, on a real topology whose key never moved.
        Assert.Equal(SettleDecision.Restore, e.Settle(transient: false, liveKey: Real, hasSnapshot: true));
    }

    [Fact]
    public void Without_the_message_time_observation_the_same_blackout_is_invisible()
    {
        // Regression guard for the above: identical sequence with the observation removed yields Idle — the
        // engine concludes nothing happened and the next capture records the squeezed geometry. If this ever
        // starts returning Restore the two tests have stopped telling different stories.
        var e = new EngineModel();

        e.DisplayChanged(transient: false);  // pretend we only ever saw the "after" state
        e.DisplayChanged(transient: false);

        Assert.False(e.SawTransient);
        Assert.Equal(SettleDecision.Idle, e.Settle(transient: false, liveKey: Real, hasSnapshot: true));
    }

    [Fact]
    public void A_blackout_spent_entirely_behind_an_engine_busy_retry_is_still_caught()
    {
        // The other blind spot: the borderless engine is mid-tick for the whole blackout, so every settle
        // returns RetryBusy. The message-time observation is what survives it.
        var e = new EngineModel();

        e.DisplayChanged(transient: true);
        Assert.Equal(SettleDecision.RetryBusy, e.Settle(transient: true, liveKey: Fallback, hasSnapshot: false, engineBusy: true));
        Assert.Equal(SettleDecision.RetryBusy, e.Settle(transient: false, liveKey: Real, hasSnapshot: true, engineBusy: true));

        Assert.Equal(SettleDecision.Restore, e.Settle(transient: false, liveKey: Real, hasSnapshot: true));
    }

    [Fact]
    public void A_slow_blackout_is_caught_by_the_capture_tick_as_before()
    {
        // The long episode still works the way it did: the message-time look is an addition, not a replacement.
        var e = new EngineModel();

        e.DisplayChanged(transient: true);
        Assert.Equal(CaptureDecision.Skip, e.CaptureTick(idle: false, transient: true, Fallback)); // frozen
        Assert.Equal(SettleDecision.WaitTransient, e.Settle(transient: true, liveKey: Fallback, hasSnapshot: false));
        Assert.Equal(SettleDecision.Restore, e.Settle(transient: false, liveKey: Real, hasSnapshot: true));
        Assert.False(e.SawTransient); // discharged by the restore
    }

    // ---- ForCapture ----

    [Fact]
    public void Capture_is_skipped_when_disabled_or_not_idle()
    {
        Assert.Equal(CaptureDecision.Skip,
            PersistenceDecision.ForCapture(enabled: false, idle: true, transient: false, sawTransient: false, Real, Real));
        Assert.Equal(CaptureDecision.Skip,
            PersistenceDecision.ForCapture(enabled: true, idle: false, transient: false, sawTransient: false, Real, Real));
        // Even a transient topology yields Skip when the engine is not eligible at all — the outer guards win.
        Assert.Equal(CaptureDecision.Skip,
            PersistenceDecision.ForCapture(enabled: true, idle: false, transient: true, sawTransient: false, Fallback, Real));
    }

    [Fact]
    public void An_ordinary_tick_on_an_unchanged_topology_captures()
    {
        Assert.Equal(CaptureDecision.Capture,
            PersistenceDecision.ForCapture(enabled: true, idle: true, transient: false, sawTransient: false, Real, Real));
    }

    [Fact]
    public void A_changed_key_freezes_instead_of_capturing()
    {
        Assert.Equal(CaptureDecision.Freeze,
            PersistenceDecision.ForCapture(enabled: true, idle: true, transient: false, sawTransient: false, Other, Real));
    }

    [Fact]
    public void A_transient_topology_pauses_and_is_checked_before_the_key_comparison()
    {
        // THE ordering fix. The fallback has a perfectly ordinary key, so the key check would call it a
        // topology change and freeze — and the settle that follows would be deciding things while the desktop
        // is 1024x768. "Pause" means the engine has nothing to say about this topology at all, which crucially
        // includes not letting it move _currentKey.
        Assert.Equal(CaptureDecision.PauseTransient,
            PersistenceDecision.ForCapture(enabled: true, idle: true, transient: true, sawTransient: false, Fallback, Real));
        // ...and it stays paused however many ticks later, with the episode already flagged.
        Assert.Equal(CaptureDecision.PauseTransient,
            PersistenceDecision.ForCapture(enabled: true, idle: true, transient: true, sawTransient: true, Fallback, Real));
    }

    [Fact]
    public void The_end_of_a_fallback_episode_freezes_even_though_the_key_came_back_unchanged()
    {
        // The whole round trip: the panel leaves, Windows squeezes every window into the fallback, the panel
        // returns to the very configuration it left. Key equality alone would conclude "nothing happened" and
        // record the squeezed geometry as the new truth.
        Assert.Equal(CaptureDecision.Freeze,
            PersistenceDecision.ForCapture(enabled: true, idle: true, transient: false, sawTransient: true, Real, Real));
    }

    // ---- ForSettle ----

    private static SettleDecision Settle(bool frozen = true, bool enabled = true, bool engineBusy = false,
                                         bool transient = false, bool retriesLeft = true, bool sawTransient = false,
                                         bool keyChanged = false, bool hasSnapshot = true)
        => PersistenceDecision.ForSettle(frozen, enabled, engineBusy, transient, retriesLeft, sawTransient,
                                         keyChanged, hasSnapshot);

    [Fact]
    public void Settle_guards_run_in_order_frozen_enabled_busy()
    {
        Assert.Equal(SettleDecision.Ignore, Settle(frozen: false, keyChanged: true));
        Assert.Equal(SettleDecision.Abort, Settle(enabled: false, keyChanged: true));
        Assert.Equal(SettleDecision.RetryBusy, Settle(engineBusy: true, keyChanged: true));
        // Busy is checked before transient: letting the borderless engine finish is orthogonal and cheaper.
        Assert.Equal(SettleDecision.RetryBusy, Settle(engineBusy: true, transient: true));
    }

    [Fact]
    public void A_transient_topology_at_settle_waits_instead_of_restoring_or_adopting_its_key()
    {
        // Restoring into a 1024x768 desktop just gets the layout re-squeezed, and adopting its key would throw
        // away the anchor needed to recognise the panel's return.
        Assert.Equal(SettleDecision.WaitTransient, Settle(transient: true, keyChanged: true, hasSnapshot: true));
        Assert.Equal(SettleDecision.WaitTransient, Settle(transient: true, keyChanged: true, hasSnapshot: false));
        Assert.Equal(SettleDecision.WaitTransient, Settle(transient: true, sawTransient: true));
    }

    [Fact]
    public void Waiting_for_a_transient_topology_is_bounded()
    {
        // A permanently frozen engine would be a worse failure than the one being fixed. Giving up is safe
        // because ForCapture still refuses to capture while transient (asserted here so the two stay linked).
        Assert.Equal(SettleDecision.StopWaitingTransient,
            Settle(transient: true, retriesLeft: false, keyChanged: true));
        Assert.Equal(CaptureDecision.PauseTransient,
            PersistenceDecision.ForCapture(enabled: true, idle: true, transient: true, sawTransient: true, Fallback, Real));
    }

    [Fact]
    public void A_genuinely_changed_topology_with_a_remembered_layout_restores()
    {
        Assert.Equal(SettleDecision.Restore, Settle(keyChanged: true, hasSnapshot: true));
    }

    [Fact]
    public void A_same_key_return_from_a_fallback_episode_restores_too()
    {
        // The user-visible half of the fix: windows squeezed while the panel was away are put back even though
        // the configuration is byte-for-byte the one it left.
        Assert.Equal(SettleDecision.Restore, Settle(keyChanged: false, sawTransient: true, hasSnapshot: true));
    }

    [Fact]
    public void Nothing_is_restored_without_a_reason_or_without_a_memory()
    {
        // A spurious same-key WM_DISPLAYCHANGE must not pull windows back over the user's recent moves...
        Assert.Equal(SettleDecision.Idle, Settle(keyChanged: false, sawTransient: false, hasSnapshot: true));
        // ...and a configuration we have no memory of is simply adopted and captured from now on.
        Assert.Equal(SettleDecision.Idle, Settle(keyChanged: true, hasSnapshot: false));
        Assert.Equal(SettleDecision.Idle, Settle(sawTransient: true, hasSnapshot: false));
    }

    // ---- The whole episode, step by step ----

    [Fact]
    public void The_full_input_switch_episode_never_captures_or_restores_under_the_fallback()
    {
        // 1. Steady state on the real panel.
        Assert.Equal(CaptureDecision.Capture,
            PersistenceDecision.ForCapture(true, idle: true, transient: false, sawTransient: false, Real, Real));

        // 2. The panel drops off the bus. WM_DISPLAYCHANGE freezes the engine; the settle finds the fallback
        //    and keeps waiting — _currentKey stays on the real panel.
        Assert.Equal(SettleDecision.WaitTransient,
            PersistenceDecision.ForSettle(frozen: true, enabled: true, engineBusy: false, transient: true,
                                          retriesLeft: true, sawTransient: true, keyChanged: true, hasSnapshot: false));

        // 3. Capture ticks arriving during the episode do nothing (this is where the garbage buckets came from).
        Assert.Equal(CaptureDecision.PauseTransient,
            PersistenceDecision.ForCapture(true, idle: true, transient: true, sawTransient: true, Fallback, Real));

        // 4. The panel returns to the same configuration. Even with an unchanged key, the episode justifies a
        //    restore of the layout that was squeezed while it was away.
        Assert.Equal(SettleDecision.Restore,
            PersistenceDecision.ForSettle(frozen: true, enabled: true, engineBusy: false, transient: false,
                                          retriesLeft: true, sawTransient: true, keyChanged: false, hasSnapshot: true));

        // 5. Once the engine has settled (sawTransient cleared) it is back to ordinary capture.
        Assert.Equal(CaptureDecision.Capture,
            PersistenceDecision.ForCapture(true, idle: true, transient: false, sawTransient: false, Real, Real));
    }
}
