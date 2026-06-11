using System.Runtime.InteropServices;
using System.Text;

namespace Reframe.Interop;

/// <summary>Low-level Win32 calls collected here. x64 target only, so the *Ptr versions are used directly.</summary>
public static class NativeMethods
{
    // ---- Window enumeration / info ----
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd); // Minimized?

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    public const uint GW_OWNER = 4;

    // ---- Styles ----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    // The "frame-class" style bits of a normal window
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_BORDER = 0x00800000L;
    public const long WS_DLGFRAME = 0x00400000L;
    public const long WS_CHILD = 0x40000000L;

    public const long WS_EX_DLGMODALFRAME = 0x00000001L;
    public const long WS_EX_WINDOWEDGE = 0x00000100L;
    public const long WS_EX_CLIENTEDGE = 0x00000200L;
    public const long WS_EX_STATICEDGE = 0x00020000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_TOPMOST = 0x00000008L; // Controlled by SetWindowPos(HWND_TOPMOST/NOTOPMOST); decided by the snapshot on restore

    // ---- Position / size ----
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    // Z-order anchors: topmost / not-topmost. Used as hWndInsertAfter only when the topmost state needs to change (and then SWP_NOZORDER must not be set).
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public const int SW_RESTORE = 9;   // First restore from maximized/minimized, then position

    // ---- Monitors ----
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;   // The whole monitor (including the taskbar)
        public RECT rcWork;      // The work area (excluding the taskbar)
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;  // \\.\DISPLAY1 etc.
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd); // Whether the handle still refers to a valid window

    // ---- DWM: cloaked (hidden) detection ----
    // A DWM-"cloaked" window has IsWindowVisible=true but isn't actually on the currently visible desktop:
    // a suspended UWP, a window left on another virtual desktop after switching away, etc. It should be
    // dropped when enumerating "real windows you can create a profile for".
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    public const int DWMWA_CLOAKED = 14;

    // ---- Foreground window / cursor clip ----
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // ClipCursor(NULL) releases the clip; passing a rect clamps the cursor inside that screen rect. Coordinates are virtual-desktop pixels.
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClipCursor(in RECT lpRect);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "ClipCursor")]
    public static extern bool ClipCursorRelease(IntPtr lpRect); // Pass IntPtr.Zero = NULL to release the clip

    // ---- WinEvent hook (event-driven detection) ----
    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;   // The callback runs in our process, no injection into the target
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002; // Don't report events from our own process

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A; // User starts dragging/resizing a window with the mouse
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;   // User finishes dragging/resizing a window
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;

    public const int OBJID_WINDOW = 0; // Only the window itself matters; not child objects/cursor/etc.

    // ---- Message pump for the hook thread ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(in MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(in MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // Before calling an API like SetWindowLongPtr (where "a return of 0 could be a valid old value or a
    // failure"), clear the thread's last-error first; after the call, judge failure by
    // return==0 && GetLastWin32Error()!=0 (a protected/UWP window we can't touch).
    [DllImport("kernel32.dll")]
    public static extern void SetLastError(uint dwErrCode);

    public const uint WM_QUIT = 0x0012;

    // ---- Drag snapping: key state / cursor position ----
    // The high bit (0x8000) means the key is currently down. Reads the instantaneous state only; doesn't affect the input queue.
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public const int VK_SHIFT = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    // Returns virtual-desktop physical-pixel coordinates (the process is PerMonitorV2).
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ---- Extended styles for overlay "click-through" ----
    // TRANSPARENT: hit-testing passes through this window to what's below; LAYERED: required alongside
    // TRANSPARENT for real pass-through; NOACTIVATE: clicks don't steal focus; TOOLWINDOW: keeps it out of
    // Alt-Tab/the taskbar.
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
}
