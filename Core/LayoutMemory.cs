namespace Reframe.Core;

/// <summary>
/// One window's remembered geometry under a given <see cref="LayoutKey"/>. Stores two rectangles, both in
/// physical pixels: the on-screen rect (from <c>GetWindowRect</c>) used for the precise, non-activating
/// <c>SetWindowPos</c> restore of a visible normal window; and the <c>WINDOWPLACEMENT.rcNormalPosition</c>
/// restore rectangle (workspace coords) used to fix windows that are minimized / hidden-to-tray / maximized
/// via <c>SetWindowPlacement</c> — a display change resets <i>their</i> restore rect, which is why a tray app
/// comes back tiny in the top-left. Because a layout is only restored under an <b>identical</b> DisplayKey
/// (same monitors ⇒ same DPI and the same workspace↔screen mapping), both rectangles round-trip exactly with
/// no remapping. Identity (which window this belongs to) is kept next to the record in
/// <see cref="RememberedWindow"/>, not inside it.
/// </summary>
public sealed record WindowRecord(
    int Left, int Top, int Right, int Bottom,                  // GetWindowRect (screen) — visible-normal SetWindowPos path
    int NormLeft, int NormTop, int NormRight, int NormBottom,  // WINDOWPLACEMENT.rcNormalPosition (workspace) — hidden/minimized/maximized SetWindowPlacement path
    int ShowCmd);

/// <summary>One window as observed by a capture pass: who it is, what it's called, and where it sits.</summary>
public readonly record struct CapturedWindow(IntPtr Handle, WindowIdentity Identity, string Title, WindowRecord Record);

/// <summary>
/// One entry of the layout memory: a remembered geometry plus everything needed to find its window again in a
/// <i>later</i> session. Mutable (the store rewrites entries in place every capture round) and owned by the
/// single <see cref="PersistenceEngine"/> worker thread.
/// </summary>
public sealed class RememberedWindow
{
    /// <summary>Cross-session identity (process + class). See <see cref="WindowIdentity"/> for why the title isn't in it.</summary>
    public WindowIdentity Identity { get; set; } = WindowIdentity.Unknown;

    /// <summary>
    /// Slot number of this record inside its identity group, assigned when the record is created and stable
    /// for its lifetime: it keeps several windows of one app in several <i>separate</i> records instead of
    /// fighting over one, and gives the matcher a deterministic order for its last-resort pairing. Handed out
    /// in ascending-HWND order at capture time (deterministic, and needs no window creation timestamp).
    ///
    /// <para><b>An ordinal is bookkeeping, not identity.</b> Handle values are arbitrary across a restart, so
    /// "same ordinal" says nothing about "same window" — pairing by ordinal alone is never enough to justify
    /// <i>moving</i> a window, only to bind a record to it. See <see cref="WindowMatcher"/>.</para>
    /// </summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// Last seen window caption. Deliberately <b>not</b> part of <see cref="Identity"/> (a browser caption
    /// follows the active tab, so identity would drift on every tab switch), but it <i>is</i> the evidence the
    /// matcher uses to tell same-class sibling windows apart: an exact, group-unique caption match is one of
    /// the two ways a pairing can become confident enough to move a window. See <see cref="WindowMatcher"/>.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>The remembered geometry.</summary>
    public WindowRecord Record { get; set; } = null!;

    /// <summary>When this record was last refreshed by a capture. Drives aging (see <see cref="LayoutMemory.Trim"/>).</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// The HWND this record is currently bound to, or <see cref="IntPtr.Zero"/> when unbound. A binding is
    /// <b>session-scoped</b> and is never persisted: records read back from disk always start unbound (last
    /// session's handle values are meaningless — possibly reused by an unrelated window), and a record whose
    /// window died is unbound rather than deleted so its geometry survives until the app restarts and reclaims it.
    ///
    /// <para>Clearing the handle also clears <see cref="ConfidentBinding"/> — confidence is a property of a
    /// binding, not of the record, so it can never outlive the binding it describes.</para>
    /// </summary>
    public IntPtr Handle
    {
        get => _handle;
        private set
        {
            _handle = value;
            if (value == IntPtr.Zero) _confidentBinding = false; // no binding ⇒ no confidence in one
        }
    }

    /// <summary>
    /// Whether the matcher was <b>sure</b> that <see cref="Handle"/> is the same window this record was
    /// captured from (same HWND within a session, a group-unique exact caption match, or a forced 1:1) — as
    /// opposed to a positional ordinal pairing, which is bookkeeping and no evidence at all. See
    /// <see cref="WindowMatcher"/>.
    ///
    /// <para><b>This is what a restore is allowed to act on.</b> Moving a window is destructive: get it wrong
    /// and a QQ chat window is reshaped into the main panel's tall strip. So both the adoption restore
    /// (<see cref="LayoutMemory.Capture"/>'s return value) and the display-change / manual restore
    /// (<see cref="LayoutMemory.GetRestorable"/>) are gated on this flag, while ordinary capture tracking is
    /// not: a non-confidently bound record keeps following its window and recording where it is, it just never
    /// tells anyone to move it.</para>
    ///
    /// <para><b>Session-scoped, like <see cref="Handle"/>, and never written to disk.</b> A record read back
    /// from the file starts unbound, and an unbound record has no binding to be confident about — so there is
    /// nothing to persist, and no way for a stale "we were sure last week" to leak into a new session.</para>
    /// </summary>
    public bool ConfidentBinding
    {
        get => _handle != IntPtr.Zero && _confidentBinding;
        set => _confidentBinding = value;
    }

    /// <summary>
    /// When this record stopped having a live window — i.e. when <see cref="Unbind"/> was called — or, for a
    /// record read off disk, when it was loaded. Null while the record is bound.
    ///
    /// <para><b>What it is for.</b> Cross-session re-identification (a group-unique caption, a forced 1:1)
    /// tells us <i>which</i> window a record belongs to; it says nothing about <i>how long ago</i>. Adoption
    /// restore was built for the seconds-to-minutes gap in which an app comes back — a crash, a self-update, an
    /// installer, the apps launching after a reboot — but without a clock the same rule keeps firing forever,
    /// and a record from last week hijacks a freshly opened window that merely wears the same caption (the
    /// reopened QQ group chat yanked back to a days-old rectangle). This timestamp is what
    /// <see cref="IsAdoptionEligible"/> measures against <see cref="LayoutMemory.AdoptionWindow"/>.</para>
    ///
    /// <para><b>Session-scoped and never written to disk</b> — it belongs to <see cref="Handle"/>'s family of
    /// binding state, not to the remembered geometry. Persisting it would be meaningless anyway: it would
    /// describe a machine state that no longer exists (the app may have been restarted, or the machine
    /// rebooted, with no way to tell from the file). Records read back from disk are instead all anchored to
    /// the moment of the load (<see cref="LayoutMemory.ImportFromDisk"/>), which is exactly the semantics we
    /// want after a reboot: apps that come up squeezed in the first quarter of an hour (the TV was on another
    /// input) go home, anything opened later in the day is left where the user opened it. Freshness is one of
    /// the conditions, not the whole test — see <see cref="AdoptionGate"/> for the other. Contrast
    /// <see cref="LastSeenUtc"/>, which <i>is</i> persisted and drives aging of the geometry itself.</para>
    /// </summary>
    public DateTime? UnboundAtUtc { get; private set; }

    private IntPtr _handle;
    private bool _confidentBinding;

    /// <summary>
    /// Whether this record's window disappeared recently enough that re-finding it may be acted on by
    /// <i>moving</i> a window (see <see cref="LayoutMemory.AdoptionWindow"/>). A record that never lost a
    /// binding to begin with has no timestamp and is not eligible — it also has nothing to adopt, since the
    /// HWND fast path is what claims it, and that path is confident regardless of age.
    ///
    /// <para>Boundary: <b>exactly</b> <see cref="LayoutMemory.AdoptionWindow"/> still counts as fresh
    /// (<c>&lt;=</c>). A clock that jumped backwards yields a negative age and also counts as fresh — the
    /// failure mode we care about is a stale record moving a window, and a backwards NTP correction is no
    /// reason to drop a legitimate restart on the floor.</para>
    /// </summary>
    public bool IsAdoptionEligible(DateTime nowUtc)
        => UnboundAtUtc is { } since && nowUtc - since <= LayoutMemory.AdoptionWindow;

    /// <summary>
    /// Bind this record to a live window, recording whether the matcher was sure it is the same one. A record
    /// with a window again has no "unbound since" — the clock only starts when the window goes away.
    /// </summary>
    public void Bind(IntPtr handle, bool confident)
    {
        Handle = handle;
        ConfidentBinding = confident;
        if (handle != IntPtr.Zero) UnboundAtUtc = null;
    }

    /// <summary>
    /// Drop the session-scoped binding — and with it any confidence in it — stamping <see cref="UnboundAtUtc"/>
    /// with the moment the window went away. The geometry is kept.
    ///
    /// <para>Unbinding an already-unbound record is a no-op: re-stamping would silently renew the adoption
    /// window of a record whose window has been gone for days.</para>
    /// </summary>
    public void Unbind(DateTime nowUtc)
    {
        if (_handle == IntPtr.Zero) return;
        UnboundAtUtc = nowUtc;
        Handle = IntPtr.Zero;
    }

    /// <summary>Unbind with the ambient clock. See <see cref="Unbind(DateTime)"/>.</summary>
    public void Unbind() => Unbind(DateTime.UtcNow);

    /// <summary>
    /// Put an imported record into the unbound state anchored at <paramref name="loadedAtUtc"/> — the one
    /// place where "unbound since" is not the moment a window died, because the file cannot say when that was.
    /// See <see cref="UnboundAtUtc"/> and <see cref="LayoutMemory.ImportFromDisk"/>.
    /// </summary>
    public void MarkImportedUnbound(DateTime loadedAtUtc)
    {
        Handle = IntPtr.Zero;
        UnboundAtUtc = loadedAtUtc;
    }

    /// <summary>
    /// Re-anchor this record as if the adoption clock had been <b>stopped</b> for the interval
    /// <paramref name="episodeStartUtc"/>..<paramref name="episodeEndUtc"/>: the age it had when the interval
    /// began is the age it has when the interval ends.
    ///
    /// <para><b>Why the clock must stop.</b> The interval in question is a transient fallback episode — the
    /// desktop collapsed to Windows' no-display 1024x768 mode because a panel dropped off the bus. Nothing can
    /// happen during it: the engine will not capture, will not restore, and will not even look for windows to
    /// claim. Letting <see cref="LayoutMemory.AdoptionWindow"/> burn down through such an interval punishes a
    /// record for time in which it had no chance to be adopted. The concrete failure: Reframe starts with the
    /// login, the TV is still on the console's input, the layout file loads and anchors every record at that
    /// instant, and the user switches back twenty minutes later — every record is stale, the reclaim binds them
    /// all <i>without</i> confidence, nothing is moved, and the squeezed geometry then overwrites the good
    /// bucket. With the clock stopped the records are as fresh as they were at login and go home.</para>
    ///
    /// <para>Only ever called for records that were <i>still fresh when the episode began</i> (see
    /// <see cref="LayoutMemory.ShiftUnboundAnchors"/>), so a record that had already expired stays expired —
    /// a chat window closed three days ago must not be resurrected by a monitor blink.</para>
    ///
    /// <para>Expressed as "age is preserved" rather than "add the duration" so the result can never land in the
    /// future and cannot overflow. A clock that ran backwards (negative age) is treated as age zero.</para>
    /// </summary>
    public void PauseUnboundClock(DateTime episodeStartUtc, DateTime episodeEndUtc)
    {
        if (UnboundAtUtc is not { } since) return;
        var ageAtStart = episodeStartUtc - since;
        if (ageAtStart < TimeSpan.Zero) ageAtStart = TimeSpan.Zero;
        UnboundAtUtc = episodeEndUtc - ageAtStart;
    }
}

/// <summary>
/// The layout store: <c>DisplayKey → remembered windows</c>. Pure data + matching, no Win32 — the
/// <see cref="PersistenceEngine"/> owns one instance and drives it from its single worker thread, so this type
/// is not internally synchronized (single-thread ownership). Unit-tested standalone.
///
/// <para><b>Entries are keyed by identity, not by HWND.</b> The HWND is only a session-scoped binding cached on
/// the entry (fast path / who to actually move). This is the whole point: a window whose process restarted gets
/// a brand-new handle, and the old handle-keyed store simply forgot it (worse: <see cref="PruneDead"/> actively
/// deleted the record), so it was never restored. Now a dead handle only clears the binding, and the next
/// capture/restore re-claims the record by identity via <see cref="WindowMatcher"/>.</para>
/// </summary>
public sealed class LayoutMemory
{
    private readonly Dictionary<string, List<RememberedWindow>> _byKey = new(StringComparer.Ordinal);

    /// <summary>Default cap on remembered windows per DisplayKey (see <see cref="Trim"/>).</summary>
    public const int DefaultMaxPerKey = 200;

    /// <summary>Default age after which an unbound record is forgotten (see <see cref="Trim"/>).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// How long after its window disappeared a record may still be <i>adopted</i> — re-identified across
    /// sessions <b>and moved back</b> to its remembered rectangle. Past this, the same re-identification still
    /// binds the record (so records don't pile up) but is no longer confident, so nothing is moved: the record
    /// simply starts following the new window and recording where it is.
    ///
    /// <para><b>Why 15 minutes.</b> Adoption exists to cover a window that <i>came back</i>, and everything
    /// that legitimately brings one back is minutes at most: an app crashing and being relaunched, a
    /// self-update restarting itself, an installer replacing an app, or the whole desktop repopulating after a
    /// reboot (where the anchor is the layout file's load — see <see cref="ImportFromDisk"/> — and browsers,
    /// chat clients and editors trickle in over the first several minutes, some behind a slow VPN or a
    /// sign-in). A quarter of an hour clears all of those with room to spare.</para>
    ///
    /// <para><b>Why not longer.</b> The other side of the trade is a record that outlives its purpose and
    /// becomes position enforcement. A QQ group chat closed on Monday leaves a record whose caption is unique
    /// in its group forever; reopen that conversation on Thursday — QQ places it perfectly well by itself —
    /// and an untimed rule yanks it to Monday's rectangle, over and over, with no way for the user to tell why.
    /// That is the bug this constant exists to bound. Hours would re-admit it for anyone who closes and reopens
    /// chat windows during a working day; minutes-scale is the only range where "it came back" and "the user
    /// opened it again" are actually distinguishable without asking the user.</para>
    ///
    /// <para>Not persisted and not configurable: it is a property of the mechanism, not of a layout.</para>
    /// </summary>
    public static readonly TimeSpan AdoptionWindow = TimeSpan.FromMinutes(15);

    // ---- Capture ----

    /// <summary>
    /// Legacy shape (no identity available): record geometry keyed purely by handle. Kept for callers/tests
    /// that don't have identity to give; such records can only ever be re-found by the HWND fast path.
    /// </summary>
    public IReadOnlyList<(IntPtr Handle, WindowRecord Target)> Capture(
        string displayKey, IReadOnlyList<(IntPtr Handle, WindowRecord Record)> windows)
    {
        var list = new List<CapturedWindow>(windows.Count);
        foreach (var (h, r) in windows)
            list.Add(new CapturedWindow(h, WindowIdentity.Unknown, "", r));
        return Capture(displayKey, list, DateTime.UtcNow);
    }

    /// <summary>
    /// Record the current geometry of the given windows under <paramref name="displayKey"/>. Existing entries
    /// for other windows in that bucket are left intact (a window momentarily absent — minimized, or skipped
    /// because the engine owns it — keeps its remembered position for a later restore). Two keys are refused
    /// outright: the sentinel <see cref="LayoutKey.None"/> (no monitors), so a mid-transition empty topology
    /// can't clobber a bucket, and a transient fallback key (<see cref="LayoutKey.IsTransientKey"/> — the
    /// 1024×768 desktop Windows synthesises while a display is momentarily gone), because every window
    /// observed on it has been squeezed into that box and the bucket would be pure garbage.
    ///
    /// <para>Capture doubles as a re-claim pass: the observed windows are run through
    /// <see cref="WindowMatcher"/>, so a record left unbound by a restart (or loaded from disk) is adopted by
    /// the new window of the same identity instead of a duplicate entry piling up next to it.</para>
    ///
    /// <para><b>Adoption ⇒ restore, never overwrite</b> (<paramref name="adoptGeometry"/> = true, the periodic
    /// path). When a record that was <i>unbound at the start of this call</i> gets claimed <b>confidently</b>
    /// by a window that is <b>squeezed</b> (see below), the right meaning is "move that window to the
    /// remembered place", not "the remembered place is now wherever the window happens to be". Overwriting is
    /// what made the whole feature invisible: Chrome comes back squashed into the 1024×768 fallback box, the
    /// next 2 s capture claims its record and stamps the bad geometry over the good one, and nothing ever
    /// restores it. So such a record keeps its remembered <see cref="RememberedWindow.Record"/> (only handle /
    /// title / timestamp are refreshed) and is <b>returned to the caller</b> as a pending adoption restore.
    /// Records that were already bound keep the old behaviour: their geometry tracks the window, which is
    /// what makes this the live layout for this display.</para>
    ///
    /// <para><b>Adoption only repairs a squeeze</b> (<see cref="AdoptionGate"/>). Knowing <i>which</i> window a
    /// record describes, and that its window went away recently, still says nothing about whether the new
    /// window is in the wrong place. In the common case it is not: the user closed one window and opened
    /// another, or started an app at login, and the app placed the window itself. Moving it then is a fight
    /// nobody asked for. So a confident, fresh claim is only acted on when the window sits inside the display
    /// fallback box (where Windows squeezes everything while the only panel is away) and its remembered
    /// rectangle does not; otherwise the record is bound — confidently, so the next display change restores
    /// the window to <i>its own</i> place — and simply starts tracking where the window is.</para>
    ///
    /// <para><b>Binding and adoption are two different things.</b> Every window the matcher pairs is bound —
    /// that is what stops a duplicate record piling up next to the old one on each restart. Only a pairing the
    /// matcher marks <see cref="WindowAssignment.Confident"/> (same HWND, a group-unique exact caption match,
    /// or a 1:1 leftover — see <see cref="WindowMatcher"/>) is reported for an adoption restore. A record
    /// claimed by a window we cannot actually tell apart from its same-class siblings — QQ NT's main panel,
    /// chat windows and image viewer all live in <c>chrome_widgetwin_1</c> — takes the ordinary capture path
    /// instead: its geometry is overwritten with what the window looks like now, and nothing is moved.
    /// Guessing there is how a reopened chat window got reshaped into the main panel's tall strip.</para>
    ///
    /// <para><b>Adoption also expires</b> (<see cref="AdoptionWindow"/>). Being sure <i>which</i> window a
    /// record describes is necessary but not sufficient: the record must also have lost its window recently
    /// enough that "the app is coming back" is the plausible reading. A record whose window closed days ago is
    /// re-bound by the same caption rule and then treated as an ordinary capture — the user reopening a chat
    /// window is not an app restart, and moving it would be the store enforcing a position nobody asked it to
    /// keep. See <see cref="RememberedWindow.UnboundAtUtc"/>.</para>
    ///
    /// <para>The pending list is produced exactly once per binding — by the next round the record is bound and
    /// takes the normal tracking path, so a window that springs back is not fought over round after round.</para>
    /// </summary>
    /// <param name="adoptGeometry">
    /// true (periodic capture): records claimed by a <i>confident</i> pairing keep their remembered geometry
    /// and are reported for an adoption restore. false (the user's explicit "Capture now"): current geometry
    /// wins for every window and nothing is reported — that gesture means "remember where things are right now".
    /// </param>
    /// <returns>
    /// Windows that just claimed a remembered record, are confidently the same window that record came from,
    /// <b>and</b> look squeezed into the display fallback box, so they should be moved to it. Empty in most
    /// rounds — and deliberately empty whenever the pairing was a guess or the window is where its app put it.
    /// </returns>
    public IReadOnlyList<(IntPtr Handle, WindowRecord Target)> Capture(
        string displayKey, IReadOnlyList<CapturedWindow> windows, bool adoptGeometry = true)
        => Capture(displayKey, windows, DateTime.UtcNow, adoptGeometry);

    /// <summary>Capture with an explicit clock (unit tests). See the overload above for the semantics.</summary>
    public IReadOnlyList<(IntPtr Handle, WindowRecord Target)> Capture(
        string displayKey, IReadOnlyList<CapturedWindow> windows, DateTime nowUtc, bool adoptGeometry = true)
    {
        // Two keys are never written to. LayoutKey.None (no monitors) so a mid-transition empty topology can't
        // clobber a bucket, and a transient fallback key (LayoutKey.IsTransientKey — Windows' no-display
        // 1024x768/800x600 desktop) because every window observed on it has been squeezed into that box, so
        // the "layout" would be garbage. PersistenceEngine already refuses to call us in that state; this is
        // the second line of defence, right where the bucket would otherwise be created.
        if (string.IsNullOrEmpty(displayKey) || displayKey == LayoutKey.None) return Array.Empty<(IntPtr, WindowRecord)>();
        if (LayoutKey.IsTransientKey(displayKey)) return Array.Empty<(IntPtr, WindowRecord)>();
        if (windows.Count == 0) return Array.Empty<(IntPtr, WindowRecord)>();

        if (!_byKey.TryGetValue(displayKey, out var bucket))
            _byKey[displayKey] = bucket = new List<RememberedWindow>();

        // Which entries were unbound *before* this call decided anything — the entries whose geometry is a
        // memory to be applied rather than an observation to be refreshed. Entries appended later in this call
        // are new windows we've never seen and are deliberately outside this array's range.
        var wasUnbound = new bool[bucket.Count];
        for (int i = 0; i < bucket.Count; i++)
            wasUnbound[i] = bucket[i].Handle == IntPtr.Zero;

        var live = new List<LiveWindowRef>(windows.Count);
        foreach (var w in windows)
            live.Add(new LiveWindowRef(w.Handle, w.Identity, w.Title)); // the caption is what tells siblings apart
        var assigned = BindHandles(bucket, live, nowUtc);

        // Deterministic creation order for the leftovers, so ordinals are handed out ascending-HWND.
        var ordered = new List<CapturedWindow>(windows);
        ordered.Sort(static (a, b) => a.Handle.ToInt64().CompareTo(b.Handle.ToInt64()));

        List<(IntPtr, WindowRecord)>? adoptions = null;

        foreach (var w in ordered)
        {
            if (assigned.TryGetValue(w.Handle, out var hit))
            {
                int idx = hit.Index;
                var e = bucket[idx];
                // Upgrade a placeholder / partial identity once a complete one is available, and re-slot the
                // ordinal into the group it now belongs to (its old ordinal came from a different group).
                if (w.Identity.IsMatchable && w.Identity != e.Identity)
                {
                    e.Identity = w.Identity;
                    e.Ordinal = NextOrdinal(bucket, w.Identity, e);
                }

                // Adoption needs all four: the periodic path, a record that was unbound coming into this
                // call, a pairing the matcher is *sure* about, and a window that actually looks squeezed
                // into the display fallback box while its record does not (AdoptionGate). Drop any one of
                // them and this is an ordinary capture — the window stays put and the record learns its
                // current shape. The gate is what keeps "I closed one window and opened another" from
                // turning into a move: the app placed the new window itself, and that placement wins.
                if (adoptGeometry && idx < wasUnbound.Length && wasUnbound[idx] && hit.Confident
                    && AdoptionGate.ShouldRestore(w.Record, e.Record))
                    (adoptions ??= new List<(IntPtr, WindowRecord)>()).Add((w.Handle, e.Record)); // keep e.Record: the window moves, not the memory
                else
                    e.Record = w.Record;

                e.Title = w.Title;
                e.LastSeenUtc = nowUtc;
                // (the binding — handle + confidence — was already applied by BindHandles)
            }
            else
            {
                var fresh = new RememberedWindow
                {
                    Identity = w.Identity,
                    Ordinal = NextOrdinal(bucket, w.Identity, null),
                    Title = w.Title,
                    Record = w.Record,
                    LastSeenUtc = nowUtc,
                };
                // A brand-new record is created *from* this window, so it is trivially the same window: this
                // is what makes an ordinary session's records restorable on the next display change.
                fresh.Bind(w.Handle, confident: true);
                bucket.Add(fresh);
            }
        }

        return (IReadOnlyList<(IntPtr, WindowRecord)>?)adoptions ?? Array.Empty<(IntPtr, WindowRecord)>();
    }

    /// <summary>
    /// Re-claim records for the given live windows without recording geometry: pure (re)binding. Called before
    /// a restore so that records orphaned by a process restart — or freshly read off disk — find their new
    /// window and get moved. Returns how many records became newly bound.
    ///
    /// <para>Binding is unconditional here: confident or not, a pairing claims its slot (see
    /// <see cref="BindHandles"/>). The confidence distinction gates what may be <i>moved</i> — the adoption
    /// restore that <see cref="Capture"/> reports, and <see cref="GetRestorable"/> / <see cref="GetRestorePlan"/>
    /// on this path — and it now includes the freshness rule (<see cref="AdoptionWindow"/>): a record whose
    /// window has been gone for days is re-bound here but not moved, exactly as an ambiguous one is.</para>
    /// </summary>
    public int Reclaim(string displayKey, IReadOnlyList<LiveWindowRef> candidates)
        => Reclaim(displayKey, candidates, DateTime.UtcNow);

    /// <summary>Reclaim with an explicit clock (unit tests). See the overload above for the semantics.</summary>
    public int Reclaim(string displayKey, IReadOnlyList<LiveWindowRef> candidates, DateTime nowUtc)
    {
        if (!_byKey.TryGetValue(displayKey, out var bucket) || bucket.Count == 0) return 0;
        int before = 0;
        foreach (var e in bucket) if (e.Handle != IntPtr.Zero) before++;
        BindHandles(bucket, candidates, nowUtc);
        int after = 0;
        foreach (var e in bucket) if (e.Handle != IntPtr.Zero) after++;
        return after - before;
    }

    /// <summary>
    /// Run the pure matcher over a bucket and apply the resulting bindings. Returns handle → (bucket index,
    /// whether the matcher was confident it is the same window). <b>Every</b> assignment is bound — binding is
    /// how records stop piling up — but only the confident ones may be acted on by moving a window.
    ///
    /// <para>This is where the clock enters and immediately leaves again: each record's
    /// "unbound recently enough to be adopted" is evaluated here against <paramref name="nowUtc"/> and handed
    /// to the matcher as a plain bool, so the matcher stays a pure function of its inputs.</para>
    /// </summary>
    private static Dictionary<IntPtr, (int Index, bool Confident)> BindHandles(
        List<RememberedWindow> bucket, IReadOnlyList<LiveWindowRef> candidates, DateTime nowUtc)
    {
        var refs = new List<MemoryEntryRef>(bucket.Count);
        for (int i = 0; i < bucket.Count; i++)
        {
            var e = bucket[i];
            refs.Add(new MemoryEntryRef(i, e.Identity, e.Ordinal, e.Handle, e.Title, e.IsAdoptionEligible(nowUtc)));
        }

        var map = new Dictionary<IntPtr, (int, bool)>(candidates.Count);
        foreach (var a in WindowMatcher.Assign(refs, candidates))
        {
            bucket[a.Id].Bind(a.Handle, a.Confident);
            map[a.Handle] = (a.Id, a.Confident);
        }
        return map;
    }

    /// <summary>Smallest ordinal not already used inside <paramref name="identity"/>'s group (ignoring <paramref name="self"/>).</summary>
    private static int NextOrdinal(List<RememberedWindow> bucket, WindowIdentity identity, RememberedWindow? self)
    {
        var used = new HashSet<int>();
        foreach (var e in bucket)
            if (!ReferenceEquals(e, self) && e.Identity == identity)
                used.Add(e.Ordinal);
        int n = 0;
        while (used.Contains(n)) n++;
        return n;
    }

    // ---- Queries ----

    /// <summary>
    /// Whether we have any remembered windows for this key (i.e. a layout worth restoring). Counts unbound
    /// records too — a layout just read off disk is entirely unbound and must still count as "we know this display".
    ///
    /// <para><b>Deliberately not filtered by binding or confidence.</b> This is the "do we know this display
    /// at all" gate that decides whether the settle path even attempts a restore; the binding state at that
    /// instant is irrelevant, because <see cref="Reclaim"/> runs first and is what turns records into
    /// something actionable. Filtering here would mean a freshly-loaded layout could never be restored — the
    /// engine would give up before ever trying to claim its windows.</para>
    /// </summary>
    public bool HasSnapshot(string displayKey)
        => _byKey.TryGetValue(displayKey, out var b) && b.Count > 0;

    /// <summary>Total remembered records under this key (bound or not). Diagnostics / tests.</summary>
    public int CountFor(string displayKey)
        => _byKey.TryGetValue(displayKey, out var b) ? b.Count : 0;

    /// <summary>All DisplayKeys we hold a layout for. Diagnostics / tests.</summary>
    public IReadOnlyList<string> Keys => new List<string>(_byKey.Keys);

    /// <summary>The raw entries under this key, bound or not. Diagnostics / tests; the engine's normal paths use the methods above.</summary>
    public IReadOnlyList<RememberedWindow> EntriesFor(string displayKey)
        => _byKey.TryGetValue(displayKey, out var b) ? b : Array.Empty<RememberedWindow>();

    /// <summary>
    /// For the windows currently alive under <paramref name="displayKey"/>, return each one we have a
    /// <b>confidently bound</b> record for, paired with its target geometry. Windows with no record are
    /// omitted (we never invent a position); remembered windows that are no longer alive are simply not in the
    /// live set; and windows whose record was claimed by a guess are omitted too — see
    /// <see cref="GetRestorable"/> for why moving on a guess is the bug this gate exists to stop.
    /// </summary>
    public IReadOnlyList<(IntPtr Handle, WindowRecord Target)> GetRestorePlan(
        string displayKey, IEnumerable<IntPtr> liveHandles)
    {
        var plan = new List<(IntPtr, WindowRecord)>();
        if (!_byKey.TryGetValue(displayKey, out var bucket)) return plan;

        var byHandle = new Dictionary<IntPtr, RememberedWindow>(bucket.Count);
        foreach (var e in bucket)
            if (e.ConfidentBinding) byHandle[e.Handle] = e;

        foreach (var h in liveHandles)
            if (byHandle.TryGetValue(h, out var e))
                plan.Add((h, e.Record));
        return plan;
    }

    /// <summary>
    /// Every (handle, record) under this key that a restore may actually act on: bound to a live window
    /// <b>and</b> bound confidently. Includes windows hidden to the tray or minimized (which a live-window
    /// scan would miss), which is why the restore pass uses this rather than a fresh scan and filters by
    /// liveness/ownership itself.
    ///
    /// <para><b>Why confidence gates this and not just the adoption path.</b> The two restore paths reach the
    /// same windows by different routes. Adoption fires from <see cref="Capture"/> when a record goes from
    /// unbound to bound; the display-change / manual restore instead runs <see cref="Reclaim"/> and then moves
    /// everything bound. Gating only the first left the identical bug alive on the second: after a reboot the
    /// disk layout loads unbound, the monitor wakes, <see cref="Reclaim"/> binds QQ's four same-class records
    /// to whatever windows exist by ordinal, and the restore reshapes a chat window into the image viewer's
    /// box. Both routes now ask the same question — <see cref="RememberedWindow.ConfidentBinding"/>.</para>
    ///
    /// <para>Unbound records (nothing to act on right now) and non-confidently bound ones are both omitted.
    /// A binding is non-confident for either of two reasons: we cannot tell which window is which, or the
    /// record's window has been gone longer than <see cref="AdoptionWindow"/> and re-identifying it no longer
    /// licenses a move. Both still take part in ordinary capture tracking: they follow their window and record
    /// where it is, they are just never moved. (A record that keeps tracking one window is of course restored
    /// on the next display change — by then its geometry <i>is</i> that window's, claimed through the HWND
    /// fast path, and nothing about it is stale any more.)</para>
    /// </summary>
    public IReadOnlyList<(IntPtr Handle, WindowRecord Record)> GetRestorable(string displayKey)
    {
        var list = new List<(IntPtr, WindowRecord)>();
        if (_byKey.TryGetValue(displayKey, out var bucket))
            foreach (var e in bucket)
                if (e.ConfidentBinding)
                    list.Add((e.Handle, e.Record));
        return list;
    }

    // ---- Lifecycle ----

    /// <summary>
    /// Detach a handle from every bucket (called when a window is destroyed; handles get reused). The record
    /// itself is kept — only the session-scoped binding goes away, so the geometry is still there when the app
    /// comes back.
    /// </summary>
    public void ForgetWindow(IntPtr handle) => ForgetWindow(handle, DateTime.UtcNow);

    /// <summary>Detach a handle with an explicit clock (unit tests). See the overload above for the semantics.</summary>
    public void ForgetWindow(IntPtr handle, DateTime nowUtc)
    {
        if (handle == IntPtr.Zero) return;
        foreach (var bucket in _byKey.Values)
            foreach (var e in bucket)
                if (e.Handle == handle) e.Unbind(nowUtc); // clears the confidence flag, starts the adoption clock
    }

    /// <summary>
    /// Unbind every remembered handle that is no longer a live window, so a reused handle can't be restored to
    /// the previous window's position.
    ///
    /// <para><b>Semantics changed with identity matching:</b> this used to <i>delete</i> the record, which is
    /// precisely why a restarted app (Chrome getting a new HWND) was never restored — its remembered geometry
    /// was thrown away seconds after the old process exited. Now the record survives unbound and waits to be
    /// re-claimed by identity; only <see cref="Trim"/> ever deletes anything.</para>
    ///
    /// <para>Callers run this immediately before matching, which gives the matcher its key invariant: a
    /// non-zero binding means that window is alive. Unbinding also clears
    /// <see cref="RememberedWindow.ConfidentBinding"/>, so a record can never carry "we were sure" over from
    /// the window that just died into whatever claims it next — and stamps
    /// <see cref="RememberedWindow.UnboundAtUtc"/>, which is what starts the
    /// <see cref="AdoptionWindow"/> countdown: this is the exact moment "the app might be restarting" begins.</para>
    /// </summary>
    public void PruneDead(Func<IntPtr, bool> isAlive) => PruneDead(isAlive, DateTime.UtcNow);

    /// <summary>Prune with an explicit clock (unit tests). See the overload above for the semantics.</summary>
    public void PruneDead(Func<IntPtr, bool> isAlive, DateTime nowUtc)
    {
        foreach (var bucket in _byKey.Values)
            foreach (var e in bucket)
                if (e.Handle != IntPtr.Zero && !isAlive(e.Handle))
                    e.Unbind(nowUtc);
    }

    /// <summary>
    /// Bound the store: drop unbound records older than <paramref name="maxAge"/>, then cap each bucket at
    /// <paramref name="maxPerKey"/> records (keeping bound ones, then the most recently seen), then drop empty
    /// buckets. Bound records are never aged out — they belong to a window that is alive right now, however
    /// long ago it was last written. Returns the number of records dropped.
    /// </summary>
    public int Trim(int maxPerKey, TimeSpan maxAge, DateTime nowUtc)
    {
        int dropped = 0;
        List<string>? emptyKeys = null;

        foreach (var (key, bucket) in _byKey)
        {
            // No special case for transient fallback buckets: this ordinary aging *is* how they go away. Their
            // records arrive unbound from disk and can never be refreshed (Capture refuses the key) or bound
            // (nothing ever restores under it), so every one of them ages out below, and the bucket then falls
            // off the empty-key sweep at the end of the loop. See LayoutKey.IsTransientKey.
            if (maxAge > TimeSpan.Zero)
            {
                int removed = bucket.RemoveAll(e => e.Handle == IntPtr.Zero && nowUtc - e.LastSeenUtc > maxAge);
                dropped += removed;
            }

            if (maxPerKey > 0 && bucket.Count > maxPerKey)
            {
                bucket.Sort(static (a, b) =>
                {
                    bool ab = a.Handle != IntPtr.Zero, bb = b.Handle != IntPtr.Zero;
                    if (ab != bb) return ab ? -1 : 1;              // keep live windows first
                    return b.LastSeenUtc.CompareTo(a.LastSeenUtc); // then most recently seen
                });
                dropped += bucket.Count - maxPerKey;
                bucket.RemoveRange(maxPerKey, bucket.Count - maxPerKey);
            }

            if (bucket.Count == 0) (emptyKeys ??= new List<string>()).Add(key);
        }

        if (emptyKeys != null)
            foreach (var k in emptyKeys) _byKey.Remove(k);
        return dropped;
    }

    /// <summary>
    /// Stop the adoption clock for the interval <paramref name="episodeStartUtc"/>..<paramref name="episodeEndUtc"/>,
    /// during which a transient fallback topology made adoption impossible. Every <b>unbound</b> record that was
    /// still adoption-eligible when the interval began keeps the age it had then; everything else is untouched.
    /// Returns how many records were re-anchored.
    ///
    /// <para>The eligibility test is taken at <paramref name="episodeStartUtc"/> on purpose. It is the last
    /// moment at which the record's freshness was a fact about the world rather than an artefact of the
    /// blackout — so records that were already stale stay stale (no resurrection), and records that were fresh
    /// are not punished for a blackout they could not act during. See
    /// <see cref="RememberedWindow.PauseUnboundClock"/> for the failure this exists to stop.</para>
    ///
    /// <para>Bound records are skipped: they have no anchor (the clock only runs while a record has no window),
    /// and the HWND fast path that claims them is confident regardless of age.</para>
    /// </summary>
    public int ShiftUnboundAnchors(DateTime episodeStartUtc, DateTime episodeEndUtc)
    {
        if (episodeEndUtc <= episodeStartUtc) return 0;

        int shifted = 0;
        foreach (var bucket in _byKey.Values)
            foreach (var e in bucket)
            {
                if (e.Handle != IntPtr.Zero) continue;                 // bound: no clock to stop
                if (!e.IsAdoptionEligible(episodeStartUtc)) continue;  // already stale before the blackout began
                e.PauseUnboundClock(episodeStartUtc, episodeEndUtc);
                shifted++;
            }
        return shifted;
    }

    // ---- Disk round-trip ----

    /// <summary>Project the whole store into the serializable on-disk shape (see <see cref="LayoutStore"/>).</summary>
    public LayoutFile ExportForDisk()
    {
        var file = new LayoutFile { Version = LayoutFile.CurrentVersion };
        var keys = new List<string>(_byKey.Keys);
        keys.Sort(StringComparer.Ordinal); // stable file content: no spurious diffs between saves
        foreach (var key in keys)
        {
            var bucket = _byKey[key];
            if (bucket.Count == 0) continue;
            var layout = new PersistedLayout { DisplayKey = key };
            foreach (var e in bucket)
            {
                var r = e.Record;
                layout.Windows.Add(new PersistedWindow
                {
                    Process = e.Identity.ProcessName,
                    Class = e.Identity.ClassName,
                    Ordinal = e.Ordinal,
                    Title = e.Title,
                    Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom,
                    NormLeft = r.NormLeft, NormTop = r.NormTop, NormRight = r.NormRight, NormBottom = r.NormBottom,
                    ShowCmd = r.ShowCmd,
                    LastSeenUtc = e.LastSeenUtc,
                });
            }
            file.Layouts.Add(layout);
        }
        return file;
    }

    /// <summary>
    /// Replace the store's contents with what was read off disk.
    ///
    /// <para><b>Core correctness point:</b> a handle read back from a previous session is meaningless — the
    /// process is gone and Windows freely hands the same numeric handle to an unrelated window. Handles are
    /// therefore never written to the file in the first place, and every imported record starts
    /// <b>unbound</b> (<c>Handle == IntPtr.Zero</c>), which keeps it out of the matcher's HWND fast path: an
    /// imported layout can only ever be claimed by identity. Nothing else in the engine may assume otherwise.</para>
    ///
    /// <para>The same goes for <see cref="RememberedWindow.ConfidentBinding"/>: it describes <i>this</i>
    /// session's binding, so there is nothing to write and nothing to read back. An imported record is unbound
    /// and therefore not confident, and it can only become confident by being claimed again on evidence
    /// available right now.</para>
    ///
    /// <para><b>The load is the adoption anchor.</b> Every imported record gets
    /// <see cref="RememberedWindow.UnboundAtUtc"/> = the moment of this call, so the
    /// <see cref="AdoptionWindow"/> is measured from startup rather than from a per-record timestamp. That is
    /// the honest reading of the file: it cannot say when each window went away (the machine may have been off
    /// for a week), only that none of them exist yet. The behaviour that falls out is the one we want after a
    /// reboot — apps that come up squeezed into the display fallback box in the first quarter of an hour are
    /// put back on the remembered layout (subject to <see cref="AdoptionGate"/>: a window that comes up where
    /// its app put it is left there), while something the user opens hours later is left exactly where it
    /// opened, even if a record with its caption is still sitting in the file.</para>
    /// </summary>
    public void ImportFromDisk(LayoutFile? file) => ImportFromDisk(file, DateTime.UtcNow);

    /// <summary>Import with an explicit load timestamp (unit tests). See the overload above for the semantics.</summary>
    public void ImportFromDisk(LayoutFile? file, DateTime loadedAtUtc)
    {
        _byKey.Clear();
        if (file?.Layouts == null) return;

        foreach (var layout in file.Layouts)
        {
            string key = layout.DisplayKey ?? "";
            if (string.IsNullOrEmpty(key) || key == LayoutKey.None) continue;
            // Transient fallback buckets (LayoutKey.IsTransientKey) are deliberately NOT dropped here. They are
            // almost always garbage written while a panel was off the bus — but "almost always" is not a licence
            // to destroy data on sight: someone genuinely running a single 1024x768 primary (a kiosk, a VM
            // console) would have their whole memory deleted the first time this build started. Capture refuses
            // to write such a key, so the bucket can never be refreshed, its LastSeenUtc stops advancing, and
            // Trim's ordinary 30-day aging removes it. Slower than a purge, and it cannot hurt anyone.
            if (layout.Windows == null || layout.Windows.Count == 0) continue;

            if (!_byKey.TryGetValue(key, out var bucket))
                _byKey[key] = bucket = new List<RememberedWindow>();

            foreach (var w in layout.Windows)
            {
                if (w == null) continue;
                var loaded = new RememberedWindow
                {
                    Identity = WindowIdentity.Create(w.Process, w.Class),
                    Ordinal = w.Ordinal,
                    Title = w.Title ?? "",
                    Record = new WindowRecord(w.Left, w.Top, w.Right, w.Bottom,
                                              w.NormLeft, w.NormTop, w.NormRight, w.NormBottom, w.ShowCmd),
                    LastSeenUtc = w.LastSeenUtc,
                };
                // Last session's handle is meaningless — identity-only from here — and the adoption clock for
                // every imported record starts now (see the remarks above).
                loaded.MarkImportedUnbound(loadedAtUtc);
                bucket.Add(loaded);
            }

            if (bucket.Count == 0) _byKey.Remove(key);
        }
    }
}
