namespace Reframe.Core;

/// <summary>One remembered record, reduced to what matching needs. <paramref name="Id"/> is the caller's opaque key back to the full record.</summary>
/// <param name="Id">Caller-side identifier of the record (index / slot id). Only used to hand the pairing back.</param>
/// <param name="Identity">Cross-session identity of the window this record was captured from.</param>
/// <param name="Ordinal">Position of this record within its identity group, assigned at capture time and stable thereafter.</param>
/// <param name="BoundHandle">The HWND this record is currently bound to, or <see cref="IntPtr.Zero"/> when unbound (loaded from disk, or its window died).</param>
public readonly record struct MemoryEntryRef(int Id, WindowIdentity Identity, int Ordinal, IntPtr BoundHandle);

/// <summary>One currently-alive window offered as a match candidate.</summary>
public readonly record struct LiveWindowRef(IntPtr Handle, WindowIdentity Identity);

/// <summary>
/// The pure (Win32-free, side-effect-free) core of window-position persistence: decide which live window each
/// remembered record belongs to, now that a record is no longer keyed by HWND.
///
/// <para><b>Rules, in priority order</b> (see <see cref="Assign"/>):</para>
/// <list type="number">
/// <item><b>HWND fast path.</b> A record still bound to a handle that is among the candidates pairs with it
/// directly. Within one session, with no process restarts, this is the <i>only</i> rule that fires — which is
/// what makes the identity work a strict superset of the old handle-keyed behaviour (zero regression).</item>
/// <item><b>Bound-but-absent records are skipped.</b> A record whose bound handle is alive (callers prune dead
/// bindings first) but simply wasn't offered this round — the borderless engine owns that window, it was just
/// moved by us, it is filtered out — keeps its binding and is left alone. Without this, its still-living
/// window's geometry could be handed to a <i>different</i> window of the same identity.</item>
/// <item><b>Identity groups, paired by ordinal.</b> The remaining <i>unbound</i> records and the remaining
/// candidates are grouped by <see cref="WindowIdentity"/>. Inside a group, records are ordered by ordinal and
/// candidates by ascending handle value — both total orders, so the pairing is deterministic and needs no
/// creation timestamps — and paired positionally. Unequal counts pair min(records, candidates): surplus
/// candidates are left untouched (we never invent a position) and surplus records stay unbound, waiting for a
/// later round to claim them.</item>
/// <item><b>Ambiguity is resolved by refusing to guess.</b> Identities that aren't fully known
/// (<see cref="WindowIdentity.IsMatchable"/> false) never take part in rule 3 — only the HWND fast path can
/// pair them.</item>
/// </list>
///
/// <para><b>Invariants:</b> every handle is assigned at most once, every record at most once. The result is a
/// function of the inputs only (no clock, no Win32, no ambient state).</para>
/// </summary>
public static class WindowMatcher
{
    /// <summary>
    /// Pair remembered records with live windows. Returns (handle → record id) assignments; records that found
    /// no window, and windows that match no record, are simply absent from the result.
    /// </summary>
    public static List<(IntPtr Handle, int Id)> Assign(
        IReadOnlyList<MemoryEntryRef> entries, IReadOnlyList<LiveWindowRef> live)
    {
        var result = new List<(IntPtr, int)>();
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
            result.Add((e.BoundHandle, e.Id));
        }

        // ---- Rule 2 + 3: identity groups over the *unbound* leftovers ----
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

            int n = Math.Min(records.Count, windows.Count);
            for (int i = 0; i < n; i++)
            {
                usedHandles.Add(windows[i].Handle);
                usedIds.Add(records[i].Id);
                result.Add((windows[i].Handle, records[i].Id));
            }
        }

        return result;
    }
}
