using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>Apply 的结果三态:无改动 / 已改动 / 失败(受保护窗口动不了,调用方据此报"无权限")。</summary>
public enum ApplyOutcome { NoChange, Changed, Failed }

/// <summary>对窗口动手:去边框、定位、快照与还原。仅在确有变化时操作。</summary>
public static class WindowOps
{
    private sealed record Snapshot(long Style, long ExStyle, NativeMethods.RECT Rect);

    /// <summary>改动前的原始状态,用于"禁用规则/退出引擎 → 还原"。</summary>
    private static readonly ConcurrentDictionary<IntPtr, Snapshot> _originals = new();

    /// <summary>
    /// 应用目标状态。返回三态:<see cref="ApplyOutcome.Changed"/> 做了实际改动;
    /// <see cref="ApplyOutcome.NoChange"/> 目标已满足、无需动手;
    /// <see cref="ApplyOutcome.Failed"/> 改样式被拒(受保护/UWP 窗口),调用方不应登记接管、应报"无权限"。
    /// </summary>
    public static ApplyOutcome Apply(IntPtr hWnd, in PlacementResolver.Target t)
    {
        // 本次调用是否由我们首次为该窗口建快照——失败时要回滚,避免给动不了的窗口留下脏快照。
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
            // ±2px 容差:DPI 取整/边框抖动下,差一两像素不算"不同",免得无谓重发 SetWindowPos 与游戏拉锯。
            bool same = Near(cur.Left, r.Left) && Near(cur.Top, r.Top) &&
                        Near(cur.Right - cur.Left, cw) && Near(cur.Bottom - cur.Top, ch);
            if (!same)
            {
                snapshotCreatedHere |= EnsureSnapshot(hWnd);
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

    /// <summary>位置/尺寸比较的像素容差(DPI 取整抖动)。</summary>
    private const int PosTolerance = 2;
    private static bool Near(int a, int b) => Math.Abs(a - b) <= PosTolerance;

    private static bool IsTopmost(IntPtr hWnd)
        => ((long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE)
            & NativeMethods.WS_EX_TOPMOST) != 0;

    /// <summary>还原单个窗口到接管前的样子。返回 false 表示该窗口无快照或还原样式被拒。</summary>
    public static bool Restore(IntPtr hWnd)
    {
        if (!_originals.TryRemove(hWnd, out var s)) return false;

        bool ok = SetWindowLongPtrChecked(hWnd, NativeMethods.GWL_STYLE, (IntPtr)s.Style);
        ok &= SetWindowLongPtrChecked(hWnd, NativeMethods.GWL_EXSTYLE, (IntPtr)s.ExStyle);

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
        return ok;
    }

    /// <summary>还原全部已接管窗口(引擎停止/应用退出)。</summary>
    public static void RestoreAll()
    {
        foreach (var hWnd in _originals.Keys)
            Restore(hWnd);
    }

    public static bool IsTracked(IntPtr hWnd) => _originals.ContainsKey(hWnd);

    /// <summary>
    /// 丢弃句柄已失效(<paramref name="isAlive"/> 判否)的原始快照。句柄会被系统复用——
    /// 旧窗口销毁后其快照若不清,新窗口拿到同一 HWND 时 <see cref="EnsureSnapshot"/> 会跳过建快照
    /// (GetOrAdd 命中旧值),导致还原写回上一个窗口的样式/位置(脏快照污染)。
    /// 由 <see cref="Watcher"/> 的死窗口清理统一驱动(传 NativeMethods.IsWindow)。
    /// </summary>
    public static void ForgetDead(Func<IntPtr, bool> isAlive)
    {
        foreach (var h in _originals.Keys)
            if (!isAlive(h))
                _originals.TryRemove(h, out _);
    }

    /// <summary>建快照(若尚无)。返回 true 表示本次调用新建了快照(供失败回滚判断)。</summary>
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
    /// SetWindowLongPtr 包一层"返回 0 不必然成功"的标准判定:调用前清线程 last-error,
    /// 返回非 0 即成功;返回 0 时再看 last-error——0 表示原值本就是 0(成功),非 0 表示被拒(失败)。
    /// </summary>
    private static bool SetWindowLongPtrChecked(IntPtr hWnd, int nIndex, IntPtr value)
    {
        NativeMethods.SetLastError(0);
        IntPtr prev = NativeMethods.SetWindowLongPtr(hWnd, nIndex, value);
        if (prev != IntPtr.Zero) return true;
        return Marshal.GetLastWin32Error() == 0;
    }
}
