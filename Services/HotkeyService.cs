using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Reframe.Core;
using Reframe.Interop;

namespace Reframe.Services;

/// <summary>
/// Central global-hotkey manager: owns a background thread plus a message-only window that receives
/// WM_HOTKEY (reusing <see cref="TrayIcon"/>'s RegisterClassEx + HWND_MESSAGE + GetMessage pump
/// approach). All RegisterHotKey/UnregisterHotKey calls happen on that thread (the OS requires
/// registration and unregistration to share a thread).
///
/// <para>Action table (Id → default gesture → executor):</para>
/// <list type="bullet">
/// <item><b>ToggleBorderless</b> = Ctrl+Alt+B: foreground window IsTracked ? Restore : Apply to strip the border (moved over from TrayIcon).</item>
/// <item><b>SendToZone1/2/3</b> = Ctrl+Alt+1/2/3: foreground window → the rectangle of zone N in Layouts[0],
///       within the work area of "the monitor the window is currently on" (via
///       <see cref="PlacementResolver.ZoneToRect"/>, the same source as DragSnap/ResolveRect) → a plain SetWindowPos move.</item>
/// </list>
///
/// <para>Bindings come from <see cref="AppConfig.Hotkeys"/> (missing entries fall back to defaults);
/// re-registration is debounced on <see cref="ConfigService.Changed"/>; registration failures
/// (invalid gesture or already taken) are recorded in the status table exposed via <see cref="GetStatuses"/>.</para>
///
/// <para>Action executors run on the hotkey thread (the WM_HOTKEY callback) and only perform
/// thread-agnostic Win32 (get the foreground window, SetWindowPos).</para>
/// </summary>
public sealed class HotkeyService : IDisposable
{
    // ---- Action ids (match the Config.Hotkeys dictionary keys and SettingsPage) ----
    public const string ActToggleBorderless = "ToggleBorderless";
    public const string ActSendToZone1 = "SendToZone1";
    public const string ActSendToZone2 = "SendToZone2";
    public const string ActSendToZone3 = "SendToZone3";

    /// <summary>
    /// Action metadata: Id, default gesture, and a localized display name. Ordering here is the
    /// SettingsPage display order. <see cref="DisplayName"/> resolves at access time from
    /// Services.resw (<c>Services/Hotkey_&lt;Id&gt;</c>) via <see cref="Loc"/>, so the label follows the
    /// chosen UI language; if the resource is missing it falls back to a built-in English string.
    /// </summary>
    public sealed record ActionInfo(string Id, string DefaultGesture)
    {
        /// <summary>Localized display name for this action (Services.resw key <c>Hotkey_&lt;Id&gt;</c>).</summary>
        public string DisplayName
        {
            get
            {
                string key = "Services/Hotkey_" + Id;
                string v = Loc.T(key);
                return v == key ? FallbackName(Id) : v; // Loc.T returns the id unchanged on a miss
            }
        }

        // English fallback used only if the resw lookup fails (broken PRI); normally resw wins.
        private static string FallbackName(string id) => id switch
        {
            ActToggleBorderless => "Toggle borderless (foreground window)",
            ActSendToZone1 => "Send to zone 1",
            ActSendToZone2 => "Send to zone 2",
            ActSendToZone3 => "Send to zone 3",
            _ => id,
        };
    }

    // The default gestures were verified registrable via RegisterHotKey on this machine (2026-06,
    // see hotkey.log/diagnostics): the old Win+Alt+1/2/3 are reserved by the Windows taskbar jump
    // list (always 1409, and Win+Ctrl/Shift+digit are taken too). Switched to the same Ctrl+Alt
    // family (alongside the working Ctrl+Alt+B): B / 1 / 2 / 3, all of which register successfully.
    private static readonly ActionInfo[] _actions =
    {
        new(ActToggleBorderless, "Ctrl+Alt+B"),
        new(ActSendToZone1,      "Ctrl+Alt+1"),
        new(ActSendToZone2,      "Ctrl+Alt+2"),
        new(ActSendToZone3,      "Ctrl+Alt+3"),
    };

    /// <summary>Read-only action table (SettingsPage renders one row per entry).</summary>
    public static IReadOnlyList<ActionInfo> Actions => _actions;

    /// <summary>The default gesture for an action (used by SettingsPage to backfill missing entries).</summary>
    public static string DefaultGesture(string actionId)
        => _actions.FirstOrDefault(a => a.Id == actionId)?.DefaultGesture ?? "";

    /// <summary>The current registration state of one action (for the Settings page to report).
    /// <paramref name="Error"/>, when set, is a localized human-readable reason shown in the UI.</summary>
    public sealed record HotkeyStatus(string ActionId, string Gesture, bool Registered, string? Error);

    private Func<AppConfig>? _getConfig;

    private Thread? _thread;
    private IntPtr _hwnd;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;

    // The delegate is kept in a field: its address is handed to the OS, and a local would be GC'd
    // (the same pitfall as in TrayIcon).
    private readonly WndProc _wndProc;

    private const string WindowClassName = "Reframe.HotkeyHostWindow";

    // Debounce: ConfigService.Changed can fire in bursts; coalesce into a single re-registration.
    private const int DebounceMs = 250;
    private Timer? _debounce;
    private readonly object _gate = new();

    // ---- Registration state (read/written only on the hotkey thread; exposing the status table
    // requires a locked copy) ----
    // hotkeyId → (action Id, executor). Ids count up from 1 and are renumbered on every re-registration.
    private readonly Dictionary<int, RegEntry> _registered = new();
    private List<HotkeyStatus> _statuses = new();
    private readonly object _statusGate = new();

    private sealed record RegEntry(string ActionId, Action Execute);

    public HotkeyService() => _wndProc = WndProcImpl;

    /// <summary>Start: call on the UI thread. Idempotent. All action executors are thread-agnostic
    /// Win32, so none need to marshal back to the UI thread.</summary>
    public void Start(DispatcherQueue ui, Func<AppConfig> getConfig)
    {
        if (_thread != null) return;
        _ = ui; // all current actions run directly on the hotkey thread; the parameter is kept for the caller contract
        _getConfig = getConfig;

        _thread = new Thread(ThreadProc) { IsBackground = true, Name = "Reframe.Hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);

        // Initial registration + subscribe to config changes for re-registration.
        PostRegisterAll();
        ConfigService.Instance.Changed += OnConfigChanged;
    }

    /// <summary>Stop: unsubscribe, unregister all hotkeys, drain the pump, and tear down. Idempotent.</summary>
    public void Stop()
    {
        if (_thread == null) return;
        try { ConfigService.Instance.Changed -= OnConfigChanged; } catch { /* ignore */ }

        lock (_gate) { _debounce?.Dispose(); _debounce = null; }

        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        try { _thread.Join(2000); } catch { /* ignore */ }
        _thread = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _ready.Dispose();
    }

    /// <summary>A snapshot of each action's current registration state (the Settings page shows
    /// success/failure from this after "Apply").</summary>
    public IReadOnlyList<HotkeyStatus> GetStatuses()
    {
        lock (_statusGate) return new List<HotkeyStatus>(_statuses);
    }

    private void OnConfigChanged()
    {
        // Debounce: re-register on the hotkey thread after 250ms of quiet.
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => PostRegisterAll(), null, DebounceMs, Timeout.Infinite);
        }
    }

    // Post the "re-register" request to the hotkey thread (registration must share the window's thread).
    private void PostRegisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        PostMessage(_hwnd, WM_APP_REREGISTER, IntPtr.Zero, IntPtr.Zero);
    }

    private void ThreadProc()
    {
        _threadId = GetCurrentThreadId();
        RegisterWindowClass();

        _hwnd = CreateWindowEx(0, WindowClassName, "Reframe.Hotkey", 0,
            0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(in msg);
            DispatchMessage(in msg);
        }
    }

    private void RegisterWindowClass()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClassName,
        };
        RegisterClassEx(ref wc); // process-wide uniqueness is enough; a duplicate returning 0 is fine
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
            {
                int id = (int)(wParam.ToInt64() & 0xFFFFFFFF);
                if (_registered.TryGetValue(id, out var e))
                {
                    try { e.Execute(); } catch { /* a single failing action must not take down the pump */ }
                }
                return IntPtr.Zero;
            }

            case WM_APP_REREGISTER:
                ReRegisterAll();
                return IntPtr.Zero;

            case WM_CLOSE:
                UnregisterAll();
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ---- Registration / unregistration (both on the hotkey thread) ----

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
            UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    private void ReRegisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterAll();

        var cfg = _getConfig?.Invoke();
        var statuses = new List<HotkeyStatus>(_actions.Length);
        // English detail for the dashboard log (engine logs in English; see docs/dev/I18N.md).
        var failedEn = new List<string>();
        int nextId = 1;

        foreach (var act in _actions)
        {
            // Take the configured gesture; missing/blank falls back to the default.
            string gesture = act.DefaultGesture;
            if (cfg?.Hotkeys != null &&
                cfg.Hotkeys.TryGetValue(act.Id, out var g) && !string.IsNullOrWhiteSpace(g))
                gesture = g.Trim();

            if (!HotkeyGesture.TryParse(gesture, out uint mods, out uint vk))
            {
                // UI status carries a localized reason; the dashboard log stays English.
                statuses.Add(new HotkeyStatus(act.Id, gesture, false, Loc.T("Services/HotkeyErrInvalidGesture")));
                failedEn.Add($"{gesture} (invalid gesture)");
                continue;
            }

            int id = nextId++;
            // All global hotkeys add NOREPEAT to avoid auto-repeat while held.
            bool ok = RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok)
            {
                _registered[id] = new RegEntry(act.Id, BuildExecutor(act.Id));
                statuses.Add(new HotkeyStatus(act.Id, gesture, true, null));
                Log($"OK    {act.Id,-18} {gesture,-16} mods=0x{mods:X4} vk=0x{vk:X2}");
            }
            else
            {
                // Status table: localized reason for the Settings page. Log file/dashboard: English.
                statuses.Add(new HotkeyStatus(act.Id, gesture, false, DescribeError(err)));
                failedEn.Add($"{gesture} ({DescribeErrorEn(err)})");
                Log($"FAIL  {act.Id,-18} {gesture,-16} mods=0x{mods:X4} vk=0x{vk:X2} err={err} ({DescribeErrorEn(err)})");
            }
        }

        // Any registration failure: surface one visible line in the dashboard log (so the user
        // doesn't have to open Settings to find out). Engine log is English by the i18n red line.
        if (failedEn.Count > 0)
        {
            try
            {
                var detail = string.Join(", ", failedEn);
                App.Engine?.LogExternal($"Hotkey registration failed: {detail}. Rebind under Settings -> Hotkeys.");
            }
            catch { /* engine not ready etc.: don't let the notice path affect registration */ }
        }

        lock (_statusGate) _statuses = statuses;
    }

    /// <summary>
    /// Localized human-readable form of a RegisterHotKey Win32 error code, for the status table that
    /// the Settings page shows. The hotkey.log and dashboard log use <see cref="DescribeErrorEn"/> instead.
    /// </summary>
    private static string DescribeError(int err) => err switch
    {
        0    => Loc.T("Services/HotkeyErrUnknown"),
        1409 => Loc.T("Services/HotkeyErrAlreadyRegistered"), // ERROR_HOTKEY_ALREADY_REGISTERED: taken by this or another process
        1419 => Loc.T("Services/HotkeyErrNotRegistered"),     // ERROR_HOTKEY_NOT_REGISTERED (unregister path)
        87   => Loc.T("Services/HotkeyErrInvalidParam"),      // ERROR_INVALID_PARAMETER
        _    => Loc.T("Services/HotkeyErrCodeFormat", err),
    };

    /// <summary>English-only form of a RegisterHotKey error code, for diagnostic logs (hotkey.log and
    /// the engine dashboard log, which are English by the i18n red line).</summary>
    private static string DescribeErrorEn(int err) => err switch
    {
        0    => "unknown",
        1409 => "already in use",     // ERROR_HOTKEY_ALREADY_REGISTERED
        1419 => "hotkey unavailable", // ERROR_HOTKEY_NOT_REGISTERED
        87   => "invalid parameter",  // ERROR_INVALID_PARAMETER
        _    => $"error {err}",
    };

    // ---- Diagnostic log: %LOCALAPPDATA%\Reframe\hotkey.log (one line per registration, for
    // troubleshooting hotkey contention) ----
    private static readonly object _logFileGate = new();

    private static void Log(string line)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "hotkey.log");
            lock (_logFileGate)
            {
                string stamp = DateTime.Now.ToString("HH:mm:ss");
                File.AppendAllText(path, $"[{stamp}] {line}{Environment.NewLine}");
            }
        }
        catch { /* a logging failure must not affect registration */ }
    }

    // Map an action Id to its executor. Zone actions capture their 0-based index.
    private Action BuildExecutor(string actionId) => actionId switch
    {
        ActToggleBorderless => ToggleForegroundBorderless,
        ActSendToZone1 => () => SendForegroundToZone(0),
        ActSendToZone2 => () => SendForegroundToZone(1),
        ActSendToZone3 => () => SendForegroundToZone(2),
        _ => () => { },
    };

    // ---- Action executors ----

    /// <summary>Strip the border from / restore the foreground window (the original Ctrl+Alt+B
    /// behavior, moved over from TrayIcon→App).</summary>
    private void ToggleForegroundBorderless()
    {
        IntPtr h = WindowActivation.GetForeground();
        if (h == IntPtr.Zero) return;
        if (WindowOps.IsTracked(h))
        {
            WindowOps.Restore(h);
        }
        else
        {
            // Only strip the border: don't change geometry, don't set topmost; Apply keeps the snapshot internally.
            var target = new PlacementResolver.Target(MakeBorderless: true, Rect: null, Topmost: false);
            WindowOps.Apply(h, in target);
        }
    }

    /// <summary>
    /// Send the foreground window into the rectangle of zone <paramref name="zoneIndex"/> in Layouts[0],
    /// within the work area of "the monitor the window is currently on": ratio × rcWork (same as
    /// DragSnap), a plain SetWindowPos move (no borderless, no takeover, no topmost).
    /// </summary>
    private void SendForegroundToZone(int zoneIndex)
    {
        IntPtr h = WindowActivation.GetForeground();
        if (h == IntPtr.Zero) return;

        var cfg = _getConfig?.Invoke();
        var layout = cfg?.Layouts.Count > 0 ? cfg.Layouts[0] : null;
        if (layout is null || zoneIndex < 0 || zoneIndex >= layout.Zones.Count) return;
        var z = layout.Zones[zoneIndex];

        // The work area (rcWork) of the monitor the window is currently on.
        IntPtr hMon = NativeMethods.MonitorFromWindow(h, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return;

        // zone ratio × work area: go through the contract function PlacementResolver.ZoneToRect
        // (eliminating a hand-copied third formula; same source as DragSnap/ResolveRect). basis is
        // rcWork (so sending a window into a zone avoids the taskbar).
        var r = PlacementResolver.ZoneToRect(z, mi.rcWork);
        int left = r.Left, top = r.Top;
        int cw = r.Right - r.Left, ch = r.Bottom - r.Top;
        if (cw <= 0 || ch <= 0) return;

        // Restore from minimized first, then move (don't change Z-order, don't steal focus).
        if (NativeMethods.IsIconic(h))
            NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);

        NativeMethods.SetWindowPos(h, IntPtr.Zero, left, top, cw, ch,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    // ====================== P/Invoke (private to this service; does not pollute NativeMethods) ======================

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP = 0x8000;
    private const uint WM_APP_REREGISTER = WM_APP + 1; // custom: ask the hotkey thread to re-register

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    // RegisterHotKey modifier (same value as HotkeyGesture's MOD_*; NOREPEAT is added at registration time).
    private const uint MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(in MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
