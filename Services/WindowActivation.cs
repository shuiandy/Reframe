using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Reframe.Services;

/// <summary>
/// 唤起/前台相关的小工具(自包含 P/Invoke,不碰 NativeMethods.cs)。
/// 给托盘唤起主窗口、全局热键取前台窗口用。
/// </summary>
public static class WindowActivation
{
    /// <summary>当前前台窗口句柄(全局热键对它动手)。</summary>
    public static IntPtr GetForeground() => GetForegroundWindow();

    /// <summary>把自己的窗口提到前台并激活(托盘唤起;Activate 在后台触发时未必抢得到焦点,补一记 SetForegroundWindow)。</summary>
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
