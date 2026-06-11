using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>The three outcomes of Apply: no change / changed / failed (a protected window we can't touch; the caller reports "no permission" accordingly).</summary>
public enum ApplyOutcome { NoChange, Changed, Failed }

/// <summary>Acts on windows: strip border, position, snapshot and restore. Only operates when there's an actual change.</summary>
public static class WindowOps
{
    private sealed record Snapshot(long Style, long ExStyle, NativeMethods.RECT Rect);

    /// <summary>The original state before any change, used to "restore on rule-disable / engine-exit".</summary>
    private static readonly ConcurrentDictionary<IntPtr, Snapshot> _originals = new();

    /// <summary>
    /// Apply the target state. Returns three outcomes: <see cref="ApplyOutcome.Changed"/> made an actual change;
    /// <see cref="ApplyOutcome.NoChange"/> the target was already met, nothing to do;
    /// <see cref="ApplyOutcome.Failed"/> the style change was rejected (protected/UWP window) — the caller
    /// should not register a takeover and should report "no permission".
    /// </summary>
    public static ApplyOutcome Apply(IntPtr hWnd, in PlacementResolver.Target t)
    {
        // Whether this call created the window's snapshot for the first time — on failure we roll it back,
        // so an untouchable window isn't left with a stale snapshot.
        bool snapshotCreatedHere = false;
        bool changed = false;

        if (t.MakeBorderless)
        {
            snapshotCreatedHere |= EnsureSnapshot(hWnd);
            switch (StripBorder(hWnd))
            {
                case ApplyOutcome.Failed:
                    if (snapshotCreatedHere) _originals.TryRemove(hWnd, out _);
                    return ApplyOutcome.Failed;
                case ApplyOutcome.Changed:
                    changed = true;
                    break;
            }
        }

        if (t.Rect is { } r)
        {
            NativeMethods.GetWindowRect(hWnd, out var cur);
            int cw = r.Right - r.Left, ch = r.Bottom - r.Top;
            // ±2px tolerance: under DPI rounding / frame jitter, being off by a pixel or two doesn't count as
            // "different", so we avoid a pointless SetWindowPos resend and a tug-of-war with the game.
            bool same = Near(cur.Left, r.Left) && Near(cur.Top, r.Top) &&
                        Near(cur.Right - cur.Left, cw) && Near(cur.Bottom - cur.Top, ch);
            if (!same)
            {
                snapshotCreatedHere |= EnsureSnapshot(hWnd);
                if (NativeMethods.IsIconic(hWnd))
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

                // Pure move/resize, no Z-order change (topmost is handled separately below).
                NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, r.Left, r.Top, cw, ch,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                    NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED);
                changed = true;
            }
        }

        // Topmost: only when the profile requests topmost AND the window isn't already TOPMOST do we send one
        // Z-order-changing SetWindowPos (dropping SWP_NOZORDER, using HWND_TOPMOST as the anchor). A
        // non-topmost profile leaves Z-order alone: it neither forces NOTOPMOST nor changes a normal window's
        // existing level — clearing topmost happens only in Restore, per the snapshot.
        if (t.Topmost)
        {
            snapshotCreatedHere |= EnsureSnapshot(hWnd);
            if (!IsTopmost(hWnd))
            {
                NativeMethods.SetWindowPos(hWnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
                changed = true;
            }
        }

        return changed ? ApplyOutcome.Changed : ApplyOutcome.NoChange;
    }

    /// <summary>Pixel tolerance for position/size comparison (DPI rounding jitter).</summary>
    private const int PosTolerance = 2;
    private static bool Near(int a, int b) => Math.Abs(a - b) <= PosTolerance;

    private static bool IsTopmost(IntPtr hWnd)
        => ((long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE)
            & NativeMethods.WS_EX_TOPMOST) != 0;

    /// <summary>Restore a single window to how it looked before takeover. Returns false if the window has no snapshot or the style restore was rejected.</summary>
    public static bool Restore(IntPtr hWnd)
    {
        if (!_originals.TryRemove(hWnd, out var s)) return false;

        bool ok = SetWindowLongPtrChecked(hWnd, NativeMethods.GWL_STYLE, (IntPtr)s.Style);
        ok &= SetWindowLongPtrChecked(hWnd, NativeMethods.GWL_EXSTYLE, (IntPtr)s.ExStyle);

        // Restore position (no Z-order change); WS_EX_TOPMOST can't go through SetWindowLongPtr, so it's
        // restored separately below via a Z-order anchor.
        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero,
            s.Rect.Left, s.Rect.Top, s.Rect.Right - s.Rect.Left, s.Rect.Bottom - s.Rect.Top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED);

        // Restore the topmost state per the snapshot: originally topmost → still TOPMOST, originally not →
        // clear to NOTOPMOST (this is exactly "only clear topmost on windows we previously made topmost" —
        // if the snapshot wasn't topmost, this step undoes the topmost we added).
        bool wasTopmost = (s.ExStyle & NativeMethods.WS_EX_TOPMOST) != 0;
        IntPtr anchor = wasTopmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;
        NativeMethods.SetWindowPos(hWnd, anchor, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        return ok;
    }

    /// <summary>Restore every managed window (engine stop / app exit).</summary>
    public static void RestoreAll()
    {
        foreach (var hWnd in _originals.Keys)
            Restore(hWnd);
    }

    public static bool IsTracked(IntPtr hWnd) => _originals.ContainsKey(hWnd);

    /// <summary>
    /// Drop original snapshots whose handle is dead (<paramref name="isAlive"/> returns false). Handles get
    /// reused by the system — if a destroyed window's snapshot isn't cleared, a new window grabbing the same
    /// HWND would have <see cref="EnsureSnapshot"/> skip creating a snapshot (GetOrAdd hits the stale value),
    /// causing restore to write back the previous window's style/position (stale-snapshot pollution).
    /// Driven centrally by <see cref="Watcher"/>'s dead-window cleanup (passing NativeMethods.IsWindow).
    /// </summary>
    public static void ForgetDead(Func<IntPtr, bool> isAlive)
    {
        foreach (var h in _originals.Keys)
            if (!isAlive(h))
                _originals.TryRemove(h, out _);
    }

    /// <summary>Create the snapshot (if absent). Returns true if this call created a new snapshot (for failure-rollback decisions).</summary>
    private static bool EnsureSnapshot(IntPtr hWnd)
    {
        bool created = false;
        _originals.GetOrAdd(hWnd, h =>
        {
            created = true;
            long style = (long)NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_STYLE);
            long ex = (long)NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE);
            NativeMethods.GetWindowRect(h, out var rect);
            return new Snapshot(style, ex, rect);
        });
        return created;
    }

    private static ApplyOutcome StripBorder(IntPtr hWnd)
    {
        long style = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE);
        long ex = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);

        long newStyle = style & ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                                  NativeMethods.WS_BORDER | NativeMethods.WS_DLGFRAME);
        long newEx = ex & ~(NativeMethods.WS_EX_DLGMODALFRAME | NativeMethods.WS_EX_WINDOWEDGE |
                            NativeMethods.WS_EX_CLIENTEDGE | NativeMethods.WS_EX_STATICEDGE);

        if (newStyle == style && newEx == ex) return ApplyOutcome.NoChange;

        if (!SetWindowLongPtrChecked(hWnd, NativeMethods.GWL_STYLE, (IntPtr)newStyle) ||
            !SetWindowLongPtrChecked(hWnd, NativeMethods.GWL_EXSTYLE, (IntPtr)newEx))
            return ApplyOutcome.Failed;

        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_FRAMECHANGED);
        return ApplyOutcome.Changed;
    }

    /// <summary>
    /// Wraps SetWindowLongPtr with the standard "a return of 0 isn't necessarily success" check: clear the
    /// thread's last-error before the call; a non-zero return is success; on a 0 return, inspect last-error —
    /// 0 means the previous value really was 0 (success), non-zero means rejected (failure).
    /// </summary>
    private static bool SetWindowLongPtrChecked(IntPtr hWnd, int nIndex, IntPtr value)
    {
        NativeMethods.SetLastError(0);
        IntPtr prev = NativeMethods.SetWindowLongPtr(hWnd, nIndex, value);
        if (prev != IntPtr.Zero) return true;
        return Marshal.GetLastWin32Error() == 0;
    }
}
