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
    public int Width { get; init; }                 // 窗口外框宽(像素);未取到为 0
    public int Height { get; init; }                // 窗口外框高(像素);未取到为 0
}

/// <summary>枚举"像应用主窗口"的顶层窗口。</summary>
public static class WindowScanner
{
    /// <summary>候选窗口最小边长:小于此(任一边)的剔除(托盘气泡、隐形小工具窗等)。</summary>
    public const int MinCandidateSize = 80;

    /// <summary>
    /// 进程黑名单(小写,不含 .exe):系统外壳 / 输入法 / 自身,从不该出现在"可建配置"列表里。
    /// 仅此纯函数部分可单测;cloaked / 尺寸等需 Win32 的过滤在 <see cref="EnumerateCandidates"/> 里。
    /// </summary>
    private static readonly HashSet<string> ProcessBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "reframe",                  // 自身
        "textinputhost",            // 输入法/触摸键盘宿主
        "shellexperiencehost",      // 操作中心 / 任务视图等外壳
        "searchhost",               // 搜索面板
        "startmenuexperiencehost",  // 开始菜单
        "lockapp",                  // 锁屏
        "widgets",                  // 小组件面板
        "systemsettings",           // 设置(沉浸式外壳,通常 cloaked,双保险)
        "applicationframehost",     // UWP 外框宿主(挂起的 UWP 常以它为顶层、又 cloaked)
        "explorer",                 // 资源管理器桌面/任务栏外壳窗(真实文件窗 class 不同,但其外壳窗会混入)
    };

    /// <summary>进程名(小写、不含 .exe)是否在系统外壳黑名单里。纯函数,单测靶点。</summary>
    public static bool IsBlacklistedProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return ProcessBlacklist.Contains(name);
    }

    /// <summary>
    /// 枚举顶层窗口(基础过滤:可见 / 有标题 / 非子窗 / 无 owner / 非 toolwindow)。
    /// 引擎匹配循环用此原始集合,不附加面向 UI 的额外剔除(cloaked / 尺寸 / 黑名单)。
    /// </summary>
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

            int w = 0, h = 0;
            if (NativeMethods.GetWindowRect(hWnd, out var r))
            {
                w = r.Right - r.Left;
                h = r.Bottom - r.Top;
            }

            result.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                ClassName = cls.ToString(),
                ProcessId = pid,
                ProcessName = procName,
                Width = w,
                Height = h,
            });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// 面向"可建配置"UI 的候选窗口:在 <see cref="EnumerateTopLevel"/> 基础上再剔除——
    /// (a) DWM cloaked(挂起 UWP / 别的虚拟桌面留下的窗);
    /// (b) 尺寸任一边 &lt; <see cref="MinCandidateSize"/>;
    /// (c) 进程名命中系统外壳黑名单(<see cref="IsBlacklistedProcess"/>)。
    /// </summary>
    public static List<WindowInfo> EnumerateCandidates()
    {
        var result = new List<WindowInfo>();
        foreach (var w in EnumerateTopLevel())
        {
            if (IsBlacklistedProcess(w.ProcessName)) continue;
            if (w.Width < MinCandidateSize || w.Height < MinCandidateSize) continue;
            if (IsCloaked(w.Handle)) continue;
            result.Add(w);
        }
        return result;
    }

    /// <summary>窗口是否被 DWM cloaked(隐藏)。取不到属性按"未隐藏"处理(不误杀)。</summary>
    private static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                    out int cloaked, sizeof(int)) == 0)
                return cloaked != 0;
        }
        catch { /* dwmapi 不可用:按未隐藏处理 */ }
        return false;
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
