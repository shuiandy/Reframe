using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Reframe.Core;

namespace Reframe.UI;

/// <summary>
/// 选取对话框的一行:运行中的某个顶层窗口。x:Bind 需要顶层 public 类(参考 ProfileRow)。
/// 进程图标尽力而为:拿不到就回落到默认字形(FallbackIcon 显隐互斥)。
/// </summary>
public sealed class WindowRow
{
    public required WindowInfo Window { get; init; }
    public string Title { get; init; } = "";
    public string ProcessLabel { get; init; } = "";   // 次要灰字:进程名.exe
    public string SizeLabel { get; init; } = "";       // 如 1280×860
    public ImageSource? Icon { get; init; }            // 取得到才有,否则 null → 显示回落字形

    public Visibility RealIconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackIconVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class WindowPickerDialog : ContentDialog
{
    private List<WindowRow> _all = new();

    /// <summary>点「创建」后,用户选中的窗口;取消则为 null。由调用方据此建 Profile。</summary>
    public WindowInfo? SelectedWindow { get; private set; }

    public WindowPickerDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadWindows();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadWindows();

    private void LoadWindows()
    {
        var rows = new List<WindowRow>();
        foreach (var w in WindowScanner.EnumerateTopLevel())
        {
            // 过滤掉 Reframe 自己(ProcessName 不含 .exe、小写)
            if (string.Equals(w.ProcessName, "reframe", StringComparison.OrdinalIgnoreCase))
                continue;

            rows.Add(new WindowRow
            {
                Window = w,
                Title = w.Title,
                ProcessLabel = string.IsNullOrEmpty(w.ProcessName) ? "(未知进程)" : w.ProcessName + ".exe",
                SizeLabel = SizeOf(w.Handle),
                Icon = TryLoadIcon(w.ProcessId),
            });
        }

        // 标题排序,稳定可预期
        rows.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        _all = rows;
        ApplyFilter(SearchBox.Text);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    private void ApplyFilter(string? query)
    {
        IEnumerable<WindowRow> view = _all;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            view = _all.Where(r =>
                r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.ProcessLabel.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = view.ToList();
        WindowList.ItemsSource = list;
        bool empty = list.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        WindowList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = WindowList.SelectedItem is not null;
    }

    private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => IsPrimaryButtonEnabled = WindowList.SelectedItem is not null;

    private void OnCreateClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (WindowList.SelectedItem is WindowRow row)
            SelectedWindow = row.Window;
        else
            args.Cancel = true; // 没选中不应关闭
    }

    private static string SizeOf(IntPtr hwnd)
    {
        try
        {
            if (Interop.NativeMethods.GetWindowRect(hwnd, out var r))
            {
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w > 0 && h > 0) return $"{w}×{h}";
            }
        }
        catch { /* 忽略,显示空 */ }
        return "";
    }

    // ---- 进程图标提取:Process → MainModule.FileName → ExtractIcon → BitmapImage ----
    // 全程 try/catch:提权进程 / 受保护进程 的 MainModule 会抛 Win32Exception(拒绝访问),
    // 32 位访问 64 位进程也会抛;任何失败一律回落 null → UI 显示默认字形。

    private static ImageSource? TryLoadIcon(uint pid)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            string? path = null;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                path = p.MainModule?.FileName; // 这一行最常因权限抛异常
            }
            catch { return null; }

            if (string.IsNullOrEmpty(path)) return null;

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

    // ---- 局部 P/Invoke(刻意不动 NativeMethods.cs:M4 该文件归 Agent-DragSnap)----

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
        // 256 项 RGBQUAD 占位,确保 GetDIBits 写入调色板时不越界(32bpp 实际不写)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256 * 4)]
        public byte[] bmiColors;
    }
}
