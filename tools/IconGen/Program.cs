using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Reframe 应用图标生成器(一次性工具)。
// 隐喻:深色圆角方块底 + 内部两个分区(左 2/3 亮块 + 右 1/3 次亮块,中间细缝)。
// 多尺寸 PNG 帧手工打包成 .ico(PNG-compressed entry,Vista+ 原生支持,256px 清晰)。

static class Program
{
    // 与应用 accent 协调的配色(暗底亮分区)。
    static readonly Color Base = Color.FromArgb(0xFF, 0x1B, 0x1F, 0x26);   // 深石墨底
    static readonly Color BaseEdge = Color.FromArgb(0xFF, 0x2C, 0x32, 0x3C); // 底部细描边
    static readonly Color ZoneMain = Color.FromArgb(0xFF, 0x4C, 0x9A, 0xFF); // 左主区:亮蓝(accent)
    static readonly Color ZoneSide = Color.FromArgb(0xFF, 0x6E, 0x77, 0x88); // 右副区:次亮灰蓝

    static int Main()
    {
        int[] sizes = { 16, 24, 32, 48, 256 };
        string outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Assets");
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        string icoPath = Path.Combine(outDir, "reframe.ico");

        var frames = new List<byte[]>();
        foreach (int s in sizes)
        {
            using var bmp = Render(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            frames.Add(ms.ToArray());
        }

        WriteIco(icoPath, sizes, frames);
        Console.WriteLine($"Wrote {icoPath} ({sizes.Length} frames: {string.Join(",", sizes)})");
        return 0;
    }

    static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // 几何按尺寸比例化,小尺寸缝隙最少 1px 才看得见。
        float f = size;
        float outerR = MathF.Max(2f, f * 0.22f);          // 底块圆角
        float pad = MathF.Max(1f, f * 0.085f);            // 底块外边距
        var baseRect = new RectangleF(pad, pad, f - pad * 2, f - pad * 2);

        using (var basePath = Rounded(baseRect, outerR))
        using (var baseBrush = new SolidBrush(Base))
        {
            g.FillPath(baseBrush, basePath);
            if (size >= 32)
                using (var pen = new Pen(BaseEdge, MathF.Max(1f, f * 0.012f)))
                    g.DrawPath(pen, basePath);
        }

        // 内部两分区:总内容区 = 底块再内缩;左占 ~63%,右占 ~30%,中缝 ~7%。
        float ip = MathF.Max(1f, f * 0.10f);               // 分区相对底块的内缩
        var content = new RectangleF(
            baseRect.X + ip, baseRect.Y + ip,
            baseRect.Width - ip * 2, baseRect.Height - ip * 2);

        float gap = MathF.Max(1f, f * 0.05f);              // 中间细缝
        float leftW = (content.Width - gap) * 0.66f;
        float rightW = content.Width - gap - leftW;
        float zoneR = MathF.Max(1.5f, f * 0.09f);

        var leftRect = new RectangleF(content.X, content.Y, leftW, content.Height);
        var rightRect = new RectangleF(content.X + leftW + gap, content.Y, rightW, content.Height);

        // 左主区:竖向轻渐变,增加立体感(小尺寸退化为纯色亦可)。
        using (var lp = Rounded(leftRect, zoneR))
        {
            if (size >= 32)
            {
                using var lb = new LinearGradientBrush(
                    leftRect, Lighten(ZoneMain, 0.12f), Darken(ZoneMain, 0.10f), 90f);
                g.FillPath(lb, lp);
            }
            else
            {
                using var lb = new SolidBrush(ZoneMain);
                g.FillPath(lb, lp);
            }
        }

        using (var rp = Rounded(rightRect, zoneR))
        using (var rb = new SolidBrush(ZoneSide))
            g.FillPath(rb, rp);

        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        d = MathF.Min(d, MathF.Min(r.Width, r.Height)); // 半径不超过半边长
        var p = new GraphicsPath();
        if (d <= 0.5f) { p.AddRectangle(r); p.CloseFigure(); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Color Lighten(Color c, float amt) => Color.FromArgb(c.A,
        (int)Math.Clamp(c.R + 255 * amt, 0, 255),
        (int)Math.Clamp(c.G + 255 * amt, 0, 255),
        (int)Math.Clamp(c.B + 255 * amt, 0, 255));

    static Color Darken(Color c, float amt) => Color.FromArgb(c.A,
        (int)Math.Clamp(c.R - 255 * amt, 0, 255),
        (int)Math.Clamp(c.G - 255 * amt, 0, 255),
        (int)Math.Clamp(c.B - 255 * amt, 0, 255));

    // ICO 容器:6 字节 ICONDIR + 每帧 16 字节 ICONDIRENTRY,随后是 PNG 数据。
    static void WriteIco(string path, int[] sizes, List<byte[]> frames)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write((ushort)0);              // reserved
        bw.Write((ushort)1);              // type = icon
        bw.Write((ushort)frames.Count);   // count

        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int s = sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s)); // width  (0 表示 256)
            bw.Write((byte)(s >= 256 ? 0 : s)); // height (0 表示 256)
            bw.Write((byte)0);                  // color count
            bw.Write((byte)0);                  // reserved
            bw.Write((ushort)1);                // planes
            bw.Write((ushort)32);               // bpp
            bw.Write((uint)frames[i].Length);   // bytes in resource
            bw.Write((uint)offset);             // image offset
            offset += frames[i].Length;
        }
        foreach (var f in frames) bw.Write(f);
    }
}
