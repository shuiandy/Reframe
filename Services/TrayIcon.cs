using System.Runtime.InteropServices;

namespace Reframe.Services;

/// <summary>
/// 裸 Shell_NotifyIcon 托盘实现,完全自包含(P/Invoke 全在本文件,不碰 Interop/NativeMethods.cs)。
///
/// 设计:独占一个后台线程,建 message-only 窗口 + 注册窗口类 + GetMessage 消息泵。
/// 托盘交互(左键/右键菜单)由该窗口的 WndProc 在本线程收到 WM_USER 回调;
/// 真正要动 UI/引擎的动作(打开窗口、开关引擎、专注模式、退出)通过委托回调出去,
/// 由宿主(App)切回 UI 线程执行。Dispose 时 PostMessage(WM_CLOSE) 让线程收尾。
///
/// 全局热键不再由本类承载(已迁至 <see cref="HotkeyService"/> 自己的消息窗口)。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ---- 对外回调(都在托盘线程触发,宿主自行切 UI 线程) ----
    /// <summary>左键单击托盘 / 菜单"打开":显示并激活主窗口。</summary>
    public Action? OnOpen;
    /// <summary>菜单"引擎开关"被点:参数为期望的新状态(取反当前勾选)。</summary>
    public Action<bool>? OnToggleEngine;
    /// <summary>菜单"专注模式"被点:宿主切回 UI 线程后 Toggle 幕布。</summary>
    public Action? OnToggleCurtain;
    /// <summary>菜单"退出":还原窗口 + 真正退出。</summary>
    public Action? OnExit;
    /// <summary>引擎当前是否启用,菜单据此显示勾选态(宿主提供取值器)。</summary>
    public Func<bool>? EngineEnabledProvider;
    /// <summary>专注模式当前是否开启,菜单据此显示勾选态(宿主提供取值器)。</summary>
    public Func<bool>? CurtainOnProvider;

    private Thread? _thread;
    private IntPtr _hwnd;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;

    // 委托必须保存为字段:WndProc 地址会传给系统,局部变量会被 GC 回收(经典坑)。
    private readonly WndProc _wndProc;
    private IntPtr _iconHandle;            // 自取的图标,需在移除托盘后销毁
    private bool _ownIcon;                 // _iconHandle 是否需要 DestroyIcon

    private const string WindowClassName = "Reframe.TrayHostWindow";
    private const uint TrayCallbackMsg = WM_USER + 1;  // 托盘事件回传消息
    private const uint TrayIconId = 1;

    // 菜单项命令 id
    private const uint CmdOpen = 1;
    private const uint CmdToggle = 2;
    private const uint CmdCurtain = 3;
    private const uint CmdExit = 4;

    public TrayIcon() => _wndProc = WndProcImpl;

    /// <summary>启动托盘。tooltip 显示在悬停提示。全局热键由 HotkeyService 独立承载,这里不再注册。</summary>
    public void Start(string tooltip = "Reframe")
    {
        if (_thread != null) return;
        _tooltip = tooltip;

        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "Reframe.Tray"
        };
        _thread.SetApartmentState(ApartmentState.STA); // TrackPopupMenu 需要消息泵,STA 更稳
        _thread.Start();
        _ready.Wait(3000);
    }

    private string _tooltip = "Reframe";

    private void ThreadProc()
    {
        RegisterWindowClass();

        // message-only 窗口:父为 HWND_MESSAGE,不显示、只收消息。
        _hwnd = CreateWindowEx(0, WindowClassName, "Reframe.Tray", 0,
            0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
            AddNotifyIcon();

        _ready.Set();

        // 消息泵。
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
        RegisterClassEx(ref wc); // 重复注册返回 0 也无妨(进程内唯一即可)
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
    /// 取托盘图标:优先从输出目录 Assets\reframe.ico 直接 LoadImage(尺寸最贴、最清晰),
    /// 退化到从 exe 第 0 个图标 ExtractIconEx,再退化到系统应用图标。
    /// </summary>
    private IntPtr ResolveIcon()
    {
        // 1) 优先 Assets\reframe.ico:按托盘小图标尺寸加载,边缘比 ExtractIconEx 更锐。
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
        catch { /* 落到 ExtractIconEx */ }

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
        catch { /* 落到兜底 */ }

        // 兜底:系统应用图标(共享句柄,不可 DestroyIcon)。
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
                // lParam 低位为鼠标消息。
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
                    case CmdCurtain:
                        OnToggleCurtain?.Invoke();
                        break;
                    case CmdExit:
                        OnExit?.Invoke();
                        break;
                }
                return IntPtr.Zero;
            }

            case WM_CLOSE:
                // 收尾必须在本线程(创建图标的线程)做。
                RemoveNotifyIcon();
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                if (_ownIcon && _iconHandle != IntPtr.Zero)
                {
                    DestroyIcon(_iconHandle);
                    _iconHandle = IntPtr.Zero;
                }
                PostQuitMessage(0); // 让 GetMessage 返回 0,泵退出,线程结束
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
            AppendMenu(menu, MF_STRING, CmdOpen, "打开");

            bool engineOn = EngineEnabledProvider?.Invoke() ?? true;
            uint toggleFlags = MF_STRING | (engineOn ? MF_CHECKED : MF_UNCHECKED);
            AppendMenu(menu, toggleFlags, CmdToggle, "引擎");

            bool curtainOn = CurtainOnProvider?.Invoke() ?? false;
            uint curtainFlags = MF_STRING | (curtainOn ? MF_CHECKED : MF_UNCHECKED);
            AppendMenu(menu, curtainFlags, CmdCurtain, "专注模式");

            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, CmdExit, "退出");

            GetCursorPos(out POINT pt);
            // TrackPopupMenu 约定:先把宿主窗口提到前台,菜单才会在点外面时正确消失。
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
            // 移除托盘图标必须在原线程做,用 SendMessage 同步切过去更稳;
            // 这里直接 PostMessage(WM_CLOSE),收尾逻辑放 WM_CLOSE/WM_DESTROY。
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

    // NOTIFYICONDATAW(用到的前若干字段;szTip 128 字符是 Vista+ 尺寸)。
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

    // LoadImage:从文件按指定尺寸加载图标。
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
