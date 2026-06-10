using System.Runtime.InteropServices;
using System.Threading;

namespace Reframe.Services;

/// <summary>
/// 单实例守卫(自包含 P/Invoke)。命名互斥量抢占;抢不到说明已有实例在跑,
/// 按窗口标题找到已有主窗口并唤起到前台,然后本进程退出。
///
/// 用法:App 构造/OnLaunched 最先调 <see cref="EnsureSingle"/>;返回 false 时立即 return
/// (内部已 Environment.Exit,正常不会走到 return 之后)。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Global\Reframe.SingleInstance";
    private const string MainWindowTitle = "Reframe"; // MainWindow.xaml 的 Title,FindWindow 据此找

    // 静态持有:进程存活期间一直占着这把锁,别让 GC 回收导致提前释放。
    private static Mutex? _mutex;

    /// <summary>抢锁。已有实例 → 唤起它并 Environment.Exit(0)(本调用不返回);否则返回 true。</summary>
    public static bool EnsureSingle()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew) return true;

        // 已有实例:按窗口标题找到它的主窗口并唤前台。
        // 局限:FindWindow 按可见标题匹配——若用户改了标题、或恰有同名标题的别家窗口,可能找错/找不到;
        // 但互斥量已保证不会重复启动,最坏情形只是"没把已有窗口顶到前台",可接受(不值得上 class-name/IPC 方案)。
        IntPtr hwnd = FindWindow(null, MainWindowTitle);
        if (hwnd != IntPtr.Zero)
        {
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);
            else
                ShowWindow(hwnd, SW_SHOW); // 可能被隐藏到托盘,显示出来
            SetForegroundWindow(hwnd);
        }

        Environment.Exit(0);
        return false; // 不会到达
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
