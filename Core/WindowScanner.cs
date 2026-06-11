using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>A snapshot of one top-level window.</summary>
public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = ""; // Without .exe, lowercase
    public int Width { get; init; }                 // Window outer-frame width (pixels); 0 if unavailable
    public int Height { get; init; }                // Window outer-frame height (pixels); 0 if unavailable
}

/// <summary>Why a top-level window does (or doesn't) appear in the "create-a-profile" list. None = a normal candidate, it appears.</summary>
public enum FilterReason
{
    None,           // Normal candidate, listed by default
    SystemShell,    // Matches the system-shell blacklist (hard-coded, irreversible): textinputhost etc.
    UserIgnored,    // Matches the user-defined ignore list (reversible)
    Cloaked,        // DWM-hidden (suspended UWP / another virtual desktop)
    TooSmall,       // Either side < MinCandidateSize
}

/// <summary>A top-level window + its filter verdict (for the "show filtered" UI, where filtered ones are still listed as a fallback).</summary>
public sealed class ScannedWindow
{
    public WindowInfo Window { get; init; } = null!;
    public FilterReason Reason { get; init; }
    public bool IsCandidate => Reason == FilterReason.None;
}

/// <summary>Enumerates top-level windows that "look like an app's main window".</summary>
public static class WindowScanner
{
    /// <summary>Minimum side length for a candidate window: anything smaller (on either side) is dropped (tray balloons, invisible helper windows, etc.).</summary>
    public const int MinCandidateSize = 80;

    /// <summary>
    /// Process blacklist (lowercase, without .exe): the system shell / input method / ourselves, which should
    /// never appear in the "create-a-profile" list. Only this pure-function part is unit-testable; the
    /// cloaked / size filters (which need Win32) live in <see cref="EnumerateCandidates"/>.
    /// </summary>
    private static readonly HashSet<string> ProcessBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "reframe",                  // Ourselves
        "textinputhost",            // IME / touch-keyboard host
        "shellexperiencehost",      // Action Center / Task View and other shell surfaces
        "searchhost",               // Search panel
        "startmenuexperiencehost",  // Start menu
        "lockapp",                  // Lock screen
        "widgets",                  // Widgets panel
        "systemsettings",           // Settings (immersive shell, usually cloaked; belt and suspenders)
        "applicationframehost",     // UWP frame host (a suspended UWP often surfaces as this, and is cloaked)
        "explorer",                 // Explorer desktop/taskbar shell windows (real file windows have a different class, but its shell windows leak in)
    };

    /// <summary>Whether the process name (lowercase, without .exe) is in the system-shell blacklist. Pure function, unit-test target.</summary>
    public static bool IsBlacklistedProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return ProcessBlacklist.Contains(name);
    }

    /// <summary>
    /// Enumerate top-level windows (basic filtering: visible / has a title / not a child / no owner / not a
    /// tool window). The engine's match loop uses this raw set, without the extra UI-facing exclusions
    /// (cloaked / size / blacklist).
    /// </summary>
    public static List<WindowInfo> EnumerateTopLevel()
    {
        var result = new List<WindowInfo>();
        // pid→name dedupe within this enumeration: resolve the process name once for a process's multiple top-level windows.
        var perScan = new Dictionary<uint, string>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;

            // Skip windows with no title / tool windows / child windows / owned popups.
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
    /// Pure filter verdict (unit-test target): given process name / size / cloaked / user-ignore list, assign
    /// one <see cref="FilterReason"/>. Priority: system blacklist (irreversible) &gt; user-ignore (reversible)
    /// &gt; cloaked &gt; too small. The first match wins. User-ignore list: process names (lowercase, without
    /// .exe) compared one by one, tolerating .exe and case differences on either side.
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

    /// <summary>Whether the process name (lowercase, without .exe) is in the user-ignore list. Pure function, unit-test target. Empty name / empty list → false.</summary>
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
    /// Candidate windows for the "create-a-profile" UI: on top of <see cref="EnumerateTopLevel"/>, further
    /// drop —
    /// (a) process names matching the system-shell blacklist (<see cref="IsBlacklistedProcess"/>);
    /// (b) matches against the user-ignore list (<paramref name="userIgnores"/>);
    /// (c) DWM-cloaked windows (suspended UWP / leftovers from another virtual desktop);
    /// (d) either side &lt; <see cref="MinCandidateSize"/>.
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
    /// Enumerate all top-level windows and attach a <see cref="FilterReason"/> to each (filtered ones are
    /// returned too, for the "show filtered" UI fallback). The cloaked / size parts (which need Win32) are
    /// probed here in place, then handed to the pure function <see cref="Classify"/> to assign the reason.
    /// </summary>
    public static List<ScannedWindow> EnumerateAllWithReason(IEnumerable<string>? userIgnores = null)
    {
        // Materialize once, to avoid re-enumerating the IEnumerable (checked for every window).
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

    /// <summary>Whether the window is DWM-cloaked (hidden). If the attribute can't be read, treat it as "not hidden" (no false positives).</summary>
    private static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                    out int cloaked, sizeof(int)) == 0)
                return cloaked != 0;
        }
        catch { /* dwmapi unavailable: treat as not hidden */ }
        return false;
    }

    // ---- Process-name resolution cache ----
    // A short-TTL pid→name cache across ticks: dozens of GetProcessById per tick is expensive, and a game's
    // pid barely changes while running. Pids get reused, so a short TTL (10s) is the safety net: after reuse,
    // the stale name is used for at most 10s and the next refresh corrects it naturally.
    private static readonly TimeSpan ProcNameTtl = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<uint, (string Name, DateTime At)> _procNameCache = new();

    /// <summary>
    /// Resolve the process name (lowercase, without .exe). Three tiers: this enumeration's dedupe
    /// (<paramref name="perScan"/>) → the cross-tick TTL cache → an actual lookup via
    /// <see cref="SafeProcessName"/>. pid==0 (unavailable) returns an empty string and isn't cached.
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
            // Opportunistically prune expired entries, so the dictionary doesn't grow unbounded with dead pids over long uptime.
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
            return p.ProcessName.ToLowerInvariant(); // ProcessName excludes .exe
        }
        catch { return ""; }
    }
}
