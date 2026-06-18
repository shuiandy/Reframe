using System.Runtime.InteropServices;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>
/// Owns a hidden, <b>real top-level</b> window (deliberately NOT a <c>HWND_MESSAGE</c> message-only window —
/// those do not receive broadcast messages) on a dedicated thread, so it can receive
/// <c>WM_DISPLAYCHANGE</c> when the display topology / resolution changes (monitor add/remove, resolution
/// change, streaming VDD coming up or going away). Raises <see cref="DisplayChanged"/> on its own thread;
/// the consumer (the persistence engine) must only enqueue to its serial actor and do no heavy work here.
///
/// <para>Thread + teardown model mirrors <c>Services.HotkeyService</c> / <c>Services.TrayIcon</c>: create the
/// window and run a <c>GetMessage</c> pump on a dedicated thread; tear down with
/// <c>PostMessage(WM_CLOSE)</c> → <c>DestroyWindow</c> → <c>WM_DESTROY</c> → <c>PostQuitMessage</c> (NOT
/// <c>PostThreadMessage(WM_QUIT)</c> — destroying the window first lets later power/WTS notification
/// registrations, added in a future phase, unregister cleanly before the pump exits).</para>
/// </summary>
public sealed class DisplayChangeListener : IDisposable
{
    /// <summary>
    /// Injectable last-resort error sink for the dedicated pump thread. App wires this to
    /// <c>Services.CrashLog.Write</c>; Core itself keeps zero Services dependency (mirrors
    /// <see cref="WinEventHook.OnThreadError"/> / <see cref="Watcher.OnThreadError"/>). Static because the
    /// pump is process-wide diagnostics.
    /// </summary>
    public static Action<string, Exception?>? OnThreadError;

    /// <summary>Raised when the display configuration may have changed (WM_DISPLAYCHANGE). Fires on the listener thread.</summary>
    public event Action? DisplayChanged;

    private const string WindowClassName = "Reframe.DisplayChangeHostWindow";

    // The delegate is kept in a field: its address is handed to the OS, and a local would be GC'd
    // (the classic SetWindowsHookEx/WndProc pitfall — same as HotkeyService/TrayIcon).
    private readonly NativeMethods.WndProc _wndProc;

    private Thread? _thread;
    private IntPtr _hwnd;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;

    public DisplayChangeListener() => _wndProc = WndProcImpl;

    /// <summary>Start the listener thread; returns once the window exists (or after a 2s timeout, treated as failure). Idempotent.</summary>
    public bool Start()
    {
        if (_disposed) return false;
        if (_thread is { IsAlive: true }) return _hwnd != IntPtr.Zero; // already running

        // Reset state so a restart — or a retry after a failed CreateWindowEx left a dead thread — starts clean.
        _ready.Reset();
        _hwnd = IntPtr.Zero;
        _thread = new Thread(ThreadProc) { IsBackground = true, Name = "Reframe.DisplayChange" };
        _thread.Start();
        return _ready.Wait(2000) && _hwnd != IntPtr.Zero;
    }

    /// <summary>Stop the pump and join the thread. Idempotent.</summary>
    public void Stop()
    {
        if (_thread == null) return;
        if (_hwnd != IntPtr.Zero)
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        try { _thread.Join(2000); } catch { /* ignore */ }
        _thread = null;
        _hwnd = IntPtr.Zero; // clear so a later Start() doesn't see a stale handle
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _ready.Dispose();
    }

    private void ThreadProc()
    {
        // Whole-proc guard: a raw background thread; an escaping exception fast-fails the process without
        // reaching App/AppDomain handlers. _ready is always Set (finally) so a startup failure surfaces as
        // Start()'s 2s timeout rather than a hang.
        try
        {
            RegisterWindowClass();

            // Real top-level window (parent = Zero, NOT HWND_MESSAGE) so the broadcast WM_DISPLAYCHANGE
            // reaches it. Not visible (no WS_VISIBLE) and tool-window ex-style so it never appears in the
            // taskbar / Alt-Tab.
            _hwnd = NativeMethods.CreateWindowEx(
                (uint)NativeMethods.WS_EX_TOOLWINDOW, WindowClassName, "Reframe.DisplayChange",
                0 /* WS_OVERLAPPED, no WS_VISIBLE */, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                // Creation failed: bail without entering the pump. The finally sets _ready; Start() sees
                // _hwnd == 0 and reports failure, and Stop()'s Join completes because the thread is exiting.
                OnThreadError?.Invoke("DisplayChangeListener",
                    new Exception($"CreateWindowEx failed (Win32 {Marshal.GetLastWin32Error()})"));
                return;
            }

            _ready.Set();

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessage(in msg);
            }
        }
        catch (Exception ex)
        {
            OnThreadError?.Invoke("DisplayChangeListener thread", ex);
        }
        finally
        {
            _ready.Set(); // idempotent; unblocks Start() if we threw before reaching the pump
        }
    }

    private void RegisterWindowClass()
    {
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = WindowClassName,
        };
        NativeMethods.RegisterClassEx(ref wc); // process-wide uniqueness is enough; a duplicate returning 0 is fine
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_DISPLAYCHANGE:
                // Only signal; the consumer recomputes the monitor topology and decides whether to restore.
                try { DisplayChanged?.Invoke(); }
                catch (Exception ex) { OnThreadError?.Invoke("DisplayChangeListener handler", ex); }
                return IntPtr.Zero;

            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
