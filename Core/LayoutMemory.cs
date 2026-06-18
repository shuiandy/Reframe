namespace Reframe.Core;

/// <summary>
/// One window's remembered geometry under a given <see cref="LayoutKey"/>. P1 stores the absolute
/// virtual-desktop rect plus the show-state: because a layout is only ever restored under an <b>identical</b>
/// DisplayKey (same monitors at the same virtual positions ⇒ same per-monitor DPI), absolute physical pixels
/// map back exactly, so no monitor-relative remapping or DPI math is needed. A later phase (cross-reboot
/// persistence) enriches this with window identity (exe path / class / title) + monitor-relative coordinates.
/// </summary>
public sealed record WindowRecord(int Left, int Top, int Right, int Bottom, int ShowCmd);

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
        foreach (var bucket in _byKey.Values)
        {
            List<IntPtr>? dead = null;
            foreach (var h in bucket.Keys)
                if (!isAlive(h)) (dead ??= new List<IntPtr>()).Add(h);
            if (dead != null)
                foreach (var h in dead) bucket.Remove(h);
        }
    }
}
