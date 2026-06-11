using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Reframe.Services;

/// <summary>
/// Process-icon cache service (static, shared across pages). Icon lookup priority:
///   1. Profile.ExePath (passed in by the caller via <see cref="ByProfile"/>) → extract straight from
///      that exe. For anti-cheat-protected games whose MainModule can't be read (Zenless Zone Zero /
///      Genshin): a manual path is enough.
///   2. In-memory cache: process name (lowercase, no .exe) → ImageSource (a hit returns at zero cost;
///      failures are cached as null too, to avoid retrying).
///   3. SteamGridDB disk cache: %LOCALAPPDATA%\Reframe\icons\&lt;process&gt;.png (present once fetched
///      online; decode directly, no more network).
///   4. Persisted path map: %LOCALAPPDATA%\Reframe\iconpaths.json (process name → full exe path).
///      The path is learned and saved the first time the process is seen running; afterward the icon
///      can be extracted straight from the saved path even when the game isn't running.
///   5. Running process: MainModule.FileName, falling back to QueryFullProcessImageNameW (usually
///      still readable for protected processes).
///   6. SteamGridDB online (last resort, via <see cref="PrewarmFromSteamGridDbAsync"/>; taken only
///      when everything local failed and a key is configured).
/// Extraction: exe path → ExtractIconEx → HICON → WriteableBitmap; png file → BitmapImage. Pure
/// P/Invoke + WinRT. Everything is wrapped in try/catch; on any miss it returns null and the UI shows
/// the default glyph.
///
/// Threading: WriteableBitmap/BitmapImage must be created on the UI thread. The synchronous APIs
/// assume the caller is on the UI thread.
///   - <see cref="TryGetCached"/>: a pure in-memory hit, fetched synchronously on the UI thread (zero
///     IO), for "fetch fast first on a high-frequency refresh".
///   - <see cref="PrewarmByProcessName"/>: does the slow path resolution in the background (no bitmap);
///     a subsequent UI-thread ByProcessName then hits instantly.
/// The in-memory cache and the path map are guarded by the same lock, so the persisted part can be
/// read/written safely across threads.
/// </summary>
public static class IconCache
{
    private static readonly object _gate = new();

    // Process name (lowercase, no .exe) → resolved icon (may be null: tried but failed, don't retry).
    private static readonly Dictionary<string, ImageSource?> _mem = new(StringComparer.OrdinalIgnoreCase);

    // Process name (lowercase, no .exe) → full exe path. Persisted to iconpaths.json.
    private static Dictionary<string, string>? _paths;

    private static string PathsFile =>
        Path.Combine(ConfigStore.Dir, "iconpaths.json");

    // ---- Public entry points ----

    /// <summary>
    /// Synchronous fast path: in-memory cache only (zero IO). A hit (including a "known failure" cached
    /// as null) returns true. For high-frequency refreshes like the dashboard to fetch first: set the
    /// Icon directly on a hit without going async; only on a miss schedule background prewarm + backfill.
    /// Must be called on the UI thread (a hit's ImageSource was itself created on the UI thread).
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
    /// Get an icon for a profile: prefer Profile.ExePath (a manually specified icon source, which
    /// bypasses the anti-cheat "can't read MainModule" problem), otherwise fall back to the normal
    /// by-process-name path (MatchValue / MatchKind=Process). Returns null on a miss. Call on the UI thread.
    /// </summary>
    public static ImageSource? ByProfile(Core.Profile? profile)
    {
        if (profile is null) return null;

        // Use the process name as the cache key (different sources for the same process still share one entry). Fall back to the ExePath file name when there's no process name.
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

    /// <summary>Get an icon by process name. <paramref name="name"/> may or may not include .exe, case-insensitive. Returns null on a miss.</summary>
    public static ImageSource? ByProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = Normalize(name);

        lock (_gate)
        {
            if (_mem.TryGetValue(key, out var cached)) return cached;
        }

        // The SteamGridDB disk cache takes priority over process extraction: online game icons are higher quality and don't require the process to be running.
        ImageSource? icon = LoadPngFile(SteamGridDb.CachedIconFile(key));

        // Otherwise resolve the exe path: prefer a currently running instance (learning and saving the path along the way), otherwise fall back to the persisted path.
        if (icon is null)
        {
            string? path = ResolvePath(key);
            icon = path is null ? null : ExtractFromFile(path);
        }

        lock (_gate)
        {
            _mem[key] = icon; // cache failures too, to avoid re-extracting on every refresh
        }
        return icon;
    }

    /// <summary>
    /// Prewarm: do only the "potentially slow" path resolution (Process enumeration / MainModule read)
    /// in the background, without creating a bitmap. The caller should invoke this on a background
    /// thread, then call ByProcessName on the UI thread to do the fast bitmap construction. If it's
    /// already in the in-memory cache (failures count too), skip at zero cost.
    /// </summary>
    public static void PrewarmByProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string key = Normalize(name);
        lock (_gate)
        {
            if (_mem.ContainsKey(key)) return; // result is already decided, no resolution needed
        }
        ResolvePath(key); // side effect: if a path is learned it's saved, so a subsequent UI-thread ByProcessName hits
    }

    /// <summary>
    /// Last resort: go online via SteamGridDB only when everything local failed and a key is
    /// configured. On success it downloads the icon to the disk cache and clears this process name's
    /// "known failure" in-memory entry (so a subsequent ByProcessName can hit the disk cache and
    /// backfill). Returns true if a new icon was obtained (the caller should re-fetch on the UI thread
    /// and refresh). Must be awaited on a background thread (includes network IO).
    /// </summary>
    public static async Task<bool> PrewarmFromSteamGridDbAsync(string? apiKey, Core.Profile? profile)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || profile is null) return false;

        // No need to go online if ExePath is set (a local exe can produce the icon).
        if (!string.IsNullOrWhiteSpace(profile.ExePath) && File.Exists(profile.ExePath)) return false;

        string? procName = profile.MatchKind == Core.MatchKind.Process && !string.IsNullOrWhiteSpace(profile.MatchValue)
            ? profile.MatchValue
            : null;
        if (procName is null) return false;
        string key = Normalize(procName);

        // A non-null in-memory icon already exists → local can produce it, don't go online.
        lock (_gate)
        {
            if (_mem.TryGetValue(key, out var cached) && cached is not null) return false;
        }

        // A disk cache already exists → don't go online again, but clear the "known failure" in-memory entry so the next hop hits the disk.
        string cachedFile = SteamGridDb.CachedIconFile(key);
        if (!File.Exists(cachedFile))
        {
            // Search terms: prefer Profile.Name (SteamGridDB usually recognizes a non-English name), then fall back to camel-case-splitting the process name.
            var terms = new List<string>();
            if (!string.IsNullOrWhiteSpace(profile.Name)) terms.Add(profile.Name);
            terms.Add(SplitCamelCase(key)); // ZenlessZoneZero → "Zenless Zone Zero"

            string? file = await SteamGridDb.TryFetchIconAsync(apiKey!, key, terms).ConfigureAwait(false);
            if (file is null) return false;
        }

        // Clear the "known failure" placeholder so ByProcessName tries again (and will hit the disk cache).
        lock (_gate)
        {
            if (_mem.TryGetValue(key, out var cached) && cached is null)
                _mem.Remove(key);
        }
        return true;
    }

    /// <summary>Camel-case / run-together process name → a space-separated readable search term: ZenlessZoneZero → "Zenless Zone Zero".</summary>
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

    /// <summary>Get an icon by process ID: first get the process name and go through ByProcessName (to reuse the cache + learn the path). Returns null on a miss.</summary>
    public static ImageSource? ByProcessId(uint pid)
    {
        string? name = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName; // without .exe
        }
        catch { return null; }

        if (string.IsNullOrWhiteSpace(name)) return null;

        // Learn the path from this pid along the way (GetProcessById is more precise than GetProcessesByName).
        TryLearnPathFromProcessId(Normalize(name), pid);
        return ByProcessName(name);
    }

    /// <summary>
    /// Get the full exe path for a process ID (reusing the MainModule → QueryFullProcessImageNameW
    /// fallback chain). For the secondary path line shown in the "running windows" list. Returns null
    /// on a miss (process exited / no permission and the fallback failed too).
    /// </summary>
    public static string? TryResolveExePath(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return TryGetProcessImagePath(p);
        }
        catch { return null; }
    }

    // ---- Path resolution and learning ----

    private static string Normalize(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant();
    }

    /// <summary>Get the exe path: if running → read MainModule and save it; otherwise consult the persisted map.</summary>
    private static string? ResolvePath(string key)
    {
        // 1. Running instances: GetProcessesByName takes the name without .exe. Learn from any instance whose path is readable.
        //    Try MainModule first (fast, full info); anti-cheat / protected processes throw "access denied",
        //    so fall back to QueryFullProcessImageNameW (PROCESS_QUERY_LIMITED_INFORMATION usually still works for protected processes).
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
                catch { /* this instance isn't readable, try the next */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* enumeration failed: fall back to the persisted map */ }

        // 2. Persisted map: hits even when the game isn't running.
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
        // If the path is already known, don't re-read MainModule (a relatively expensive call).
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
        catch { /* not readable, never mind */ }
    }

    /// <summary>
    /// Get a process's exe path: MainModule.FileName first (fast), falling back to
    /// QueryFullProcessImageNameW (open the handle with PROCESS_QUERY_LIMITED_INFORMATION, which can
    /// usually still read the path for anti-cheat / protected processes). Swallows all exceptions;
    /// returns null when unreadable.
    /// </summary>
    private static string? TryGetProcessImagePath(Process p)
    {
        try
        {
            string? file = p.MainModule?.FileName; // protected processes throw "access denied"
            if (!string.IsNullOrEmpty(file)) return file;
        }
        catch { /* fall through to the QueryFullProcessImageName fallback */ }

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
        catch { /* the fallback failed too */ }
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

    // ---- Persistence (plain JsonSerializer, no source-gen) ----

    /// <summary>
    /// Returns the _paths reference itself (not a snapshot, to save a copy).
    /// Contract: any read/write of the returned dictionary by the caller MUST happen under the
    /// <see cref="_gate"/> lock — this class's three existing callers (ResolvePath /
    /// TryLearnPathFromProcessId / RememberPath) all access it under the lock. New callers must follow
    /// suit, otherwise they race RememberPath's concurrent write and SavePaths's snapshot copy. If you
    /// need to hold it outside the lock, copy it yourself.
    /// </summary>
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
        catch { /* if corrupt, treat as an empty map */ }

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
        catch { /* a failed disk write is not fatal: this session's in-memory cache is still valid */ }
    }

    // ---- Icon extraction: exe path → ExtractIconEx → HICON → WriteableBitmap ----
    // Everything is wrapped in try/catch: any failure falls back to null → the UI shows the default glyph.

    /// <summary>
    /// png file → BitmapImage (for the SteamGridDB disk cache). Missing file / read failure → null in
    /// all cases. Call on the UI thread. Reads the whole file's bytes into a memory stream
    /// synchronously and then SetSource, severing the timing coupling between "the cached object and
    /// the disk file": the old UriSource implementation decoded lazily (BitmapImage read the disk
    /// asynchronously later), so if the file was overwritten/deleted in the meantime, decoding could
    /// fail or yield a partial image. Here we ReadAllBytes first (the file is complete at this moment),
    /// after which the bitmap no longer depends on the disk.
    ///
    /// Deadlock trap (fixed): never synchronously wait on <see cref="BitmapImage.SetSourceAsync"/> via
    /// <c>.AsTask().GetAwaiter().GetResult()</c> on the UI thread — SetSourceAsync's decode-complete
    /// continuation must be posted back to the same UI DispatcherQueue, but the UI thread is blocked in
    /// Monitor.Wait on that very Task and never returns to pump the queue → self-deadlock (this page's
    /// left column fetches icons synchronously for all running windows as soon as it loads, which is
    /// guaranteed to fire whenever the disk cache has a matching png, freezing the whole UI). Use the
    /// <b>synchronous</b> <see cref="BitmapSource.SetSource"/> instead: it feeds the stream to the
    /// bitmap in place, with no await and no continuation post-back; the actual pixel decode is driven
    /// by the framework during the subsequent layout/render phase, blocking nothing and not depending
    /// on this thread pumping messages. The MemoryStream is wrapped via AsRandomAccessStream (purely
    /// synchronous, touches no disk); the bytes are already entirely in memory, so it's no longer
    /// coupled to the disk file.
    /// </summary>
    private static ImageSource? LoadPngFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] bytes;
            // Open with explicit FileShare.Read and read it all in one go; 0 bytes (a half-downloaded / placeholder file) is treated as invalid.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long len = fs.Length;
                if (len <= 0) return null;
                bytes = new byte[len];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = fs.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != bytes.Length) return null; // incomplete read: not a valid image
            }

            // The bytes are now entirely in memory: wrap a MemoryStream synchronously as an
            // IRandomAccessStream (touches no disk) and hand it to SetSource (synchronous). Note: this
            // MemoryStream's lifetime only needs to cover the SetSource call itself — SetSource holds
            // it internally until decoding completes, so don't `using`-dispose it early.
            var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            // Synchronous SetSource: no await / no continuation post-back, never self-deadlocks on the UI thread. Decoding happens in the subsequent render phase.
            bmp.SetSource(ms.AsRandomAccessStream());
            return bmp;
        }
        catch { return null; }
    }

    private static ImageSource? ExtractFromFile(string path)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            // Take the large icon; on failure fall back to the small icon.
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

    /// <summary>HICON → 32bpp top-down BGRA pixels → WriteableBitmap (itself an ImageSource, bound directly to Image.Source).</summary>
    private static ImageSource? IconToBitmap(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out ICONINFO ii)) return null;

        IntPtr hbmColor = ii.hbmColor, hbmMask = ii.hbmMask;
        try
        {
            // A monochrome icon (mask only, no color bitmap) can't be reliably reconstructed → give up, fall back to the default glyph.
            if (hbmColor == IntPtr.Zero) return null;
            if (GetObject(hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0) return null;

            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            var bi = new BITMAPINFO
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,            // negative = top-down, matching WriteableBitmap's BGRA row order
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

    // ---- P/Invoke (self-contained; deliberately leaves NativeMethods.cs alone: that belongs to another agent) ----

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

    // QueryFullProcessImageNameW fallback: often still reads the exe path for anti-cheat / protected processes.
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

    // GetDIBits needs a trailing palette area; 32bpp BI_RGB doesn't use it, but the struct must be large enough.
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
