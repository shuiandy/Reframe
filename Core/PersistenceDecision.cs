namespace Reframe.Core;

/// <summary>What a periodic capture tick should do. See <see cref="PersistenceDecision.ForCapture"/>.</summary>
public enum CaptureDecision
{
    /// <summary>Do nothing: persistence is off, or the engine is not in its Idle state (frozen / restoring / settling).</summary>
    Skip,
    /// <summary>The live topology is a transient fallback: do not capture, do not touch the current key.</summary>
    PauseTransient,
    /// <summary>Something changed under us (key moved, or a fallback episode just ended): freeze and let the settle path decide.</summary>
    Freeze,
    /// <summary>Ordinary tick: record the current geometry under the current key.</summary>
    Capture,
}

/// <summary>What a <c>WM_DISPLAYCHANGE</c> notification should do. See <see cref="PersistenceDecision.ForDisplayChanged"/>.</summary>
public enum DisplayChangedDecision
{
    /// <summary>Persistence is off: do nothing at all.</summary>
    Ignore,
    /// <summary>Ordinary transition: freeze and let the settle path decide.</summary>
    Freeze,
    /// <summary>The topology is a fallback <i>right now</i>: record the episode first, then freeze.</summary>
    NoteTransientThenFreeze,
}

/// <summary>What the settle timer should do when it fires. See <see cref="PersistenceDecision.ForSettle"/>.</summary>
public enum SettleDecision
{
    /// <summary>Not frozen any more — a later transition superseded this timer.</summary>
    Ignore,
    /// <summary>Persistence was turned off mid-freeze: return to Idle without restoring.</summary>
    Abort,
    /// <summary>The borderless engine is mid-tick: re-arm the timer and retry shortly.</summary>
    RetryBusy,
    /// <summary>Still in a transient fallback and we still have patience: stay frozen and look again.</summary>
    WaitTransient,
    /// <summary>Still in a transient fallback but out of patience: return to Idle (capture stays blocked by the transient guard).</summary>
    StopWaitingTransient,
    /// <summary>Apply the remembered layout for the live key.</summary>
    Restore,
    /// <summary>Nothing to restore (same configuration, or one we have no memory of): resume capturing.</summary>
    Idle,
}

/// <summary>
/// The <see cref="PersistenceEngine"/> state machine's two branching points, extracted as pure functions of
/// their observable inputs so they can be unit-tested without Win32, timers or a worker thread. The engine
/// keeps the side effects (freezing, restoring, logging, arming timers); everything here is a decision table.
///
/// <para>Lives in its own file — and is linked into the test project — because <c>PersistenceEngine.cs</c>
/// itself cannot be: it pulls in the display-change listener and the window-mutation plumbing.</para>
/// </summary>
public static class PersistenceDecision
{
    /// <summary>
    /// What to do when Windows reports a display change.
    ///
    /// <para><b>Why this looks at the topology at all.</b> Freezing needs no information, so the obvious
    /// implementation just freezes — and that is precisely the hole. A <c>WM_DISPLAYCHANGE</c> is delivered
    /// <i>because</i> the mode just changed, which makes it the one instant we are guaranteed to be looking
    /// while a fallback desktop is actually on screen. For a fast switch it is the <b>only</b> such instant:
    /// if the panel goes and comes back inside the 800 ms settle debounce, both messages merely re-arm the
    /// settle timer, and the settle then wakes to a real topology whose key never moved — concluding that
    /// nothing happened, and letting the next capture record the still-squeezed windows as the truth. The same
    /// blind spot opens whenever the borderless engine is busy across the whole blackout. Observing here is
    /// what turns those into an ordinary "came back from a fallback" restore.</para>
    /// </summary>
    /// <param name="enabled">Window persistence is switched on.</param>
    /// <param name="transient">The topology observed at this instant is <see cref="LayoutKey.IsTransient"/>.</param>
    public static DisplayChangedDecision ForDisplayChanged(bool enabled, bool transient)
    {
        if (!enabled) return DisplayChangedDecision.Ignore;
        return transient ? DisplayChangedDecision.NoteTransientThenFreeze : DisplayChangedDecision.Freeze;
    }

    /// <summary>
    /// What the periodic capture tick should do.
    ///
    /// <para><b>Order matters.</b> <paramref name="transient"/> is checked <i>before</i> the key comparison. A
    /// fallback topology has a perfectly ordinary key, so the key check would see it as a topology change and
    /// freeze — and the settle that follows would then be deciding things while the desktop is 1024×768. The
    /// transient answer is "the engine has nothing to say about this topology at all", which includes not
    /// letting it move <c>_currentKey</c>; keeping the last real key is what lets the return be recognised.</para>
    ///
    /// <para><paramref name="sawTransient"/> forces a freeze even when the key is unchanged. That is the whole
    /// round trip: the display leaves, Windows squeezes every window into the fallback, the display comes back
    /// to the <i>same</i> configuration it left. Key-equality alone would conclude "nothing happened" and the
    /// squeezed windows would just be recorded as the new truth — so a fallback episode is itself treated as a
    /// topology change for the purpose of deciding to restore.</para>
    /// </summary>
    /// <param name="enabled">Window persistence is switched on.</param>
    /// <param name="idle">The engine is in its Idle state (not frozen / restoring / settling).</param>
    /// <param name="transient">The live topology is <see cref="LayoutKey.IsTransient"/>.</param>
    /// <param name="sawTransient">A transient topology has been observed since the last completed settle.</param>
    /// <param name="liveKey">Key computed from the live topology.</param>
    /// <param name="currentKey">The last key the engine accepted as real.</param>
    public static CaptureDecision ForCapture(bool enabled, bool idle, bool transient, bool sawTransient,
                                             string liveKey, string currentKey)
    {
        if (!enabled || !idle) return CaptureDecision.Skip;
        if (transient) return CaptureDecision.PauseTransient;
        if (sawTransient) return CaptureDecision.Freeze;                       // a fallback episode just ended
        if (!string.Equals(liveKey, currentKey, StringComparison.Ordinal)) return CaptureDecision.Freeze;
        return CaptureDecision.Capture;
    }

    /// <summary>
    /// What the settle timer should do when the quiet period after a display change expires.
    ///
    /// <para>A transient topology at settle time is <b>not</b> a configuration to settle on: restoring into it
    /// would apply the real layout to a 1024×768 desktop (Windows would re-squeeze it immediately) and adopting
    /// its key would lose the anchor needed to recognise the return. So we keep waiting while
    /// <paramref name="retriesLeft"/> allows. Waiting is bounded rather than indefinite because a frozen engine
    /// is a degraded engine; when patience runs out we fall back to Idle, which is safe precisely because
    /// <see cref="ForCapture"/> refuses to capture under a transient topology anyway — the only thing lost is
    /// the tighter freeze, not the protection.</para>
    ///
    /// <para><paramref name="keyChanged"/> <b>or</b> <paramref name="sawTransient"/> justifies a restore: see
    /// the remarks on <see cref="ForCapture"/> for why a same-key return still needs one.</para>
    /// </summary>
    /// <param name="frozen">The engine is in its Frozen state (otherwise this timer was superseded).</param>
    /// <param name="enabled">Window persistence is still switched on.</param>
    /// <param name="engineBusy">The borderless engine is mid-tick.</param>
    /// <param name="transient">The live topology is <see cref="LayoutKey.IsTransient"/>.</param>
    /// <param name="retriesLeft">Patience remains for another transient wait.</param>
    /// <param name="sawTransient">A transient topology has been observed since the last completed settle.</param>
    /// <param name="keyChanged">The live key differs from the last key the engine accepted as real.</param>
    /// <param name="hasSnapshot">A remembered layout exists for the live key.</param>
    public static SettleDecision ForSettle(bool frozen, bool enabled, bool engineBusy, bool transient,
                                           bool retriesLeft, bool sawTransient, bool keyChanged, bool hasSnapshot)
    {
        if (!frozen) return SettleDecision.Ignore;
        if (!enabled) return SettleDecision.Abort;
        if (engineBusy) return SettleDecision.RetryBusy;
        if (transient) return retriesLeft ? SettleDecision.WaitTransient : SettleDecision.StopWaitingTransient;
        if ((keyChanged || sawTransient) && hasSnapshot) return SettleDecision.Restore;
        return SettleDecision.Idle;
    }
}
