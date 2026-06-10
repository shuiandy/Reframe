using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Reframe.Services;

/// <summary>
/// 进程图标缓存服务(静态、跨页面共享)。三层:
///   1. 内存缓存:进程名(小写,不含 .exe) → ImageSource(命中则零成本返回;失败也缓存为 null,不反复重试)。
///   2. 路径映射持久化:%LOCALAPPDATA%\Reframe\iconpaths.json(进程名 → exe 完整路径)。
///      首次看到该进程在运行时学到路径并存盘;以后即使游戏没在跑,也能从存好的路径直接提取图标。
///   3. 提取实现:exe 路径 → ExtractIconEx → HICON → WriteableBitmap,纯 P/Invoke。
/// 全程 try/catch,取不到一律返回 null,UI 显示默认字形。
///
/// 线程:WriteableBitmap 必须在 UI 线程创建。两种 API 都假定调用方在 UI 线程
/// (位图构建本身很快;调用方若担心阻塞,可自行把 By* 调度回 UI 线程后用结果回填)。
/// 内存缓存与路径表用同一把锁保护,可安全跨线程读写持久化部分。
/// </summary>
public static class IconCache
{
    private static readonly object _gate = new();

    // 进程名(小写,不含 .exe)→ 已解析的图标(可能为 null:表示尝试过但失败,不再重试)。
    private static readonly Dictionary<string, ImageSource?> _mem = new(StringComparer.OrdinalIgnoreCase);

    // 进程名(小写,不含 .exe)→ exe 完整路径。持久化到 iconpaths.json。
    private static Dictionary<string, string>? _paths;

    private static string PathsFile =>
        Path.Combine(ConfigStore.Dir, "iconpaths.json");

    // ---- 对外入口 ----

    /// <summary>按进程名取图标。name 可带或不带 .exe,大小写不敏感。取不到返回 null。</summary>
    public static ImageSource? ByProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = Normalize(name);

        lock (_gate)
        {
            if (_mem.TryGetValue(key, out var cached)) return cached;
        }

        // 解析 exe 路径:优先用当前运行实例(顺便学习并存盘),否则回落到持久化路径。
        string? path = ResolvePath(key);
        ImageSource? icon = path is null ? null : ExtractFromFile(path);

        lock (_gate)
        {
            _mem[key] = icon; // 失败也缓存,避免每次刷新都重试提取
        }
        return icon;
    }

    /// <summary>
    /// 预热:只在后台做"可能慢"的路径解析(Process 枚举 / MainModule 读取),不创建位图。
    /// 调用方应在后台线程调用此方法,再在 UI 线程调 ByProcessName 完成快速的位图构建。
    /// 已在内存缓存中(连失败也算)则直接跳过,零成本。
    /// </summary>
    public static void PrewarmByProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string key = Normalize(name);
        lock (_gate)
        {
            if (_mem.ContainsKey(key)) return; // 结果已定,无需解析
        }
        ResolvePath(key); // 副作用:学到路径就存盘,供随后 UI 线程的 ByProcessName 命中
    }

    /// <summary>按进程 ID 取图标:先拿进程名走 ByProcessName(以复用缓存+学习路径)。取不到返回 null。</summary>
    public static ImageSource? ByProcessId(uint pid)
    {
        string? name = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName; // 不含 .exe
        }
        catch { return null; }

        if (string.IsNullOrWhiteSpace(name)) return null;

        // 顺便用这个 pid 学习路径(GetProcessById 比 GetProcessesByName 精确)。
        TryLearnPathFromProcessId(Normalize(name), pid);
        return ByProcessName(name);
    }

    // ---- 路径解析与学习 ----

    private static string Normalize(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant();
    }

    /// <summary>取 exe 路径:正在运行→读 MainModule 并存盘;否则查持久化表。</summary>
    private static string? ResolvePath(string key)
    {
        // 1. 在跑的实例:GetProcessesByName 不含 .exe。任一实例可读到路径即学习。
        try
        {
            foreach (var p in Process.GetProcessesByName(key))
            {
                try
                {
                    string? file = p.MainModule?.FileName; // 受保护/提权进程会抛(拒绝访问)
                    if (!string.IsNullOrEmpty(file) && File.Exists(file))
                    {
                        RememberPath(key, file);
                        return file;
                    }
                }
                catch { /* 这个实例读不到,试下一个 */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* 枚举失败:回落持久化表 */ }

        // 2. 持久化表:游戏没在跑也能命中。
        var map = LoadPaths();
        lock (_gate)
        {
            if (map.TryGetValue(key, out var saved) && File.Exists(saved))
                return saved;
        }
        return null;
    }

    private static void TryLearnPathFromProcessId(string key, uint pid)
    {
        // 已知路径就别重复读 MainModule(那是相对昂贵的调用)。
        var map = LoadPaths();
        lock (_gate)
        {
            if (map.ContainsKey(key)) return;
        }
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? file = p.MainModule?.FileName;
            if (!string.IsNullOrEmpty(file) && File.Exists(file))
                RememberPath(key, file);
        }
        catch { /* 读不到就算了 */ }
    }

    private static void RememberPath(string key, string file)
    {
        var map = LoadPaths();
        bool changed;
        lock (_gate)
        {
            changed = !map.TryGetValue(key, out var old) ||
                      !string.Equals(old, file, StringComparison.OrdinalIgnoreCase);
            if (changed) map[key] = file;
        }
        if (changed) SavePaths();
    }

    // ---- 持久化(简单 JsonSerializer,无源生成) ----

    private static Dictionary<string, string> LoadPaths()
    {
        lock (_gate)
        {
            if (_paths is not null) return _paths;
        }

        Dictionary<string, string>? loaded = null;
        try
        {
            if (File.Exists(PathsFile))
            {
                string json = File.ReadAllText(PathsFile);
                loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }
        }
        catch { /* 损坏就当空表 */ }

        lock (_gate)
        {
            _paths ??= loaded is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            return _paths;
        }
    }

    private static void SavePaths()
    {
        Dictionary<string, string> snapshot;
        lock (_gate)
        {
            if (_paths is null) return;
            snapshot = new Dictionary<string, string>(_paths, StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            Directory.CreateDirectory(ConfigStore.Dir);
            string json = JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PathsFile, json);
        }
        catch { /* 写盘失败不致命:本次会话内存缓存仍有效 */ }
    }

    // ---- 图标提取:exe 路径 → ExtractIconEx → HICON → WriteableBitmap ----
    // 全程 try/catch:任何失败一律回落 null → UI 显示默认字形。

    private static ImageSource? ExtractFromFile(string path)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            // 取大图标;失败再退而求其次取小图标。
            if (ExtractIconEx(path, 0, out hIcon, out IntPtr hSmall, 1) <= 0 || hIcon == IntPtr.Zero)
            {
                if (hSmall != IntPtr.Zero) { hIcon = hSmall; hSmall = IntPtr.Zero; }
                else return null;
            }
            else if (hSmall != IntPtr.Zero)
            {
                DestroyIcon(hSmall);
            }

            return IconToBitmap(hIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
    }

    /// <summary>HICON → 32bpp top-down BGRA 像素 → WriteableBitmap(本身即 ImageSource,直接绑定 Image.Source)。</summary>
    private static ImageSource? IconToBitmap(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out ICONINFO ii)) return null;

        IntPtr hbmColor = ii.hbmColor, hbmMask = ii.hbmMask;
        try
        {
            // 单色图标(只有 mask、无彩色位图)无法可靠重建 → 放弃,回落默认字形。
            if (hbmColor == IntPtr.Zero) return null;
            if (GetObject(hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0) return null;

            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            var bi = new BITMAPINFO
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,            // 负数 = top-down,与 WriteableBitmap 的 BGRA 行序一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,        // BI_RGB
                bmiColors = new byte[256 * 4],
            };

            var bytes = new byte[w * 4 * h];
            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;
            try
            {
                var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    int scan = GetDIBits(hdc, hbmColor, 0, (uint)h, handle.AddrOfPinnedObject(), ref bi, 0);
                    if (scan == 0) return null;
                }
                finally { handle.Free(); }
            }
            finally { ReleaseDC(IntPtr.Zero, hdc); }

            var wb = new WriteableBitmap(w, h);
            using (var s = wb.PixelBuffer.AsStream())
                s.Write(bytes, 0, bytes.Length);
            return wb;
        }
        finally
        {
            if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
            if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
        }
    }

    // ---- P/Invoke(自带,刻意不动 NativeMethods.cs:那归别的 agent) ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        IntPtr lpvBits, ref BITMAPINFO lpbi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    // GetDIBits 需要紧跟一段调色板空间;32bpp BI_RGB 用不到,但结构体须够大。
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256 * 4)]
        public byte[] bmiColors;
    }
}
