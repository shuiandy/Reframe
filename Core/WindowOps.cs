using System.Collections.Concurrent;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>对窗口动手:去边框、定位、快照与还原。仅在确有变化时操作。</summary>
public static class WindowOps
{
    private sealed record Snapshot(long Style, long ExStyle, NativeMethods.RECT Rect);

    /// <summary>改动前的原始状态,用于"禁用规则/退出引擎 → 还原"。</summary>
    private static readonly ConcurrentDictionary<IntPtr, Snapshot> _originals = new();

    /// <summary>应用目标状态。返回是否做了实际改动。</summary>
    public static bool Apply(IntPtr hWnd, in PlacementResolver.Target t)
    {
        bool changed = false;

        if (t.MakeBorderless)
        {
            EnsureSnapshot(hWnd);
            changed |= StripBorder(hWnd);
        }

        if (t.Rect is { } r)
        {
            NativeMethods.GetWindowRect(hWnd, out var cur);
            int cw = r.Right - r.Left, ch = r.Bottom - r.Top;
            bool same = cur.Left == r.Left && cur.Top == r.Top &&
                        (cur.Right - cur.Left) == cw && (cur.Bottom - cur.Top) == ch;
            if (!same)
            {
                EnsureSnapshot(hWnd);
                if (NativeMethods.IsIconic(hWnd))
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

                // 纯移动/缩放,不动 Z 序(置顶由下面单独处理)。
                NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, r.Left, r.Top, cw, ch,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                    NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED);
                changed = true;
            }
        }

        // 置顶:仅在 profile 要求置顶、且窗口当前还不是 TOPMOST 时,才发一次改 Z 序的 SetWindowPos
        // (此时去掉 SWP_NOZORDER,用 HWND_TOPMOST 作锚点)。非置顶 profile 不动 Z 序:既不强设
        // NOTOPMOST,也不改普通窗口已有层级——清除置顶只在 Restore 按快照进行。
        if (t.Topmost)
        {
            EnsureSnapshot(hWnd);
            if (!IsTopmost(hWnd))
            {
                NativeMethods.SetWindowPos(hWnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
                changed = true;
            }
        }

        return changed;
    }

    private static bool IsTopmost(IntPtr hWnd)
        => ((long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE)
            & NativeMethods.WS_EX_TOPMOST) != 0;

    /// <summary>还原单个窗口到接管前的样子。</summary>
    public static void Restore(IntPtr hWnd)
    {
        if (!_originals.TryRemove(hWnd, out var s)) return;

        NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE, (IntPtr)s.Style);
        NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE, (IntPtr)s.ExStyle);

        // 还原位置(不动 Z 序);WS_EX_TOPMOST 走不了 SetWindowLongPtr,下面用 Z 序锚点单独还原。
        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero,
            s.Rect.Left, s.Rect.Top, s.Rect.Right - s.Rect.Left, s.Rect.Bottom - s.Rect.Top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED);

        // 置顶状态按快照还原:原本置顶 → 仍 TOPMOST,原本不置顶 → 清成 NOTOPMOST
        // (这正是"只对此前被我们置顶过的窗口在还原时清除"——若快照里就没置顶,这步把我们加的置顶撤掉)。
        bool wasTopmost = (s.ExStyle & NativeMethods.WS_EX_TOPMOST) != 0;
        IntPtr anchor = wasTopmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;
        NativeMethods.SetWindowPos(hWnd, anchor, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    /// <summary>还原全部已接管窗口(引擎停止/应用退出)。</summary>
    public static void RestoreAll()
    {
        foreach (var hWnd in _originals.Keys)
            Restore(hWnd);
    }

    public static bool IsTracked(IntPtr hWnd) => _originals.ContainsKey(hWnd);

    private static void EnsureSnapshot(IntPtr hWnd)
    {
        _originals.GetOrAdd(hWnd, h =>
        {
            long style = (long)NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_STYLE);
            long ex = (long)NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE);
            NativeMethods.GetWindowRect(h, out var rect);
            return new Snapshot(style, ex, rect);
        });
    }

    private static bool StripBorder(IntPtr hWnd)
    {
        long style = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE);
        long ex = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);

        long newStyle = style & ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                                  NativeMethods.WS_BORDER | NativeMethods.WS_DLGFRAME);
        long newEx = ex & ~(NativeMethods.WS_EX_DLGMODALFRAME | NativeMethods.WS_EX_WINDOWEDGE |
                            NativeMethods.WS_EX_CLIENTEDGE | NativeMethods.WS_EX_STATICEDGE);

        if (newStyle == style && newEx == ex) return false;

        NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE, (IntPtr)newStyle);
        NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE, (IntPtr)newEx);
        NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_FRAMECHANGED);
        return true;
    }
}
