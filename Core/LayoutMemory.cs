namespace Reframe.Core;

/// <summary>
/// One window's remembered geometry under a given <see cref="LayoutKey"/>. Stores two rectangles, both in
/// physical pixels: the on-screen rect (from <c>GetWindowRect</c>) used for the precise, non-activating
/// <c>SetWindowPos</c> restore of a visible normal window; and the <c>WINDOWPLACEMENT.rcNormalPosition</c>
/// restore rectangle (workspace coords) used to fix windows that are minimized / hidden-to-tray / maximized
/// via <c>SetWindowPlacement</c> — a display change resets <i>their</i> restore rect, which is why a tray app
/// comes back tiny in the top-left. Because a layout is only restored under an <b>identical</b> DisplayKey
/// (same monitors ⇒ same DPI and the same workspace↔screen mapping), both rectangles round-trip exactly with
/// no remapping. A later phase enriches this with window identity (exe path / class / title).
/// </summary>
public sealed record WindowRecord(
    int Left, int Top, int Right, int Bottom,                  // GetWindowRect (screen) — visible-normal SetWindowPos path
    int NormLeft, int NormTop, int NormRight, int NormBottom,  // WINDOWPLACEMENT.rcNormalPosition (workspace) — hidden/minimized/maximized SetWindowPlacement path
    int ShowCmd);

/// <summary>
/// In-memory layout store: <c>DisplayKey → (window handle → remembered geometry)</c>. Pure data + lookup,
/// no Win32 — the <see cref="PersistenceEngine"/> owns one instance and drives it from its single worker
/// thread, so this type is not internally synchronized (single-thread ownership). Unit-tested standalone.
/// </summary>
public sealed class LayoutMemory
{
    private readonly Dictionary<string, Dictionary<IntPtr, WindowRecord>> _byKey = new();

    /// <summary>
    /// Record the current geometry of the given windows under <paramref name="displayKey"/>. Existing entries
    /// for other windows in that bucket are left intact (a window momentarily absent — minimized, or skipped
    /// because the engine owns it — keeps its remembered position for a later restore). The sentinel
    /// <see cref="LayoutKey.None"/> (no monitors) is ignored so a mid-transition empty topology can't clobber a bucket.
    /// </summary>
    public void Capture(string displayKey, IReadOnlyList<(IntPtr Handle, WindowRecord Record)> windows)
    {
        if (string.IsNullOrEmpty(displayKey) || displayKey == LayoutKey.None) return;
        if (!_byKey.TryGetValue(displayKey, out var bucket))
            _byKey[displayKey] = bucket = new Dictionary<IntPtr, WindowRecord>();
        foreach (var (h, r) in windows)
            bucket[h] = r;
    }

    /// <summary>Whether we have any remembered windows for this key (i.e. a layout worth restoring).</summary>
    public bool HasSnapshot(string displayKey)
        => _byKey.TryGetValue(displayKey, out var b) && b.Count > 0;

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
        foreach (var h in liveHandles)
            if (bucket.TryGetValue(h, out var rec))
                plan.Add((h, rec));
        return plan;
    }

    /// <summary>
    /// Every remembered (handle, record) under this key — including windows that are currently hidden to the
    /// tray or minimized (which a live-window scan would miss). The restore pass uses this and filters by
    /// liveness/ownership itself, so a tray app captured while visible still gets its restore rect fixed.
    /// </summary>
    public IReadOnlyList<(IntPtr Handle, WindowRecord Record)> GetAll(string displayKey)
    {
        var list = new List<(IntPtr, WindowRecord)>();
        if (_byKey.TryGetValue(displayKey, out var bucket))
            foreach (var kv in bucket)
                list.Add((kv.Key, kv.Value));
        return list;
    }

    /// <summary>Drop a handle from every bucket (called when a window is destroyed; handles get reused).</summary>
    public void ForgetWindow(IntPtr handle)
    {
        foreach (var bucket in _byKey.Values)
            bucket.Remove(handle);
    }

    /// <summary>
    /// Drop every remembered handle that is no longer a live window. Prevents unbounded growth and, more
    /// importantly, stops a reused handle from being restored to the previous window's position.
    /// </summary>
    public void PruneDead(Func<IntPtr, bool> isAlive)
    {
        List<string>? emptyKeys = null;
        foreach (var (key, bucket) in _byKey)
        {
            List<IntPtr>? dead = null;
            foreach (var h in bucket.Keys)
                if (!isAlive(h)) (dead ??= new List<IntPtr>()).Add(h);
            if (dead != null)
                foreach (var h in dead) bucket.Remove(h);
            if (bucket.Count == 0) (emptyKeys ??= new List<string>()).Add(key);
        }
        if (emptyKeys != null)
            foreach (var k in emptyKeys) _byKey.Remove(k);
    }
}
