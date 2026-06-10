using System.Runtime.InteropServices;
using Reframe.Interop;

namespace Reframe.Services;

/// <summary>一块显示器的几何描述(物理像素)。</summary>
public sealed record MonitorDesc(string DeviceName, bool IsPrimary,
    int X, int Y, int Width, int Height,            // rcMonitor
    int WorkX, int WorkY, int WorkW, int WorkH);    // rcWork

/// <summary>枚举当前显示器布局。即取即用,不缓存(热插拔/分辨率会变)。</summary>
public static class MonitorService
{
    public static IReadOnlyList<MonitorDesc> GetMonitors()
    {
        var list = new List<MonitorDesc>();

        // 委托是局部变量,EnumDisplayMonitors 是同步调用,枚举期间不会被 GC。
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
                return true; // 继续枚举
            }, IntPtr.Zero);

        return list;
    }
}
