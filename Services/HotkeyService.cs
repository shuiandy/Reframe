using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Reframe.Core;
using Reframe.Interop;

namespace Reframe.Services;

/// <summary>
/// 全局热键统管:自带一个后台线程 + message-only 窗口收 WM_HOTKEY(照搬 <see cref="TrayIcon"/> 的
/// RegisterClassEx + HWND_MESSAGE + GetMessage 泵的手法)。所有 RegisterHotKey/UnregisterHotKey 都
/// 在该线程做(系统要求注册与注销同线程)。
///
/// <para>动作表(Id → 默认手势 → 执行体):</para>
/// <list type="bullet">
/// <item><b>ToggleBorderless</b> = Ctrl+Alt+B:前台窗口 IsTracked ? Restore : Apply 去边框(从 TrayIcon 迁来)。</item>
/// <item><b>ToggleCurtain</b> = Ctrl+Alt+D:经 UI 线程调 CurtainService.Toggle()。</item>
/// <item><b>SendToZone1/2/3</b> = Ctrl+Alt+1/2/3:前台窗口 → Layouts[0] 第 N 个 zone 在"窗口当前所在屏"
///       工作区的矩形(zone 比例 × rcWork,同 DragSnap/PlacementResolver 数学)→ SetWindowPos 普通移动。</item>
/// </list>
///
/// <para>绑定来源 <see cref="AppConfig.Hotkeys"/>(缺项补默认);<see cref="ConfigService.Changed"/> 时防抖重注册;
/// 注册失败(手势无效或被占用)记入 <see cref="GetStatuses"/> 可查的状态表。</para>
///
/// <para>动作的执行体跑在热键线程(WM_HOTKEY 回调),只做线程无关的 Win32(取前台窗口、SetWindowPos);
/// 需要碰 UI / 引擎托管状态的(开关幕布)经 <see cref="_ui"/> 切回 UI 线程。</para>
/// </summary>
public sealed class HotkeyService : IDisposable
{
    // ---- 动作 Id(与 Config.Hotkeys 字典键、SettingsPage 一致) ----
    public const string ActToggleBorderless = "ToggleBorderless";
    public const string ActToggleCurtain = "ToggleCurtain";
    public const string ActSendToZone1 = "SendToZone1";
    public const string ActSendToZone2 = "SendToZone2";
    public const string ActSendToZone3 = "SendToZone3";

    /// <summary>动作元数据:Id、中文名、默认手势、执行体。顺序即 SettingsPage 显示顺序。</summary>
    public sealed record ActionInfo(string Id, string DisplayName, string DefaultGesture);

    // 默认手势经本机实测 RegisterHotKey 验证可注册(2026-06,见 hotkey.log/诊断):
    //   旧 Ctrl+Alt+F 被系统/IME 占用(err 1409,会穿透到"在目录中查找"AD 对话框);
    //   旧 Win+Alt+1/2/3 被 Windows 任务栏跳转列表保留(恒 1409,且 Win+Ctrl/Shift+数字 同样被占)。
    // 改为同一 Ctrl+Alt 家族(与可用的 Ctrl+Alt+B 同族):B / D / 1 / 2 / 3,全部实测注册成功。
    private static readonly ActionInfo[] _actions =
    {
        new(ActToggleBorderless, "无边框开关(前台窗口)", "Ctrl+Alt+B"),
        new(ActToggleCurtain,    "专注模式开关",          "Ctrl+Alt+D"),
        new(ActSendToZone1,      "送入分区 1",            "Ctrl+Alt+1"),
        new(ActSendToZone2,      "送入分区 2",            "Ctrl+Alt+2"),
        new(ActSendToZone3,      "送入分区 3",            "Ctrl+Alt+3"),
    };

    /// <summary>对外只读动作表(SettingsPage 据此渲染每一行)。</summary>
    public static IReadOnlyList<ActionInfo> Actions => _actions;

    /// <summary>某动作的默认手势(SettingsPage "缺项回填"用)。</summary>
    public static string DefaultGesture(string actionId)
        => _actions.FirstOrDefault(a => a.Id == actionId)?.DefaultGesture ?? "";

    /// <summary>一条动作的当前注册状态(供设置页提示)。</summary>
    public sealed record HotkeyStatus(string ActionId, string Gesture, bool Registered, string? Error);

    private Func<AppConfig>? _getConfig;
    private DispatcherQueue? _ui;

    private Thread? _thread;
    private IntPtr _hwnd;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;

    // 委托保存为字段:WndProc 地址传给系统,局部会被 GC(同 TrayIcon 的坑)。
    private readonly WndProc _wndProc;

    private const string WindowClassName = "Reframe.HotkeyHostWindow";

    // 防抖:ConfigService.Changed 可能连发,合并成一次重注册。
    private const int DebounceMs = 250;
    private Timer? _debounce;
    private readonly object _gate = new();

    // ---- 注册态(仅热键线程读写;状态表对外暴露需上锁拷贝) ----
    // hotkeyId → (动作 Id, 执行体)。id 从 1 递增,每次重注册重排。
    private readonly Dictionary<int, RegEntry> _registered = new();
    private List<HotkeyStatus> _statuses = new();
    private readonly object _statusGate = new();

    private sealed record RegEntry(string ActionId, Action Execute);

    public HotkeyService() => _wndProc = WndProcImpl;

    /// <summary>启动:在 UI 线程调用(传入 UI DispatcherQueue 供切线程)。幂等。</summary>
    public void Start(DispatcherQueue ui, Func<AppConfig> getConfig)
    {
        if (_thread != null) return;
        _ui = ui;
        _getConfig = getConfig;

        _thread = new Thread(ThreadProc) { IsBackground = true, Name = "Reframe.Hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);

        // 首次注册 + 订阅配置变化重注册。
        PostRegisterAll();
        ConfigService.Instance.Changed += OnConfigChanged;
    }

    /// <summary>停止:解订阅、注销全部热键、退泵收尾。幂等。</summary>
    public void Stop()
    {
        if (_thread == null) return;
        try { ConfigService.Instance.Changed -= OnConfigChanged; } catch { /* ignore */ }

        lock (_gate) { _debounce?.Dispose(); _debounce = null; }

        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        try { _thread.Join(2000); } catch { /* ignore */ }
        _thread = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _ready.Dispose();
    }

    /// <summary>当前各动作的注册状态快照(设置页"应用后"据此显示成功/失败)。</summary>
    public IReadOnlyList<HotkeyStatus> GetStatuses()
    {
        lock (_statusGate) return new List<HotkeyStatus>(_statuses);
    }

    private void OnConfigChanged()
    {
        // 防抖:静默 250ms 后在热键线程重注册。
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => PostRegisterAll(), null, DebounceMs, Timeout.Infinite);
        }
    }

    // 把"重注册"投递到热键线程执行(注册必须与窗口同线程)。
    private void PostRegisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        PostMessage(_hwnd, WM_APP_REREGISTER, IntPtr.Zero, IntPtr.Zero);
    }

    private void ThreadProc()
    {
        _threadId = GetCurrentThreadId();
        RegisterWindowClass();

        _hwnd = CreateWindowEx(0, WindowClassName, "Reframe.Hotkey", 0,
            0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        _ready.Set();

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
        RegisterClassEx(ref wc); // 进程内唯一即可,重复返回 0 无妨
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
            {
                int id = (int)(wParam.ToInt64() & 0xFFFFFFFF);
                if (_registered.TryGetValue(id, out var e))
                {
                    try { e.Execute(); } catch { /* 单个动作失败不拖垮泵 */ }
                }
                return IntPtr.Zero;
            }

            case WM_APP_REREGISTER:
                ReRegisterAll();
                return IntPtr.Zero;

            case WM_CLOSE:
                UnregisterAll();
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ---- 注册 / 注销(均在热键线程) ----

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
            UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    private void ReRegisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterAll();

        var cfg = _getConfig?.Invoke();
        var statuses = new List<HotkeyStatus>(_actions.Length);
        int nextId = 1;

        foreach (var act in _actions)
        {
            // 取配置手势,缺项/空串回落默认。
            string gesture = act.DefaultGesture;
            if (cfg?.Hotkeys != null &&
                cfg.Hotkeys.TryGetValue(act.Id, out var g) && !string.IsNullOrWhiteSpace(g))
                gesture = g.Trim();

            if (!HotkeyGesture.TryParse(gesture, out uint mods, out uint vk))
            {
                statuses.Add(new HotkeyStatus(act.Id, gesture, false, "手势无效"));
                continue;
            }

            int id = nextId++;
            // 全局热键统一附加 NOREPEAT,避免长按连发。
            bool ok = RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok)
            {
                _registered[id] = new RegEntry(act.Id, BuildExecutor(act.Id));
                statuses.Add(new HotkeyStatus(act.Id, gesture, true, null));
                Log($"OK    {act.Id,-18} {gesture,-16} mods=0x{mods:X4} vk=0x{vk:X2}");
            }
            else
            {
                statuses.Add(new HotkeyStatus(act.Id, gesture, false, DescribeError(err)));
                Log($"FAIL  {act.Id,-18} {gesture,-16} mods=0x{mods:X4} vk=0x{vk:X2} err={err} ({DescribeError(err)})");
            }
        }

        // 任何注册失败:在仪表盘日志打一条可见提示(用户不必进设置页才知道)。
        var failed = statuses.Where(s => !s.Registered).ToList();
        if (failed.Count > 0)
        {
            try
            {
                var detail = string.Join("、", failed.Select(s => $"{s.Gesture}({s.Error})"));
                App.Engine?.LogExternal($"热键注册失败:{detail}。可在 设置 → 热键 改绑。");
            }
            catch { /* 引擎未就绪等:不让提示路径影响注册 */ }
        }

        lock (_statusGate) _statuses = statuses;
    }

    /// <summary>把 RegisterHotKey 的 Win32 错误码翻成人话(供状态表与 hotkey.log)。</summary>
    private static string DescribeError(int err) => err switch
    {
        0    => "未知",
        1409 => "已被占用",            // ERROR_HOTKEY_ALREADY_REGISTERED:被本进程或别的进程抢先注册
        1419 => "热键不可用",          // ERROR_HOTKEY_NOT_REGISTERED(注销路径)
        87   => "参数无效",            // ERROR_INVALID_PARAMETER
        _    => $"错误码 {err}",
    };

    // ---- 诊断日志:%LOCALAPPDATA%\Reframe\hotkey.log(每次注册一行,排查热键占用用) ----
    private static readonly object _logFileGate = new();

    private static void Log(string line)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "hotkey.log");
            lock (_logFileGate)
            {
                string stamp = DateTime.Now.ToString("HH:mm:ss");
                File.AppendAllText(path, $"[{stamp}] {line}{Environment.NewLine}");
            }
        }
        catch { /* 日志失败不能影响注册 */ }
    }

    // 把动作 Id 映射到执行体。zone 动作捕获其 0 基索引。
    private Action BuildExecutor(string actionId) => actionId switch
    {
        ActToggleBorderless => ToggleForegroundBorderless,
        ActToggleCurtain => () => _ui?.TryEnqueue(CurtainService.Toggle),
        ActSendToZone1 => () => SendForegroundToZone(0),
        ActSendToZone2 => () => SendForegroundToZone(1),
        ActSendToZone3 => () => SendForegroundToZone(2),
        _ => () => { },
    };

    // ---- 动作执行体 ----

    /// <summary>前台窗口去边框/还原(从 TrayIcon→App 迁来的原 Ctrl+Alt+B 行为)。</summary>
    private void ToggleForegroundBorderless()
    {
        IntPtr h = WindowActivation.GetForeground();
        if (h == IntPtr.Zero) return;
        if (WindowOps.IsTracked(h))
        {
            WindowOps.Restore(h);
        }
        else
        {
            // 只去边框、不动几何、不置顶;快照由 Apply 内部留存。
            var target = new PlacementResolver.Target(MakeBorderless: true, Rect: null, Topmost: false);
            WindowOps.Apply(h, in target);
        }
    }

    /// <summary>
    /// 把前台窗口送进 Layouts[0] 第 <paramref name="zoneIndex"/> 个 zone 在"窗口当前所在屏"工作区的矩形:
    /// 比例 × rcWork(同 DragSnap),SetWindowPos 普通移动(不去框、不接管、不置顶)。
    /// </summary>
    private void SendForegroundToZone(int zoneIndex)
    {
        IntPtr h = WindowActivation.GetForeground();
        if (h == IntPtr.Zero) return;

        var cfg = _getConfig?.Invoke();
        var layout = cfg?.Layouts.Count > 0 ? cfg.Layouts[0] : null;
        if (layout is null || zoneIndex < 0 || zoneIndex >= layout.Zones.Count) return;
        var z = layout.Zones[zoneIndex];

        // 窗口当前所在屏的工作区(rcWork)。
        IntPtr hMon = NativeMethods.MonitorFromWindow(h, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return;
        var w = mi.rcWork;
        int bw = w.Right - w.Left, bh = w.Bottom - w.Top;

        // zone 比例 × 工作区(同 PlacementResolver.ResolveRect 的 Zone 分支)。
        int left = w.Left + (int)Math.Round(z.X * bw);
        int top = w.Top + (int)Math.Round(z.Y * bh);
        int right = w.Left + (int)Math.Round((z.X + z.W) * bw);
        int bottom = w.Top + (int)Math.Round((z.Y + z.H) * bh);
        int cw = right - left, ch = bottom - top;
        if (cw <= 0 || ch <= 0) return;

        // 先从最小化还原,再移动(不动 Z 序、不抢焦点)。
        if (NativeMethods.IsIconic(h))
            NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);

        NativeMethods.SetWindowPos(h, IntPtr.Zero, left, top, cw, ch,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    // ====================== P/Invoke(本服务私有,不污染 NativeMethods) ======================

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP = 0x8000;
    private const uint WM_APP_REREGISTER = WM_APP + 1; // 自定义:请热键线程重注册

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    // RegisterHotKey 修饰符(与 HotkeyGesture 的 MOD_* 同值;NOREPEAT 注册时附加)。
    private const uint MOD_NOREPEAT = 0x4000;

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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
