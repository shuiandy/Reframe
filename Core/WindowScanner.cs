using System.Diagnostics;
using System.Text;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>一个顶层窗口的快照。</summary>
public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = ""; // 不含 .exe,小写
}

/// <summary>枚举"像应用主窗口"的顶层窗口。</summary>
public static class WindowScanner
{
    public static List<WindowInfo> EnumerateTopLevel()
    {
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;

            // 跳过无标题 / 工具窗口 / 子窗口 / 有 owner 的弹窗
            long ex = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            long style = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE);
            if ((style & NativeMethods.WS_CHILD) != 0) return true;
            if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;

            int len = NativeMethods.GetWindowTextLength(hWnd);
            if (len <= 0) return true;

            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            var cls = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, cls, cls.Capacity);

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string procName = SafeProcessName(pid);

            result.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                ClassName = cls.ToString(),
                ProcessId = pid,
                ProcessName = procName
            });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string SafeProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant(); // ProcessName 不含 .exe
        }
        catch { return ""; }
    }
}
