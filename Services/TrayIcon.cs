using System.Runtime.InteropServices;

namespace Reframe.Services;

/// <summary>
/// Bare Shell_NotifyIcon tray implementation, fully self-contained (all P/Invoke lives in this
/// file; it does not touch Interop/NativeMethods.cs).
///
/// Design: owns a dedicated background thread that creates a message-only window, registers a
/// window class, and runs a GetMessage pump. Tray interactions (left click / right-click menu) are
/// delivered to this window's WndProc on that thread via a WM_USER callback. Actions that actually
/// touch the UI or the engine (open the window, toggle the engine, exit) are surfaced through
/// delegate callbacks, which the host (App) marshals back onto the UI thread. On Dispose,
/// PostMessage(WM_CLOSE) lets the thread tear down.
///
/// Global hotkeys are no longer handled here (moved to <see cref="HotkeyService"/>'s own message
/// window).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ---- Outbound callbacks (all fired on the tray thread; the host marshals to the UI thread) ----
    /// <summary>Left click on the tray icon / menu "Open": show and activate the main window.</summary>
    public Action? OnOpen;
    /// <summary>Menu "engine toggle" clicked: argument is the desired new state (negation of the current check).</summary>
    public Action<bool>? OnToggleEngine;
    /// <summary>Menu "Exit": restore windows and actually exit.</summary>
    public Action? OnExit;
    /// <summary>Whether the engine is currently enabled; the menu shows its checked state from this (provided by the host).</summary>
    public Func<bool>? EngineEnabledProvider;

    private Thread? _thread;
    private IntPtr _hwnd;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;

    // The delegate must be kept in a field: its address is handed to the OS, and a local would be
    // collected by the GC (the classic pitfall).
    private readonly WndProc _wndProc;
    private IntPtr _iconHandle;            // icon we obtained; must be destroyed after removing the tray icon
    private bool _ownIcon;                 // whether _iconHandle needs DestroyIcon

    private const string WindowClassName = "Reframe.TrayHostWindow";
    private const uint TrayCallbackMsg = WM_USER + 1;  // tray-event callback message
    private const uint TrayIconId = 1;

    // Menu command ids
    private const uint CmdOpen = 1;
    private const uint CmdToggle = 2;
    private const uint CmdExit = 3;

    public TrayIcon() => _wndProc = WndProcImpl;

    /// <summary>Start the tray. <paramref name="tooltip"/> is shown on hover. Global hotkeys are handled
    /// independently by HotkeyService and are no longer registered here.</summary>
    public void Start(string tooltip = "Reframe")
    {
        if (_thread != null) return;
        _tooltip = tooltip;

        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "Reframe.Tray"
        };
        _thread.SetApartmentState(ApartmentState.STA); // TrackPopupMenu needs a message pump; STA is more robust
        _thread.Start();
        _ready.Wait(3000);
    }

    private string _tooltip = "Reframe";

    private void ThreadProc()
    {
        RegisterWindowClass();

        // Message-only window: parent is HWND_MESSAGE, so it is never shown and only receives messages.
        _hwnd = CreateWindowEx(0, WindowClassName, "Reframe.Tray", 0,
            0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
            AddNotifyIcon();

        _ready.Set();

        // Message pump.
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
        RegisterClassEx(ref wc); // a duplicate registration returning 0 is fine (process-wide uniqueness is enough)
    }

    private void AddNotifyIcon()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = TrayCallbackMsg,
            hIcon = ResolveIcon(),
            szTip = _tooltip,
        };
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    /// <summary>
    /// Resolve the tray icon: prefer LoadImage straight from Assets\reframe.ico in the output
    /// directory (best-fitting size, sharpest), fall back to ExtractIconEx of the exe's icon 0, then
    /// fall back to the system application icon.
    /// </summary>
    private IntPtr ResolveIcon()
    {
        // 1) Prefer Assets\reframe.ico: loaded at the small-icon size, its edges are crisper than ExtractIconEx.
        try
        {
            string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "reframe.ico");
            if (File.Exists(ico))
            {
                int cx = GetSystemMetrics(SM_CXSMICON);
                int cy = GetSystemMetrics(SM_CYSMICON);
                IntPtr h = LoadImage(IntPtr.Zero, ico, IMAGE_ICON, cx, cy,
                    LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (h != IntPtr.Zero)
                {
                    _iconHandle = h; _ownIcon = true; return _iconHandle;
                }
            }
        }
        catch { /* fall through to ExtractIconEx */ }

        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(exe))
            {
                IntPtr[] big = new IntPtr[1];
                IntPtr[] small = new IntPtr[1];
                int n = ExtractIconEx(exe, 0, big, small, 1);
                if (n > 0)
                {
                    if (small[0] != IntPtr.Zero)
                    {
                        if (big[0] != IntPtr.Zero) DestroyIcon(big[0]);
                        _iconHandle = small[0]; _ownIcon = true; return _iconHandle;
                    }
                    if (big[0] != IntPtr.Zero)
                    {
                        _iconHandle = big[0]; _ownIcon = true; return _iconHandle;
                    }
                }
            }
        }
        catch { /* fall through to the last resort */ }

        // Last resort: the system application icon (a shared handle; must not be DestroyIcon'd).
        _iconHandle = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        _ownIcon = false;
        return _iconHandle;
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case TrayCallbackMsg:
            {
                // The low word of lParam is the mouse message.
                uint mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                switch (mouse)
                {
                    case WM_LBUTTONUP:
                        OnOpen?.Invoke();
                        break;
                    case WM_RBUTTONUP:
                    case WM_CONTEXTMENU:
                        ShowContextMenu();
                        break;
                }
                return IntPtr.Zero;
            }

            case WM_COMMAND:
            {
                uint cmd = (uint)(wParam.ToInt64() & 0xFFFF);
                switch (cmd)
                {
                    case CmdOpen:
                        OnOpen?.Invoke();
                        break;
                    case CmdToggle:
                        bool cur = EngineEnabledProvider?.Invoke() ?? true;
                        OnToggleEngine?.Invoke(!cur);
                        break;
                    case CmdExit:
                        OnExit?.Invoke();
                        break;
                }
                return IntPtr.Zero;
            }

            case WM_CLOSE:
                // Tear-down must happen on this thread (the one that created the icon).
                RemoveNotifyIcon();
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                if (_ownIcon && _iconHandle != IntPtr.Zero)
                {
                    DestroyIcon(_iconHandle);
                    _iconHandle = IntPtr.Zero;
                }
                PostQuitMessage(0); // make GetMessage return 0 so the pump exits and the thread ends
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void RemoveNotifyIcon()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
        };
        Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            // Menu labels are localized. Built on this (the tray's own STA) thread: MRT Core's
            // ResourceLoader is an agile WinRT object and GetString has no UI-thread affinity, so
            // Loc.T resolves correctly off the UI thread (see docs/dev/I18N.md).
            AppendMenu(menu, MF_STRING, CmdOpen, Loc.T("Services/TrayOpen"));

            bool engineOn = EngineEnabledProvider?.Invoke() ?? true;
            uint toggleFlags = MF_STRING | (engineOn ? MF_CHECKED : MF_UNCHECKED);
            AppendMenu(menu, toggleFlags, CmdToggle, Loc.T("Services/TrayEngine"));

            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, CmdExit, Loc.T("Services/TrayExit"));

            GetCursorPos(out POINT pt);
            // TrackPopupMenu convention: bring the host window to the foreground first, otherwise the
            // menu won't dismiss correctly when the user clicks outside it.
            SetForegroundWindow(_hwnd);
            TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            // Removing the tray icon must happen on the original thread; a synchronous SendMessage
            // hop would be more robust, but here we just PostMessage(WM_CLOSE) and put the tear-down
            // logic in WM_CLOSE/WM_DESTROY.
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        try { _thread?.Join(2000); } catch { /* ignore */ }
        _thread = null;
        _ready.Dispose();
    }

    // ====================== P/Invoke ======================

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_USER = 0x0400;
    private const uint WM_NULL = 0x0000;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

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

    // NOTIFYICONDATAW (the leading fields we use; szTip's 128 chars is the Vista+ size).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_UNCHECKED = 0x00000000;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_BOTTOMALIGN = 0x0020;

    private static readonly IntPtr IDI_APPLICATION = new(32512);

    // LoadImage: load an icon from a file at the requested size.
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type,
        int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex,
        IntPtr[] phiconLarge, IntPtr[] phiconSmall, int nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
