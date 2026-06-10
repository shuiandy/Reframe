using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Reframe.Services;

/// <summary>
/// 进程图标缓存服务(静态、跨页面共享)。取图标优先级:
///   1. Profile.ExePath(调用方经 <see cref="ByProfile"/> 传入)→ 直接从该 exe 提取。
///      用于反作弊保护、读不到 MainModule 的游戏(绝区零/原神),手动指定即可。
///   2. 内存缓存:进程名(小写,不含 .exe) → ImageSource(命中则零成本返回;失败也缓存为 null,不反复重试)。
///   3. SteamGridDB 磁盘缓存:%LOCALAPPDATA%\Reframe\icons\&lt;进程名&gt;.png(联网取过就有,直接解码,不再联网)。
///   4. 路径映射持久化:%LOCALAPPDATA%\Reframe\iconpaths.json(进程名 → exe 完整路径)。
///      首次看到该进程在运行时学到路径并存盘;以后即使游戏没在跑,也能从存好的路径直接提取图标。
///   5. 运行中进程:MainModule.FileName,失败再用 QueryFullProcessImageNameW 兜底(受保护进程通常仍可读)。
///   6. SteamGridDB 在线(最后兜底,经 <see cref="PrewarmFromSteamGridDbAsync"/>,本地全失败且配了 key 才走)。
/// 提取实现:exe 路径 → ExtractIconEx → HICON → WriteableBitmap;png 文件 → BitmapImage。纯 P/Invoke + WinRT。
/// 全程 try/catch,取不到一律返回 null,UI 显示默认字形。
///
/// 线程:WriteableBitmap/BitmapImage 必须在 UI 线程创建。同步 API 假定调用方在 UI 线程。
///   - <see cref="TryGetCached"/>:纯内存命中,UI 线程同步直取(零 IO),给"高频刷新先快取"用。
///   - <see cref="PrewarmByProcessName"/>:后台做慢的路径解析(不建位图);随后 UI 线程 ByProcessName 命中即瞬时。
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

    /// <summary>
    /// 同步快路径:仅查内存缓存(零 IO)。命中(含命中为 null 的"已知失败")返回 true。
    /// 给仪表盘这类高频刷新先取,命中就直接设 Icon、不走异步;未命中才安排后台预热+回填。
    /// 必须在 UI 线程调用(命中的 ImageSource 本就在 UI 线程创建)。
    /// </summary>
    public static bool TryGetCached(string? name, out ImageSource? icon)
    {
        icon = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string key = Normalize(name);
        lock (_gate)
        {
            return _mem.TryGetValue(key, out icon);
        }
    }

    /// <summary>
    /// 按 Profile 取图标:优先用 Profile.ExePath(手动指定的图标来源,绕过反作弊读不到 MainModule 的问题),
    /// 否则回落到按进程名(MatchValue / MatchKind=Process)的常规链路。取不到返回 null。须在 UI 线程调用。
    /// </summary>
    public static ImageSource? ByProfile(Core.Profile? profile)
    {
        if (profile is null) return null;

        // 用进程名作缓存 key(同一进程不同来源仍共享一条缓存)。无进程名时用 ExePath 文件名兜底。
        string? procName = profile.MatchKind == Core.MatchKind.Process && !string.IsNullOrWhiteSpace(profile.MatchValue)
            ? profile.MatchValue
            : null;

        if (!string.IsNullOrWhiteSpace(profile.ExePath))
        {
            string key = procName is not null ? Normalize(procName) : Normalize(Path.GetFileName(profile.ExePath));
            lock (_gate)
            {
                if (_mem.TryGetValue(key, out var cached) && cached is not null) return cached;
            }
            if (File.Exists(profile.ExePath))
            {
                var icon = ExtractFromFile(profile.ExePath);
                if (icon is not null)
                {
                    lock (_gate) { _mem[key] = icon; }
                    return icon;
                }
            }
        }

        return procName is null ? null : ByProcessName(procName);
    }

    /// <summary>按进程名取图标。name 可带或不带 .exe,大小写不敏感。取不到返回 null。</summary>
    public static ImageSource? ByProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = Normalize(name);

        lock (_gate)
        {
            if (_mem.TryGetValue(key, out var cached)) return cached;
        }

        // SteamGridDB 磁盘缓存优先于进程提取:联网取过的游戏图标质量更高,且无需进程在跑。
        ImageSource? icon = LoadPngFile(SteamGridDb.CachedIconFile(key));

        // 否则解析 exe 路径:优先用当前运行实例(顺便学习并存盘),否则回落到持久化路径。
        if (icon is null)
        {
            string? path = ResolvePath(key);
            icon = path is null ? null : ExtractFromFile(path);
        }

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

    /// <summary>
    /// 最后兜底:本地全失败且配了 key 才联网走 SteamGridDB。成功则下载图标到磁盘缓存,
    /// 并清掉该进程名"已知失败"的内存条目(使随后的 ByProcessName 能从磁盘缓存命中并回填)。
    /// 返回 true 表示拿到了新图标(调用方应在 UI 线程重取并刷新)。须在后台线程 await(含网络 IO)。
    /// </summary>
    public static async Task<bool> PrewarmFromSteamGridDbAsync(string? apiKey, Core.Profile? profile)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || profile is null) return false;

        // 已配 ExePath 的不必联网(本地 exe 就能出图)。
        if (!string.IsNullOrWhiteSpace(profile.ExePath) && File.Exists(profile.ExePath)) return false;

        string? procName = profile.MatchKind == Core.MatchKind.Process && !string.IsNullOrWhiteSpace(profile.MatchValue)
            ? profile.MatchValue
            : null;
        if (procName is null) return false;
        string key = Normalize(procName);

        // 已有非空内存图标 → 本地已能出图,不联网。
        lock (_gate)
        {
            if (_mem.TryGetValue(key, out var cached) && cached is not null) return false;
        }

        // 已有磁盘缓存 → 不重复联网,但需要清掉"已知失败"的内存条目让下一跳从磁盘命中。
        string cachedFile = SteamGridDb.CachedIconFile(key);
        if (!File.Exists(cachedFile))
        {
            // 搜索词:Profile.Name 优先(中文名 SteamGridDB 多半识别),失败再用进程名驼峰拆词。
            var terms = new List<string>();
            if (!string.IsNullOrWhiteSpace(profile.Name)) terms.Add(profile.Name);
            terms.Add(SplitCamelCase(key)); // ZenlessZoneZero → "Zenless Zone Zero"

            string? file = await SteamGridDb.TryFetchIconAsync(apiKey!, key, terms).ConfigureAwait(false);
            if (file is null) return false;
        }

        // 清掉"已知失败"占位,使 ByProcessName 重新尝试(会命中磁盘缓存)。
        lock (_gate)
        {
            if (_mem.TryGetValue(key, out var cached) && cached is null)
                _mem.Remove(key);
        }
        return true;
    }

    /// <summary>驼峰/连写进程名 → 以空格分隔的可读搜索词:ZenlessZoneZero → "Zenless Zone Zero"。</summary>
    internal static string SplitCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
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
        //    先试 MainModule(快、含完整信息);反作弊/受保护进程会抛"拒绝访问",
        //    再用 QueryFullProcessImageNameW 兜底(PROCESS_QUERY_LIMITED_INFORMATION 对受保护进程通常仍可用)。
        try
        {
            foreach (var p in Process.GetProcessesByName(key))
            {
                try
                {
                    string? file = TryGetProcessImagePath(p);
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
            string? file = TryGetProcessImagePath(p);
            if (!string.IsNullOrEmpty(file) && File.Exists(file))
                RememberPath(key, file);
        }
        catch { /* 读不到就算了 */ }
    }

    /// <summary>
    /// 取进程的 exe 路径:先 MainModule.FileName(快),失败再 QueryFullProcessImageNameW 兜底
    /// (用 PROCESS_QUERY_LIMITED_INFORMATION 打开句柄,对反作弊/受保护进程通常仍可读出路径)。
    /// 全程吞异常,读不到返回 null。
    /// </summary>
    private static string? TryGetProcessImagePath(Process p)
    {
        try
        {
            string? file = p.MainModule?.FileName; // 受保护进程会抛"拒绝访问"
            if (!string.IsNullOrEmpty(file)) return file;
        }
        catch { /* 落到 QueryFullProcessImageName 兜底 */ }

        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
            if (h == IntPtr.Zero) return null;
            var sb = new System.Text.StringBuilder(1024);
            int cap = sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref cap) && cap > 0)
                return sb.ToString(0, cap);
        }
        catch { /* 兜底也失败 */ }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
        return null;
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

    /// <summary>png 文件 → BitmapImage(SteamGridDB 磁盘缓存用)。缺文件/解码失败一律 null。须在 UI 线程调用。</summary>
    private static ImageSource? LoadPngFile(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
            var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            bmp.UriSource = new Uri(path);
            return bmp;
        }
        catch { return null; }
    }

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

    // QueryFullProcessImageNameW 兜底:对反作弊/受保护进程也常能读出 exe 路径。
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref int lpdwSize);

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
