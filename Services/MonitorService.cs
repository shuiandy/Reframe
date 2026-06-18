using System.Runtime.InteropServices;
using Reframe.Core;
using Reframe.Interop;

namespace Reframe.Services;

/// <summary>Enumerate the current monitor layout. Fetch-on-use, not cached (hot-plug / resolution can change).
/// Returns <see cref="MonitorDesc"/> (defined in Core so pure-logic code can consume it without a Services dependency).</summary>
public static class MonitorService
{
    public static IReadOnlyList<MonitorDesc> GetMonitors()
    {
        var list = new List<MonitorDesc>();

        // The delegate is a local; EnumDisplayMonitors is a synchronous call, so it won't be GC'd during enumeration.
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rc, IntPtr data) =>
            {
                var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (NativeMethods.GetMonitorInfo(hMon, ref mi))
                {
                    var m = mi.rcMonitor;
                    var w = mi.rcWork;
                    list.Add(new MonitorDesc(
                        mi.szDevice,
                        (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                        m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top,
                        w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top));
                }
                return true; // continue enumerating
            }, IntPtr.Zero);

        return list;
    }
}
