using System.Runtime.InteropServices;
using System.Threading;

namespace Reframe.Services;

/// <summary>
/// Single-instance guard (self-contained P/Invoke). Acquires a named mutex; failing to acquire it
/// means an instance is already running, so find the existing main window by title, bring it to the
/// foreground, and exit this process.
///
/// Usage: call <see cref="EnsureSingle"/> first thing in App construction / OnLaunched; on a false
/// return, return immediately (it has already Environment.Exit'd internally, so normally control
/// never reaches past the return).
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Global\Reframe.SingleInstance";
    private const string MainWindowTitle = "Reframe"; // MainWindow.xaml's Title; FindWindow matches on it

    // Held statically: keep this lock for the process lifetime; don't let the GC collect it and release it early.
    private static Mutex? _mutex;

    /// <summary>Acquire the lock. If an instance already exists → bring it to the front and Environment.Exit(0) (this call does not return); otherwise return true.</summary>
    public static bool EnsureSingle()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew) return true;

        // An instance already exists: find its main window by title and bring it to the front.
        // Limitation: FindWindow matches on the visible title — if the user changed the title, or another
        // window happens to share the title, it may match the wrong one / not find it; but the mutex already
        // guarantees no duplicate launch, so the worst case is just "didn't raise the existing window to the
        // front", which is acceptable (not worth a class-name/IPC scheme).
        IntPtr hwnd = FindWindow(null, MainWindowTitle);
        if (hwnd != IntPtr.Zero)
        {
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);
            else
                ShowWindow(hwnd, SW_SHOW); // it may be hidden to the tray; show it
            SetForegroundWindow(hwnd);
        }

        Environment.Exit(0);
        return false; // unreachable
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
}
