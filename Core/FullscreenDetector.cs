using System.Runtime.InteropServices;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>
/// How a window currently occupies the screen, from the engine's point of view.
/// <list type="bullet">
/// <item><see cref="Normal"/> — a regular windowed/bordered app (or a small window): takeover proceeds as usual.</item>
/// <item><see cref="BorderlessFullscreen"/> — the window's outer frame covers (≈) its whole monitor but it is
/// NOT an exclusive-D3D app. This is the "borderless windowed" / fake-fullscreen mode games offer: there IS a
/// real top-level window, so Reframe can strip its border and snap it to the rule target ("force-borderless").</item>
/// <item><see cref="Exclusive"/> — an exclusive (flip-mode) D3D full-screen app owns the display. There is no
/// manipulable bordered window; SetWindowPos would only fight the game. Reframe must NOT take it over and
/// instead prompts the user to switch the game to windowed/borderless.</item>
/// </list>
/// </summary>
public enum FullscreenKind
{
    Normal,
    BorderlessFullscreen,
    Exclusive,
}

/// <summary>
/// Classifies how a matched window occupies the screen so the watcher can branch:
/// exclusive D3D full-screen (untouchable → prompt) vs. borderless-fullscreen (force-borderless takeover) vs.
/// normal. The coverage test (<see cref="IsCoveringMonitor"/>) is a pure function (unit-testable); the
/// exclusive-D3D and foreground probes are Win32 and live in <see cref="Classify"/>.
/// </summary>
public static class FullscreenDetector
{
    /// <summary>
    /// Default coverage tolerance (pixels per edge): a borderless-fullscreen window is allowed to fall a few
    /// pixels short of (or spill past) the monitor rect and still count as "covering". Generous enough to
    /// absorb DPI rounding and a 1px game inset, tight enough that a clearly-smaller window (a snapped half,
    /// a normal window) does not qualify.
    /// </summary>
    public const int CoverageTolerance = 4;

    /// <summary>
    /// Pure geometry test (unit-test target): does <paramref name="winRect"/> cover <paramref name="monRect"/>
    /// to within <paramref name="tol"/> pixels on every edge? "Cover" means the window reaches each monitor
    /// edge: its left/top are at-or-before the monitor's (within tol on the inside), and its right/bottom are
    /// at-or-after the monitor's (within tol on the inside). A window spilling beyond the monitor still counts.
    /// Works in virtual-desktop coordinates, so a secondary monitor at a negative origin is handled naturally
    /// (only differences are compared, never absolute signs).
    /// </summary>
    public static bool IsCoveringMonitor(NativeMethods.RECT winRect, NativeMethods.RECT monRect, int tol = CoverageTolerance)
    {
        // Each edge must reach the monitor edge. A window edge inside the monitor by more than `tol` fails;
        // a window edge outside the monitor (overscan) always passes. So:
        //   left  must be <= monLeft + tol   (not inset from the left by more than tol)
        //   top   must be <= monTop  + tol
        //   right must be >= monRight - tol  (not inset from the right by more than tol)
        //   bottom must be >= monBottom - tol
        return winRect.Left <= monRect.Left + tol
            && winRect.Top <= monRect.Top + tol
            && winRect.Right >= monRect.Right - tol
            && winRect.Bottom >= monRect.Bottom - tol;
    }

    /// <summary>
    /// Classify a live window (Win32). Order:
    /// <list type="number">
    /// <item>If an exclusive D3D full-screen app owns the display (<c>SHQueryUserNotificationState ==
    /// QUNS_RUNNING_D3D_FULL_SCREEN</c>) AND this window is the foreground one (or shares the foreground
    /// window's pid) → <see cref="FullscreenKind.Exclusive"/>. The pid fallback covers games whose visible
    /// HWND differs from the one we matched but belongs to the same process.</item>
    /// <item>Else if the window's outer frame covers its monitor (MonitorFromWindow + GetMonitorInfo +
    /// <see cref="IsCoveringMonitor"/>) → <see cref="FullscreenKind.BorderlessFullscreen"/>.</item>
    /// <item>Else <see cref="FullscreenKind.Normal"/>.</item>
    /// </list>
    /// If any probe fails (dead handle, query error), it degrades toward <see cref="FullscreenKind.Normal"/>
    /// so the engine falls back to its normal takeover path rather than wrongly skipping a window.
    /// </summary>
    public static FullscreenKind Classify(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
            return FullscreenKind.Normal;

        if (IsExclusiveForeground(hWnd))
            return FullscreenKind.Exclusive;

        if (CoversOwnMonitor(hWnd))
            return FullscreenKind.BorderlessFullscreen;

        return FullscreenKind.Normal;
    }

    /// <summary>
    /// Whether an exclusive D3D full-screen app currently owns the display AND <paramref name="hWnd"/> is the
    /// app in front (same HWND, or same pid as the foreground window). The shell query is process-wide, so the
    /// foreground confirmation is what ties the verdict to this specific window.
    /// </summary>
    private static bool IsExclusiveForeground(IntPtr hWnd)
    {
        // SHQueryUserNotificationState returns S_OK (0) on success; any failure → not exclusive (be lenient).
        if (NativeMethods.SHQueryUserNotificationState(out int state) != 0)
            return false;
        if (state != NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN)
            return false;

        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return false;
        if (fg == hWnd)
            return true;

        // Same process as the foreground window: a game's visible swap-chain HWND may differ from the matched
        // top-level window while belonging to the same pid.
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
        return pid != 0 && pid == fgPid;
    }

    /// <summary>Whether the window's outer frame covers its current monitor (within <see cref="CoverageTolerance"/>).</summary>
    private static bool CoversOwnMonitor(IntPtr hWnd)
    {
        if (!NativeMethods.GetWindowRect(hWnd, out var winRect))
            return false;

        IntPtr hMon = NativeMethods.MonitorFromWindow(hWnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi))
            return false;

        return IsCoveringMonitor(winRect, mi.rcMonitor);
    }
}
