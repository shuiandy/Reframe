using Reframe.Interop;

namespace Reframe.Core;

/// <summary>
/// SetWinEventHook 封装:窗口出现/标题变化/前台切换/位置变化 → 抛出 hwnd。
/// 钩子回调要求线程有消息泵,故独占一个后台线程:装钩子 → GetMessage 循环;
/// Dispose 时 PostThreadMessage(WM_QUIT) 让循环退出,再 Unhook。
/// 调用方对 WindowEvent 自行防抖(同一窗口短时间会来多次)。
/// </summary>
public sealed class WinEventHook : IDisposable
{
    /// <summary>命中事件的窗口句柄(已过滤到顶层窗口)。后台线程触发。</summary>
    public event Action<IntPtr>? WindowEvent;

    // 委托必须保存为字段:OUT_OF_CONTEXT 下回调由系统跨线程调用,
    // 局部变量会被 GC 回收导致回调地址失效(经典坑)。
    private readonly NativeMethods.WinEventProc _proc;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;

    public WinEventHook() => _proc = OnWinEvent;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "Reframe.WinEventHook"
        };
        _thread.Start();
        _ready.Wait(2000); // 等钩子装好(拿到 threadId)再返回,保证 Dispose 能 Post 到位
    }

    private void ThreadProc()
    {
        _threadId = NativeMethods.GetCurrentThreadId();

        // 一个钩子覆盖 SHOW..NAMECHANGE 的连续区间(0x8002..0x800C),
        // 含 LOCATIONCHANGE(0x800B);FOREGROUND(0x0003)需单独一个钩子。
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero, _proc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        IntPtr hookFg = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        _ready.Set();

        // 消息泵:GetMessage 阻塞直到收到 WM_QUIT(返回 0)。
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(in msg);
            NativeMethods.DispatchMessage(in msg);
        }

        if (_hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hook);
        if (hookFg != IntPtr.Zero) NativeMethods.UnhookWinEvent(hookFg);
        _hook = IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // 只要窗口本身的事件:idObject==OBJID_WINDOW、idChild==0、hwnd 有效。
        if (hwnd == IntPtr.Zero) return;
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;

        WindowEvent?.Invoke(hwnd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_thread != null)
        {
            if (_threadId != 0)
                NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            try { _thread.Join(2000); } catch { /* ignore */ }
            _thread = null;
        }
        _ready.Dispose();
    }
}
