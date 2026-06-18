using Reframe.Interop;

namespace Reframe.Core;

/// <summary>
/// SetWinEventHook wrapper: window shown / title change / foreground switch / location change → raises the hwnd.
/// The hook callback requires the thread to have a message pump, so it owns a dedicated background thread:
/// install the hook → GetMessage loop; on Dispose, PostThreadMessage(WM_QUIT) exits the loop, then Unhook.
/// The caller debounces WindowEvent itself (the same window fires several times in quick succession).
/// </summary>
public sealed class WinEventHook : IDisposable
{
    /// <summary>
    /// Injectable last-resort error sink for the dedicated hook thread. An exception escaping the
    /// hook pump would otherwise fast-fail the process without reaching any managed handler (a raw
    /// <see cref="Thread"/> bypasses App/AppDomain handlers), leaving no crash.log entry. App wires
    /// this to <c>Services.CrashLog.Write</c> at startup; Core itself stays free of any Services
    /// dependency (the Tests project links these Core sources standalone, so the red line matters).
    /// Static because the callback is process-wide diagnostics, not per-instance state.
    /// </summary>
    public static Action<string, Exception?>? OnThreadError;

    /// <summary>
    /// The matched event: (eventType, hwnd). eventType lets the consumer distinguish a foreground switch
    /// (EVENT_SYSTEM_FOREGROUND) from a window shown/title/location change. Already filtered to the window
    /// itself (idObject==OBJID_WINDOW). Raised on a background thread.
    /// </summary>
    public event Action<uint, IntPtr>? WindowEvent;

    // The delegate must be kept in a field: under OUT_OF_CONTEXT the callback is invoked cross-thread by the
    // system, and a local variable would be GC'd, invalidating the callback address (a classic pitfall).
    private readonly NativeMethods.WinEventProc _proc;

    private Thread? _thread;
    // volatile: written by the hook thread, read by Start/Dispose; timeout cleanup must read the value the hook thread just set.
    private volatile uint _threadId;
    private IntPtr _hook;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;

    public WinEventHook() => _proc = OnWinEvent;

    /// <summary>
    /// Start a dedicated thread that installs the hook and runs a message pump. Returns whether startup succeeded.
    /// <para>Waits up to 2s for the hook thread to become ready. A timeout is treated as a startup failure:
    /// make a best effort to exit the thread (PostThreadMessage(WM_QUIT) + Join), clean up state and return
    /// false — rather than "pretending success" and leaving a zombie thread that may not have installed the
    /// hook and that Dispose can't reliably Post to either. The caller falls back to polling (degraded mode).</para>
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

        if (_ready.Wait(2000)) // Wait until the hook is installed (threadId obtained) before returning, so Dispose can Post reliably
            return true;

        // Timeout: make a best effort to collect the thread; don't leave a zombie. threadId is set on the first line of ThreadProc, so it's most likely available here.
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
        // Whole-proc guard: an exception escaping a raw background thread fast-fails the process
        // without reaching App/AppDomain handlers — record it so a crash leaves a trace. _ready is
        // always Set so a startup failure surfaces as a Start() timeout (degraded mode) rather than a hang.
        IntPtr hookFg = IntPtr.Zero;
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();

            // One hook covers the contiguous SHOW..NAMECHANGE range (0x8002..0x800C), including LOCATIONCHANGE
            // (0x800B); FOREGROUND (0x0003) needs a separate hook.
            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero, _proc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

            hookFg = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _proc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

            _ready.Set();

            // Message pump: GetMessage blocks until WM_QUIT (returns 0).
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessage(in msg);
            }
        }
        catch (Exception ex)
        {
            OnThreadError?.Invoke("WinEventHook thread", ex);
        }
        finally
        {
            _ready.Set(); // idempotent; unblocks Start() if we threw before reaching the pump
            if (_hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hook);
            if (hookFg != IntPtr.Zero) NativeMethods.UnhookWinEvent(hookFg);
            _hook = IntPtr.Zero;
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // Only window-itself events: idObject==OBJID_WINDOW, idChild==0, valid hwnd.
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
