using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>一个顶层窗口的快照。</summary>
public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = ""; // 不含 .exe,小写
    public int Width { get; init; }                 // 窗口外框宽(像素);未取到为 0
    public int Height { get; init; }                // 窗口外框高(像素);未取到为 0
}

/// <summary>一个顶层窗口为什么会(不会)出现在"可建配置"列表里。None = 正常候选,会出现。</summary>
public enum FilterReason
{
    None,           // 正常候选,默认列出
    SystemShell,    // 命中系统外壳黑名单(写死、不可逆):textinputhost 等
    UserIgnored,    // 用户自定义忽略名单命中(可逆)
    Cloaked,        // DWM 隐藏(挂起 UWP / 别的虚拟桌面)
    TooSmall,       // 任一边 < MinCandidateSize
}

/// <summary>顶层窗口 + 其过滤判定(供"显示已过滤"的 UI 用,被滤的也能列出兜底)。</summary>
public sealed class ScannedWindow
{
    public WindowInfo Window { get; init; } = null!;
    public FilterReason Reason { get; init; }
    public bool IsCandidate => Reason == FilterReason.None;
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
        // 本次枚举内 pid→name 去重:同一进程的多个顶层窗口只解析一次进程名。
        var perScan = new Dictionary<uint, string>();

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

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string procName = ResolveProcessName(pid, perScan);

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
    /// 纯过滤判定(单测靶点):给定进程名/尺寸/是否 cloaked/用户忽略名单,定一个 <see cref="FilterReason"/>。
    /// 优先级:系统黑名单(不可逆) &gt; 用户忽略(可逆) &gt; cloaked &gt; 过小。命中靠前者即返回。
    /// 用户忽略名单:进程名(小写、不含 .exe)逐项比较,自身带 .exe / 大小写差异都容忍。
    /// </summary>
    public static FilterReason Classify(
        string? processName, int width, int height, bool isCloaked,
        IEnumerable<string>? userIgnores = null)
    {
        if (IsBlacklistedProcess(processName)) return FilterReason.SystemShell;
        if (IsUserIgnored(processName, userIgnores)) return FilterReason.UserIgnored;
        if (isCloaked) return FilterReason.Cloaked;
        if (width < MinCandidateSize || height < MinCandidateSize) return FilterReason.TooSmall;
        return FilterReason.None;
    }

    /// <summary>进程名(小写、不含 .exe)是否在用户忽略名单里。纯函数,单测靶点。空名/空单 → false。</summary>
    public static bool IsUserIgnored(string? processName, IEnumerable<string>? userIgnores)
    {
        if (userIgnores is null || string.IsNullOrWhiteSpace(processName)) return false;
        string name = StripExe(processName.Trim());
        foreach (var ig in userIgnores)
        {
            if (string.IsNullOrWhiteSpace(ig)) continue;
            if (string.Equals(StripExe(ig.Trim()), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string StripExe(string s)
        => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;

    /// <summary>
    /// 面向"可建配置"UI 的候选窗口:在 <see cref="EnumerateTopLevel"/> 基础上再剔除——
    /// (a) 进程名命中系统外壳黑名单(<see cref="IsBlacklistedProcess"/>);
    /// (b) 命中用户忽略名单(<paramref name="userIgnores"/>);
    /// (c) DWM cloaked(挂起 UWP / 别的虚拟桌面留下的窗);
    /// (d) 尺寸任一边 &lt; <see cref="MinCandidateSize"/>。
    /// </summary>
    public static List<WindowInfo> EnumerateCandidates(IEnumerable<string>? userIgnores = null)
    {
        var result = new List<WindowInfo>();
        foreach (var s in EnumerateAllWithReason(userIgnores))
            if (s.IsCandidate)
                result.Add(s.Window);
        return result;
    }

    /// <summary>
    /// 枚举全部顶层窗口并对每个附上 <see cref="FilterReason"/>(被滤的也返回,供"显示已过滤"UI 兜底)。
    /// cloaked / 尺寸等需 Win32 的部分在此就地探测,再交给纯函数 <see cref="Classify"/> 定原因。
    /// </summary>
    public static List<ScannedWindow> EnumerateAllWithReason(IEnumerable<string>? userIgnores = null)
    {
        // 物化一次,避免对 IEnumerable 反复枚举(每个窗口都要查)。
        var ignores = userIgnores as ICollection<string> ?? userIgnores?.ToList();
        var result = new List<ScannedWindow>();
        foreach (var w in EnumerateTopLevel())
        {
            bool cloaked = IsCloaked(w.Handle);
            var reason = Classify(w.ProcessName, w.Width, w.Height, cloaked, ignores);
            result.Add(new ScannedWindow { Window = w, Reason = reason });
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

    // ---- 进程名解析缓存 ----
    // pid→name 的跨 tick 短 TTL 缓存:每 tick 数十次 GetProcessById 很贵,游戏运行期 pid 基本不变。
    // pid 会被系统复用,故设短 TTL(10s)兜底:复用后最多沿用 10s 旧名,下次刷新自然纠正。
    private static readonly TimeSpan ProcNameTtl = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<uint, (string Name, DateTime At)> _procNameCache = new();

    /// <summary>
    /// 解析进程名(小写、不含 .exe)。三级:本次枚举去重(<paramref name="perScan"/>)→ 跨 tick TTL 缓存 →
    /// 实查 <see cref="SafeProcessName"/>。pid==0(取不到)直接空串,不入缓存。
    /// </summary>
    private static string ResolveProcessName(uint pid, Dictionary<uint, string> perScan)
    {
        if (pid == 0) return "";
        if (perScan.TryGetValue(pid, out var hit)) return hit;

        var now = DateTime.UtcNow;
        string name;
        if (_procNameCache.TryGetValue(pid, out var c) && now - c.At < ProcNameTtl)
        {
            name = c.Name;
        }
        else
        {
            name = SafeProcessName(pid);
            _procNameCache[pid] = (name, now);
            // 顺手清理过期项,防长时间运行后字典随死 pid 无限增长。
            if (_procNameCache.Count > 256)
                foreach (var kv in _procNameCache)
                    if (now - kv.Value.At >= ProcNameTtl)
                        _procNameCache.TryRemove(kv.Key, out _);
        }

        perScan[pid] = name;
        return name;
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
