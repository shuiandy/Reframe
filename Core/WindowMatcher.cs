namespace Reframe.Core;

/// <summary>One remembered record, reduced to what matching needs. <paramref name="Id"/> is the caller's opaque key back to the full record.</summary>
/// <param name="Id">Caller-side identifier of the record (index / slot id). Only used to hand the pairing back.</param>
/// <param name="Identity">Cross-session identity of the window this record was captured from.</param>
/// <param name="Ordinal">Position of this record within its identity group, assigned at capture time and stable thereafter.</param>
/// <param name="BoundHandle">The HWND this record is currently bound to, or <see cref="IntPtr.Zero"/> when unbound (loaded from disk, or its window died).</param>
/// <param name="Title">Caption last seen on this record's window. Evidence for a <i>confident</i> pairing only — see <see cref="WindowMatcher"/>.</param>
public readonly record struct MemoryEntryRef(
    int Id, WindowIdentity Identity, int Ordinal, IntPtr BoundHandle, string Title = "");

/// <summary>One currently-alive window offered as a match candidate.</summary>
/// <param name="Handle">The live HWND.</param>
/// <param name="Identity">Its cross-session identity.</param>
/// <param name="Title">Its caption right now. Evidence for a <i>confident</i> pairing only — see <see cref="WindowMatcher"/>.</param>
public readonly record struct LiveWindowRef(IntPtr Handle, WindowIdentity Identity, string Title = "");

/// <summary>
/// One (record → window) pairing produced by <see cref="WindowMatcher.Assign"/>.
///
/// <para><b><see cref="Confident"/> is the load-bearing part.</b> Every assignment — confident or not — is a
/// binding: the record now tracks that window, which is what stops a duplicate record piling up next to it on
/// every app restart. Only a <i>confident</i> assignment additionally means "we are sure this is the same
/// window as before", and only that may be acted on by moving the window back to the remembered geometry
/// (<c>LayoutMemory.Capture</c>'s adoption restore). A non-confident assignment is a bookkeeping slot, nothing
/// more: the window keeps whatever position it has and the record is refreshed from it.</para>
/// </summary>
public readonly record struct WindowAssignment(IntPtr Handle, int Id, bool Confident);

/// <summary>
/// The pure (Win32-free, side-effect-free) core of window-position persistence: decide which live window each
/// remembered record belongs to, now that a record is no longer keyed by HWND — and, separately, decide
/// whether that pairing is trustworthy enough to <i>move a window</i> on the strength of it.
///
/// <para><b>Why the two questions are separate.</b> Identity is <c>(process, class)</c>, and plenty of apps
/// give every window the same class. QQ NT (Electron) puts its main panel, every chat window and the image
/// viewer in <c>chrome_widgetwin_1</c>, so an identity group can hold four wildly different windows —
/// 1015x1015, 574x2002, 2220x1487, 1921x1152 — that nothing in the identity can tell apart. Pairing those by
/// ordinal is arbitrary (ordinals are handed out in ascending-HWND order, and handle values are meaningless
/// across a restart), so an "adopt and restore" built on ordinal pairing reliably reshapes a reopened chat
/// window into the main panel's tall strip or the image viewer's box. Observed on real hardware. Ordinal is a
/// fine <i>slot number</i> for the memory; it is not evidence of window identity.</para>
///
/// <para><b>Rules, in priority order</b> (see <see cref="Assign"/>):</para>
/// <list type="number">
/// <item><b>HWND fast path — confident.</b> A record still bound to a handle that is among the candidates
/// pairs with it directly. Within one session, with no process restarts, this is the <i>only</i> rule that
/// fires — which is what makes the identity work a strict superset of the old handle-keyed behaviour (zero
/// regression). It is by definition the same window; it is also never an adoption, because a record that
/// holds a live binding was not unbound to begin with.</item>
/// <item><b>Bound-but-absent records are skipped.</b> A record whose bound handle is alive (callers prune dead
/// bindings first) but simply wasn't offered this round — the borderless engine owns that window, it was just
/// moved by us, it is filtered out — keeps its binding and is left alone. Without this, its still-living
/// window's geometry could be handed to a <i>different</i> window of the same identity.</item>
/// <item><b>Unique exact title inside the identity group — confident.</b> The remaining <i>unbound</i> records
/// and remaining candidates are grouped by <see cref="WindowIdentity"/>. Inside a group, a record pairs
/// confidently with a candidate when their captions are equal after <see cref="NormalizeTitle"/> <b>and</b>
/// that caption occurs exactly once among the group's unresolved records and exactly once among its
/// unresolved candidates. Deliberately <b>exact</b>: no prefix, fuzzy or similarity matching — approximate
/// title matching is precisely what would put a chat window onto the image viewer's record.</item>
/// <item><b>1:1 leftover — confident.</b> If, after rule 3, exactly one unbound record and exactly one
/// unclaimed candidate remain in the group, they must be each other. This is what keeps single-window apps
/// working (Chrome's lone window whose caption follows the active tab, charmap, Notepad…), i.e. the original
/// case adoption restore was built for.</item>
/// <item><b>Everything else — bound, but NOT confident.</b> Leftover records (ordinal order) and leftover
/// candidates (ascending handle) are still paired positionally, so records don't pile up and a
/// long-lived-but-ambiguous app keeps a bounded number of slots. Such a pairing is reported with
/// <see cref="WindowAssignment.Confident"/> = false and must never move a window: when we cannot tell which
/// window is which, we record where it is instead of guessing where it belongs.</item>
/// <item><b>Ambiguity is resolved by refusing to guess.</b> Identities that aren't fully known
/// (<see cref="WindowIdentity.IsMatchable"/> false) never take part in rules 3–5 — only the HWND fast path can
/// pair them.</item>
/// </list>
///
/// <para><b>Invariants:</b> every handle is assigned at most once, every record at most once. The result is a
/// function of the inputs only (no clock, no Win32, no ambient state).</para>
/// </summary>
public static class WindowMatcher
{
    /// <summary>
    /// Normalized form of a caption for title matching: trimmed, nothing else. Comparison is
    /// <b>ordinal and case-sensitive</b> (<see cref="StringComparer.Ordinal"/>) — an app writes its own
    /// caption, so a case difference is a real difference, and the strictest equality is the one least likely
    /// to pair two windows that merely look alike. A blank caption normalizes to the empty string and is
    /// treated as <i>no evidence at all</i>: it never matches anything, not even another blank one.
    /// </summary>
    public static string NormalizeTitle(string? title) => title is null ? "" : title.Trim();

    /// <summary>
    /// Pair remembered records with live windows. Returns the assignments; records that found no window, and
    /// windows that match no record, are simply absent from the result. Read
    /// <see cref="WindowAssignment.Confident"/> before moving anything — see the rules on
    /// <see cref="WindowMatcher"/>.
    /// </summary>
    public static List<WindowAssignment> Assign(
        IReadOnlyList<MemoryEntryRef> entries, IReadOnlyList<LiveWindowRef> live)
    {
        var result = new List<WindowAssignment>();
        if (entries.Count == 0 || live.Count == 0) return result;

        // Candidate handles, deduped (a defensive measure: a duplicated handle must not be paired twice).
        var liveByHandle = new Dictionary<IntPtr, LiveWindowRef>(live.Count);
        foreach (var w in live)
            if (w.Handle != IntPtr.Zero)
                liveByHandle.TryAdd(w.Handle, w);

        var usedHandles = new HashSet<IntPtr>();
        var usedIds = new HashSet<int>();

        // ---- Rule 1: HWND fast path (same session, window never restarted) ----
        foreach (var e in entries)
        {
            if (e.BoundHandle == IntPtr.Zero) continue;
            if (!liveByHandle.ContainsKey(e.BoundHandle)) continue;
            if (!usedHandles.Add(e.BoundHandle)) continue; // two records claiming one handle: first wins
            usedIds.Add(e.Id);
            // Same handle, same session: this *is* the same window. (Not an adoption either way — a record
            // that holds a live binding was never unbound, so the caller's adoption path can't fire for it.)
            result.Add(new WindowAssignment(e.BoundHandle, e.Id, Confident: true));
        }

        // ---- Rules 2..5: identity groups over the *unbound* leftovers ----
        // Rule 2 is expressed by the BoundHandle == Zero filter: a record that still holds a (live) binding is
        // deliberately excluded from re-pairing.
        var freeEntries = new List<MemoryEntryRef>();
        foreach (var e in entries)
            if (e.BoundHandle == IntPtr.Zero && !usedIds.Contains(e.Id) && e.Identity.IsMatchable)
                freeEntries.Add(e);
        if (freeEntries.Count == 0) return result;

        var freeLive = new List<LiveWindowRef>();
        foreach (var w in liveByHandle.Values)
            if (!usedHandles.Contains(w.Handle) && w.Identity.IsMatchable)
                freeLive.Add(w);
        if (freeLive.Count == 0) return result;

        var liveGroups = new Dictionary<WindowIdentity, List<LiveWindowRef>>();
        foreach (var w in freeLive)
        {
            if (!liveGroups.TryGetValue(w.Identity, out var g))
                liveGroups[w.Identity] = g = new List<LiveWindowRef>();
            g.Add(w);
        }
        foreach (var g in liveGroups.Values)
            g.Sort(static (a, b) => a.Handle.ToInt64().CompareTo(b.Handle.ToInt64()));

        var entryGroups = new Dictionary<WindowIdentity, List<MemoryEntryRef>>();
        foreach (var e in freeEntries)
        {
            if (!entryGroups.TryGetValue(e.Identity, out var g))
                entryGroups[e.Identity] = g = new List<MemoryEntryRef>();
            g.Add(e);
        }

        // Iterate the *entry* groups in a deterministic order (identity string) so the result list order is
        // reproducible, which keeps tests and logs stable.
        var identities = new List<WindowIdentity>(entryGroups.Keys);
        identities.Sort(static (a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));

        foreach (var identity in identities)
        {
            if (!liveGroups.TryGetValue(identity, out var windows)) continue;
            var records = entryGroups[identity];
            records.Sort(static (a, b) => a.Ordinal != b.Ordinal ? a.Ordinal.CompareTo(b.Ordinal) : a.Id.CompareTo(b.Id));

            // ---- Rule 3: unique exact caption on both sides ----
            // "Unique" is measured over this group's *unresolved* pool (the records still unbound and the
            // candidates still unclaimed). Everything the fast path already settled is out of the pool by
            // construction, so the pool is exactly the set of windows we genuinely cannot tell apart yet.
            var recordTitleCounts = TitleCounts(records);
            var candidateByTitle = SingletonTitles(windows);

            foreach (var rec in records)                       // ordinal order ⇒ deterministic result order
            {
                string t = NormalizeTitle(rec.Title);
                if (t.Length == 0) continue;                   // no caption: no evidence
                if (recordTitleCounts[t] != 1) continue;       // two records wear this caption: can't tell which
                if (!candidateByTitle.TryGetValue(t, out var win)) continue; // no candidate, or more than one
                if (usedHandles.Contains(win.Handle)) continue;              // defensive; can't happen here
                usedHandles.Add(win.Handle);
                usedIds.Add(rec.Id);
                result.Add(new WindowAssignment(win.Handle, rec.Id, Confident: true));
            }

            var restRecords = new List<MemoryEntryRef>(records.Count);
            foreach (var r in records) if (!usedIds.Contains(r.Id)) restRecords.Add(r);
            if (restRecords.Count == 0) continue;

            var restWindows = new List<LiveWindowRef>(windows.Count);
            foreach (var w in windows) if (!usedHandles.Contains(w.Handle)) restWindows.Add(w);
            if (restWindows.Count == 0) continue;

            // ---- Rule 4 (confident) vs. rule 5 (bind only) ----
            // One record, one window, nothing else left in the group: they must be each other. Any other shape
            // still pairs positionally — records by ordinal, windows by ascending handle, both total orders so
            // the pairing is deterministic — but is flagged non-confident, i.e. bookkeeping that must not move
            // a window. Unequal counts pair min(records, windows): surplus candidates are left untouched (we
            // never invent a position) and surplus records stay unbound for a later round.
            bool confident = restRecords.Count == 1 && restWindows.Count == 1;

            int n = Math.Min(restRecords.Count, restWindows.Count);
            for (int i = 0; i < n; i++)
            {
                usedHandles.Add(restWindows[i].Handle);
                usedIds.Add(restRecords[i].Id);
                result.Add(new WindowAssignment(restWindows[i].Handle, restRecords[i].Id, confident));
            }
        }

        return result;
    }

    /// <summary>How many records in the group carry each (non-blank, normalized) caption.</summary>
    private static Dictionary<string, int> TitleCounts(List<MemoryEntryRef> records)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            string t = NormalizeTitle(r.Title);
            if (t.Length == 0) continue;
            counts[t] = counts.TryGetValue(t, out int c) ? c + 1 : 1;
        }
        return counts;
    }

    /// <summary>The candidates whose (non-blank, normalized) caption is unique in the group, keyed by caption.</summary>
    private static Dictionary<string, LiveWindowRef> SingletonTitles(List<LiveWindowRef> windows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in windows)
        {
            string t = NormalizeTitle(w.Title);
            if (t.Length == 0) continue;
            counts[t] = counts.TryGetValue(t, out int c) ? c + 1 : 1;
        }

        var single = new Dictionary<string, LiveWindowRef>(StringComparer.Ordinal);
        foreach (var w in windows)
        {
            string t = NormalizeTitle(w.Title);
            if (t.Length == 0) continue;
            if (counts[t] == 1) single[t] = w;
        }
        return single;
    }
}
