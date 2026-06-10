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
    /// <summary>
    /// 命中事件:(eventType, hwnd)。eventType 让消费方区分前台切换(EVENT_SYSTEM_FOREGROUND)
    /// 与窗口出现/标题/位置变化。已过滤到窗口本身(idObject==OBJID_WINDOW)。后台线程触发。
    /// </summary>
    public event Action<uint, IntPtr>? WindowEvent;

    // 委托必须保存为字段:OUT_OF_CONTEXT 下回调由系统跨线程调用,
    // 局部变量会被 GC 回收导致回调地址失效(经典坑)。
    private readonly NativeMethods.WinEventProc _proc;

    private Thread? _thread;
    // volatile:钩子线程写、Start/Dispose 读;超时清理时要读到刚被钩子线程设上的值。
    private volatile uint _threadId;
    private IntPtr _hook;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;

    public WinEventHook() => _proc = OnWinEvent;

    /// <summary>
    /// 启一个独占线程装钩子并跑消息泵。返回是否启动成功。
    /// <para>等待钩子线程就绪最多 2s。超时视为启动失败:尽力让该线程退出(PostThreadMessage(WM_QUIT) +
    /// Join),清理状态并返回 false——而非"假装成功"留下一个可能没装上钩子、Dispose 也 Post 不到位的僵线程。
    /// 调用方据此走兜底轮询(降级运行)。</para>
    /// </summary>
    public bool Start()
    {
        if (_disposed) return false;
        if (_thread != null) return true;

        var t = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "Reframe.WinEventHook"
        };
        _thread = t;
        t.Start();

        if (_ready.Wait(2000)) // 等钩子装好(拿到 threadId)再返回,保证 Dispose 能 Post 到位
            return true;

        // 超时:尽力收线程,别留僵线程。threadId 在 ThreadProc 首行即设,此处多半已可用。
        uint tid = _threadId;
        if (tid != 0)
            NativeMethods.PostThreadMessage(tid, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        try { t.Join(1000); } catch { /* ignore */ }
        _thread = null;
        _threadId = 0;
        return false;
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

        WindowEvent?.Invoke(eventType, hwnd);
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
