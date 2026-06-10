using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>匹配:窗口 ↔ profile。</summary>
public static class MatchEngine
{
    public static bool Matches(WindowInfo w, Profile p)
    {
        if (!p.Enabled || string.IsNullOrWhiteSpace(p.MatchValue)) return false;
        return p.MatchKind switch
        {
            MatchKind.Process    => string.Equals(StripExe(p.MatchValue), w.ProcessName, StringComparison.OrdinalIgnoreCase),
            MatchKind.Title      => w.Title.Contains(p.MatchValue, StringComparison.OrdinalIgnoreCase),
            MatchKind.TitleRegex => SafeRegex(p.MatchValue, w.Title),
            _ => false
        };
    }

    private static string StripExe(string s)
        => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;

    private static bool SafeRegex(string pattern, string input)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase); }
        catch { return false; }
    }
}

/// <summary>解析:窗口当前所在显示器 + profile 规则表 → 目标几何。</summary>
public static class PlacementResolver
{
    /// <summary>
    /// 解析结果。Rect 为 null 表示不动几何;Topmost 由 WindowOps 转成 HWND_TOPMOST/NOTOPMOST。
    /// </summary>
    public readonly record struct Target(bool MakeBorderless, NativeMethods.RECT? Rect, bool Topmost);

    /// <summary>
    /// 取屏幕信息 + 窗口当前矩形,委托给纯函数 <see cref="ResolveRect"/>。
    /// 这是唯一碰 Win32 的入口;几何计算全部在 ResolveRect 里(可单测)。
    /// </summary>
    public static Target Resolve(WindowInfo w, Profile p, AppConfig cfg)
    {
        IntPtr hMon = NativeMethods.MonitorFromWindow(w.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi))
            return new Target(p.Borderless, null, p.Topmost);

        // GetWindowRect 失败(句柄刚失效等):不要拿零矩形去算 MoveOnly/letterbox(会产出垃圾矩形,
        // 进而被 ClipCursor 夹住)。失败时只保留去边框/置顶意图,几何返回 null(不动)。
        if (!NativeMethods.GetWindowRect(w.Handle, out var cur))
            return new Target(p.Borderless, null, p.Topmost);

        var rect = ResolveRect(mi.rcMonitor, mi.rcWork, cur, p, cfg);
        return new Target(p.Borderless, rect, p.Topmost);
    }

    /// <summary>
    /// 纯几何核(不碰 Win32,单元测试靶点)。返回目标矩形(物理像素,虚拟桌面坐标),
    /// null = 该屏无命中规则或规则不产出矩形,调用方据此"不动几何"。
    /// </summary>
    /// <param name="rcMonitor">窗口所在屏整块矩形(含任务栏)。</param>
    /// <param name="rcWork">该屏工作区(不含任务栏)。</param>
    /// <param name="currentWindowRect">窗口当前矩形;仅 KeepAspectRatio 用其宽高比做 letterbox。</param>
    /// <param name="p">profile(规则表、Offsets、KeepAspectRatio)。</param>
    /// <param name="cfg">用于按 LayoutId/ZoneId 查 Zone 比例。</param>
    public static NativeMethods.RECT? ResolveRect(
        NativeMethods.RECT rcMonitor, NativeMethods.RECT rcWork,
        NativeMethods.RECT currentWindowRect, Profile p, AppConfig cfg)
    {
        int monW = rcMonitor.Right - rcMonitor.Left;
        int monH = rcMonitor.Bottom - rcMonitor.Top;

        // 自上而下,第一条 Monitor 命中的规则生效(按整屏分辨率匹配)
        PlacementRule? rule = null;
        foreach (var r in p.Rules)
        {
            if (r.Monitor.Matches(monW, monH)) { rule = r; break; }
        }
        if (rule is null)
            return null;

        var basis = rule.UseWorkArea ? rcWork : rcMonitor;
        int bw = basis.Right - basis.Left, bh = basis.Bottom - basis.Top;

        NativeMethods.RECT? target = rule.Kind switch
        {
            PlacementKind.Fullscreen => basis,

            PlacementKind.Zone when FindZone(cfg, rule) is { } z => ZoneToRect(z, basis),

            PlacementKind.CustomRect when rule.CustomRect is { } c => new NativeMethods.RECT
            {
                Left   = basis.Left + c.X,
                Top    = basis.Top  + c.Y,
                Right  = basis.Left + c.X + c.W,
                Bottom = basis.Top  + c.Y + c.H
            },

            _ => (NativeMethods.RECT?)null
        };

        if (target is not { } t) return null;

        // 四边偏移
        var o = p.Offsets;
        t = new NativeMethods.RECT
        {
            Left = t.Left + o.Left,
            Top = t.Top + o.Top,
            Right = t.Right + o.Right,
            Bottom = t.Bottom + o.Bottom
        };

        // 当前窗口矩形宽/高非法(GetWindowRect 失败给了零矩形、或合成的占位 WindowInfo):
        // MoveOnly / letterbox 都依赖它,拿垃圾尺寸会算出垃圾矩形。此时退回纯目标矩形 t
        // (该定位的照常定位,只是不按"当前尺寸/宽高比"二次加工)。
        int cw = currentWindowRect.Right - currentWindowRect.Left;
        int ch = currentWindowRect.Bottom - currentWindowRect.Top;
        bool curValid = cw > 0 && ch > 0;

        // 只定位(MoveOnly):把窗口左上角放到目标矩形左上角,保持窗口当前尺寸不变。
        // 用于渲染分辨率钉死在注册表的 Unity 游戏——resize 只会整张拉伸。
        // MoveOnly 与 KeepAspectRatio 互斥时 MoveOnly 优先(letterbox 在固定渲染分辨率下没意义)。
        if (rule.MoveOnly)
        {
            if (!curValid) return t;
            return new NativeMethods.RECT
            {
                Left = t.Left,
                Top = t.Top,
                Right = t.Left + cw,
                Bottom = t.Top + ch
            };
        }

        // 保持宽高比:以 currentWindowRect 的宽高比,在目标矩形内等比最大化并居中(letterbox)。
        if (p.KeepAspectRatio && curValid)
            t = Letterbox(t, currentWindowRect);

        return t;
    }

    /// <summary>
    /// zone 比例(0..1,相对 <paramref name="basis"/>)→ 绝对矩形(virtual-desktop 物理像素)。
    /// <c>basis.Left + Round(z.X·bw)</c> 等,语义与 <see cref="ResolveRect"/> 的 Zone 分支一致。
    /// 纯函数,供 ResolveRect、<see cref="DragSnapService"/>、HotkeyService 共用,保证三处取整口径统一。
    /// </summary>
    /// <param name="z">分区(比例坐标)。</param>
    /// <param name="basis">投射基准矩形:整屏(rcMonitor)或工作区(rcWork)。</param>
    public static NativeMethods.RECT ZoneToRect(Zone z, NativeMethods.RECT basis)
    {
        int bw = basis.Right - basis.Left, bh = basis.Bottom - basis.Top;
        return new NativeMethods.RECT
        {
            Left   = basis.Left + (int)Math.Round(z.X * bw),
            Top    = basis.Top  + (int)Math.Round(z.Y * bh),
            Right  = basis.Left + (int)Math.Round((z.X + z.W) * bw),
            Bottom = basis.Top  + (int)Math.Round((z.Y + z.H) * bh)
        };
    }

    /// <summary>
    /// 把内容按 content 的宽高比塞进 outer,等比最大化后居中。content 宽高非法(0)时原样返回 outer。
    /// </summary>
    private static NativeMethods.RECT Letterbox(NativeMethods.RECT outer, NativeMethods.RECT content)
    {
        int ow = outer.Right - outer.Left, oh = outer.Bottom - outer.Top;
        int cw = content.Right - content.Left, ch = content.Bottom - content.Top;
        if (ow <= 0 || oh <= 0 || cw <= 0 || ch <= 0) return outer;

        // 比较 content 与 outer 的宽高比:用受限的一边定缩放,另一边留黑边。
        // cw/ch ?= ow/oh  →  cw*oh ?= ow*ch,避免浮点。
        long contentWide = (long)cw * oh;
        long outerWide = (long)ow * ch;

        int w, h;
        if (contentWide > outerWide)
        {
            // content 比 outer 更"宽":宽度顶满,高度按比例缩。
            w = ow;
            h = (int)Math.Round((double)w * ch / cw);
        }
        else
        {
            // content 更"高"(或同比):高度顶满,宽度按比例缩。
            h = oh;
            w = (int)Math.Round((double)h * cw / ch);
        }

        int left = outer.Left + (ow - w) / 2;
        int top = outer.Top + (oh - h) / 2;
        return new NativeMethods.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
    }

    private static Zone? FindZone(AppConfig cfg, PlacementRule rule)
        => cfg.Layouts.FirstOrDefault(l => l.Id == rule.LayoutId)?
              .Zones.FirstOrDefault(z => z.Id == rule.ZoneId);
}
