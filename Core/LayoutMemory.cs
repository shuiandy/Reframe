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
    /// </summary>
    public IntPtr Handle { get; set; }
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
    /// because the engine owns it — keeps its remembered position for a later restore). The sentinel
    /// <see cref="LayoutKey.None"/> (no monitors) is ignored so a mid-transition empty topology can't clobber a bucket.
    ///
    /// <para>Capture doubles as a re-claim pass: the observed windows are run through
    /// <see cref="WindowMatcher"/>, so a record left unbound by a restart (or loaded from disk) is adopted by
    /// the new window of the same identity instead of a duplicate entry piling up next to it.</para>
    ///
    /// <para><b>Adoption ⇒ restore, never overwrite</b> (<paramref name="adoptGeometry"/> = true, the periodic
    /// path). When a record that was <i>unbound at the start of this call</i> gets claimed <b>confidently</b>,
    /// the right meaning is "move that window to the remembered place", not "the remembered place is now
    /// wherever the window happens to be". Overwriting is what made the whole feature invisible: Chrome
    /// restarts in the squashed position, the next 2 s capture claims its record and stamps the bad geometry
    /// over the good one, and nothing ever restores it — and after a reboot the entire disk layout would be
    /// destroyed within one capture tick of startup. So such a record keeps its remembered
    /// <see cref="RememberedWindow.Record"/> (only handle / title / timestamp are refreshed) and is
    /// <b>returned to the caller</b> as a pending adoption restore. Records that were already bound keep the
    /// old behaviour: their geometry tracks the window, which is what makes this the live layout for this
    /// display.</para>
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
    /// <para>The pending list is produced exactly once per binding — by the next round the record is bound and
    /// takes the normal tracking path, so a window that springs back is not fought over round after round.</para>
    /// </summary>
    /// <param name="adoptGeometry">
    /// true (periodic capture): records claimed by a <i>confident</i> pairing keep their remembered geometry
    /// and are reported for an adoption restore. false (the user's explicit "Capture now"): current geometry
    /// wins for every window and nothing is reported — that gesture means "remember where things are right now".
    /// </param>
    /// <returns>
    /// Windows that just claimed a remembered record <b>and</b> are confidently the same window that record
    /// came from, so they should be moved to it. Empty in most rounds — and deliberately empty whenever the
    /// pairing was a guess.
    /// </returns>
    public IReadOnlyList<(IntPtr Handle, WindowRecord Target)> Capture(
        string displayKey, IReadOnlyList<CapturedWindow> windows, bool adoptGeometry = true)
        => Capture(displayKey, windows, DateTime.UtcNow, adoptGeometry);

    /// <summary>Capture with an explicit clock (unit tests). See the overload above for the semantics.</summary>
    public IReadOnlyList<(IntPtr Handle, WindowRecord Target)> Capture(
        string displayKey, IReadOnlyList<CapturedWindow> windows, DateTime nowUtc, bool adoptGeometry = true)
    {
        if (string.IsNullOrEmpty(displayKey) || displayKey == LayoutKey.None) return Array.Empty<(IntPtr, WindowRecord)>();
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
        var assigned = BindHandles(bucket, live);

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

                // Adoption needs all three: the periodic path, a record that was unbound coming into this
                // call, and a pairing the matcher is *sure* about. Drop any one of them and this is an
                // ordinary capture — the window stays put and the record learns its current shape.
                if (adoptGeometry && idx < wasUnbound.Length && wasUnbound[idx] && hit.Confident)
                    (adoptions ??= new List<(IntPtr, WindowRecord)>()).Add((w.Handle, e.Record)); // keep e.Record: the window moves, not the memory
                else
                    e.Record = w.Record;

                e.Title = w.Title;
                e.LastSeenUtc = nowUtc;
                e.Handle = w.Handle;
            }
            else
            {
                bucket.Add(new RememberedWindow
                {
                    Identity = w.Identity,
                    Ordinal = NextOrdinal(bucket, w.Identity, null),
                    Title = w.Title,
                    Record = w.Record,
                    LastSeenUtc = nowUtc,
                    Handle = w.Handle,
                });
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
    /// <see cref="BindHandles"/>). The confidence distinction only gates the <i>adoption</i> restore that
    /// <see cref="Capture"/> reports; this method serves the explicit restore paths, where the caller has
    /// already decided that re-applying the remembered layout is the intent.</para>
    /// </summary>
    public int Reclaim(string displayKey, IReadOnlyList<LiveWindowRef> candidates)
    {
        if (!_byKey.TryGetValue(displayKey, out var bucket) || bucket.Count == 0) return 0;
        int before = 0;
        foreach (var e in bucket) if (e.Handle != IntPtr.Zero) before++;
        BindHandles(bucket, candidates);
        int after = 0;
        foreach (var e in bucket) if (e.Handle != IntPtr.Zero) after++;
        return after - before;
    }

    /// <summary>
    /// Run the pure matcher over a bucket and apply the resulting bindings. Returns handle → (bucket index,
    /// whether the matcher was confident it is the same window). <b>Every</b> assignment is bound — binding is
    /// how records stop piling up — but only the confident ones may be acted on by moving a window.
    /// </summary>
    private static Dictionary<IntPtr, (int Index, bool Confident)> BindHandles(
        List<RememberedWindow> bucket, IReadOnlyList<LiveWindowRef> candidates)
    {
        var refs = new List<MemoryEntryRef>(bucket.Count);
        for (int i = 0; i < bucket.Count; i++)
        {
            var e = bucket[i];
            refs.Add(new MemoryEntryRef(i, e.Identity, e.Ordinal, e.Handle, e.Title));
        }

        var map = new Dictionary<IntPtr, (int, bool)>(candidates.Count);
        foreach (var a in WindowMatcher.Assign(refs, candidates))
        {
            bucket[a.Id].Handle = a.Handle;
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
    /// remembered record for, paired with its target geometry. Windows with no record are omitted (we never
    /// invent a position); remembered windows that are no longer alive are simply not in the live set.
    /// </summary>
    public IReadOnlyList<(IntPtr Handle, WindowRecord Target)> GetRestorePlan(
        string displayKey, IEnumerable<IntPtr> liveHandles)
    {
        var plan = new List<(IntPtr, WindowRecord)>();
        if (!_byKey.TryGetValue(displayKey, out var bucket)) return plan;

        var byHandle = new Dictionary<IntPtr, RememberedWindow>(bucket.Count);
        foreach (var e in bucket)
            if (e.Handle != IntPtr.Zero) byHandle[e.Handle] = e;

        foreach (var h in liveHandles)
            if (byHandle.TryGetValue(h, out var e))
                plan.Add((h, e.Record));
        return plan;
    }

    /// <summary>
    /// Every currently-bound (handle, record) under this key — including windows that are hidden to the tray or
    /// minimized (which a live-window scan would miss), which is why the restore pass uses this rather than a
    /// fresh scan and filters by liveness/ownership itself. Unbound records (no window to act on right now) are
    /// omitted; they are picked up by <see cref="Reclaim"/> once their app is back.
    /// </summary>
    public IReadOnlyList<(IntPtr Handle, WindowRecord Record)> GetAll(string displayKey)
    {
        var list = new List<(IntPtr, WindowRecord)>();
        if (_byKey.TryGetValue(displayKey, out var bucket))
            foreach (var e in bucket)
                if (e.Handle != IntPtr.Zero)
                    list.Add((e.Handle, e.Record));
        return list;
    }

    // ---- Lifecycle ----

    /// <summary>
    /// Detach a handle from every bucket (called when a window is destroyed; handles get reused). The record
    /// itself is kept — only the session-scoped binding goes away, so the geometry is still there when the app
    /// comes back.
    /// </summary>
    public void ForgetWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        foreach (var bucket in _byKey.Values)
            foreach (var e in bucket)
                if (e.Handle == handle) e.Handle = IntPtr.Zero;
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
    /// non-zero binding means that window is alive.</para>
    /// </summary>
    public void PruneDead(Func<IntPtr, bool> isAlive)
    {
        foreach (var bucket in _byKey.Values)
            foreach (var e in bucket)
                if (e.Handle != IntPtr.Zero && !isAlive(e.Handle))
                    e.Handle = IntPtr.Zero;
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
    /// </summary>
    public void ImportFromDisk(LayoutFile? file)
    {
        _byKey.Clear();
        if (file?.Layouts == null) return;

        foreach (var layout in file.Layouts)
        {
            string key = layout.DisplayKey ?? "";
            if (string.IsNullOrEmpty(key) || key == LayoutKey.None) continue;
            if (layout.Windows == null || layout.Windows.Count == 0) continue;

            if (!_byKey.TryGetValue(key, out var bucket))
                _byKey[key] = bucket = new List<RememberedWindow>();

            foreach (var w in layout.Windows)
            {
                if (w == null) continue;
                bucket.Add(new RememberedWindow
                {
                    Identity = WindowIdentity.Create(w.Process, w.Class),
                    Ordinal = w.Ordinal,
                    Title = w.Title ?? "",
                    Record = new WindowRecord(w.Left, w.Top, w.Right, w.Bottom,
                                              w.NormLeft, w.NormTop, w.NormRight, w.NormBottom, w.ShowCmd),
                    LastSeenUtc = w.LastSeenUtc,
                    Handle = IntPtr.Zero, // last session's handle is meaningless — identity-only from here
                });
            }

            if (bucket.Count == 0) _byKey.Remove(key);
        }
    }
}
