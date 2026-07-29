using System.Runtime.InteropServices;
using Reframe.Core;
using Reframe.Interop;

namespace Reframe.Services;

/// <summary>Enumerate the current monitor layout. Fetch-on-use, not cached (hot-plug / resolution / <b>scaling</b> can change).
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
                        w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top,
                        EffectiveDpi(hMon)));
                }
                return true; // continue enumerating
            }, IntPtr.Zero);

        return list;
    }

    /// <summary>
    /// The monitor's effective DPI (96 = 100%, 144 = 150%, 168 = 175%) — the scale factor the user picked in
    /// Settings. Part of the DisplayKey, because changing the scaling changes how windows should be laid out
    /// even though the pixel resolution is untouched (see <see cref="LayoutKey"/>).
    ///
    /// <para><b>Never throws and never fails the enumeration.</b> shcore.dll is present on every supported
    /// Windows, but a missing library / missing entry point / non-S_OK HRESULT / zero reading all degrade to
    /// 96. Degrading is safe: every monitor then reports the same constant, so the key simply carries no DPI
    /// information and behaves exactly like it did before this field existed.</para>
    /// </summary>
    private static int EffectiveDpi(IntPtr hMonitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
                && dpiX > 0)
                return (int)dpiX;
        }
        catch { /* DllNotFound / EntryPointNotFound / anything else: fall through to the default */ }
        return NativeMethods.USER_DEFAULT_SCREEN_DPI;
    }
}
