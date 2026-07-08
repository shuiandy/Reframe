using System.Runtime.InteropServices;

namespace Reframe.Services;

/// <summary>
/// Forensic context probe for exit/engine-toggle markers. When the engine is disabled or the app
/// exits, the crash.log marker alone can't tell a genuine user click apart from a synthetic message
/// injected by another process (e.g. an external <c>PostMessage(WM_COMMAND)</c> to the tray window).
/// This attaches two cheap ambient signals to every such marker:
/// <list type="bullet">
///   <item><b>idleMs</b> — milliseconds since the last real hardware input (mouse/keyboard),
///   from <c>GetLastInputInfo</c>. A large value at the moment of an "exit"/"toggle" is a strong
///   tell that no human was at the keyboard — i.e. the command was synthetic.</item>
///   <item><b>fg</b> — the foreground window's process name. Identifies who had focus when the
///   action fired.</item>
/// </list>
/// <para>Everything is wrapped so a probe failure can never throw into the caller (this runs on the
/// exit path). The idle computation is factored into the pure <see cref="IdleMsFrom"/> so the
/// tick-count wraparound is unit-testable without any Win32 dependency.</para>
/// </summary>
public static class ForensicProbe
{
    /// <summary>
    /// Milliseconds elapsed between <paramref name="lastInputTick"/> and <paramref name="nowTick"/>,
    /// both being <c>GetTickCount</c>-style unsigned millisecond counters. Uses an <c>unchecked</c>
    /// unsigned subtraction so a counter that has wrapped past <c>uint.MaxValue</c> (~49.7 days of
    /// uptime) still yields the correct small positive delta rather than a huge bogus number. Pure —
    /// no Win32, no exceptions.
    /// </summary>
    public static uint IdleMsFrom(uint nowTick, uint lastInputTick)
        => unchecked(nowTick - lastInputTick);

    /// <summary>
    /// Build a one-line forensic context string, e.g. <c>idleMs=123456 fg=comet</c>. Never throws;
    /// any component that can't be resolved degrades to <c>?</c>.
    /// </summary>
    public static string ForensicContext()
    {
        string idle = "?";
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii))
                idle = IdleMsFrom(unchecked((uint)Environment.TickCount), lii.dwTime).ToString();
        }
        catch { /* leave idle="?" */ }

        string fg = "?";
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h != IntPtr.Zero)
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != 0)
                {
                    try
                    {
                        using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                        fg = p.ProcessName;
                    }
                    catch { /* process gone / no access: leave fg="?" */ }
                }
            }
        }
        catch { /* leave fg="?" */ }

        return $"idleMs={idle} fg={fg}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;   // GetTickCount at the moment of the last input event
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
