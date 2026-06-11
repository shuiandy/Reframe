using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Reframe.Services;

/// <summary>
/// Small activation / foreground helpers (self-contained P/Invoke; does not touch NativeMethods.cs).
/// Used by the tray to raise the main window and by global hotkeys to get the foreground window.
/// </summary>
public static class WindowActivation
{
    /// <summary>The current foreground window handle (the one global hotkeys act on).</summary>
    public static IntPtr GetForeground() => GetForegroundWindow();

    /// <summary>Raise and activate our own window (tray activation; Activate may not grab focus when fired from the background, so follow up with a SetForegroundWindow).</summary>
    public static void BringToFront(Window window)
    {
        IntPtr h = WindowNative.GetWindowHandle(window);
        if (h == IntPtr.Zero) return;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        SetForegroundWindow(h);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;
}
