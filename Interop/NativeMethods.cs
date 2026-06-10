using System.Runtime.InteropServices;
using System.Text;

namespace Reframe.Interop;

/// <summary>底层 Win32 调用集中放这里。仅 x64 目标,直接用 *Ptr 版本。</summary>
public static class NativeMethods
{
    // ---- 窗口枚举 / 信息 ----
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd); // 最小化?

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    public const uint GW_OWNER = 4;

    // ---- 样式 ----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    // 普通窗口的"边框类"样式位
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_THICKFRAME = 0x00040000L;
    public const long WS_BORDER = 0x00800000L;
    public const long WS_DLGFRAME = 0x00400000L;
    public const long WS_SYSMENU = 0x00080000L;
    public const long WS_MINIMIZEBOX = 0x00020000L;
    public const long WS_MAXIMIZEBOX = 0x00010000L;
    public const long WS_POPUP = unchecked((long)0x80000000L);
    public const long WS_VISIBLE = 0x10000000L;
    public const long WS_CHILD = 0x40000000L;

    public const long WS_EX_DLGMODALFRAME = 0x00000001L;
    public const long WS_EX_WINDOWEDGE = 0x00000100L;
    public const long WS_EX_CLIENTEDGE = 0x00000200L;
    public const long WS_EX_STATICEDGE = 0x00020000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_APPWINDOW = 0x00040000L;

    // ---- 位置 / 尺寸 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public const int SW_RESTORE = 9;   // 先从最大化/最小化还原,再定位

    // ---- 显示器 ----
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;   // 整块屏(含任务栏)
        public RECT rcWork;      // 工作区(不含任务栏)
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;  // \\.\DISPLAY1 等
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd); // 句柄是否仍指向有效窗口

    // ---- WinEvent 钩子(事件驱动检测) ----
    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;   // 回调跑在本进程,不注入目标
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002; // 不上报自身进程的事件

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;

    public const int OBJID_WINDOW = 0; // 只关心窗口本身,不要子对象/光标等

    // ---- 钩子线程的消息泵 ----
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

    public const uint WM_QUIT = 0x0012;
}
