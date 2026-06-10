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
    /// <summary>解析结果。Rect 为 null 表示不动几何。</summary>
    public readonly record struct Target(bool MakeBorderless, NativeMethods.RECT? Rect);

    public static Target Resolve(WindowInfo w, Profile p, AppConfig cfg)
    {
        IntPtr hMon = NativeMethods.MonitorFromWindow(w.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi))
            return new Target(p.Borderless, null);

        int monW = mi.rcMonitor.Right - mi.rcMonitor.Left;
        int monH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

        // 自上而下,第一条命中的规则生效
        PlacementRule? rule = null;
        foreach (var r in p.Rules)
        {
            if (r.Monitor.Matches(monW, monH)) { rule = r; break; }
        }
        if (rule is null)
            return new Target(p.Borderless, null);

        var basis = rule.UseWorkArea ? mi.rcWork : mi.rcMonitor;
        int bw = basis.Right - basis.Left, bh = basis.Bottom - basis.Top;

        NativeMethods.RECT? target = rule.Kind switch
        {
            PlacementKind.Fullscreen => basis,

            PlacementKind.Zone when FindZone(cfg, rule) is { } z => new NativeMethods.RECT
            {
                Left   = basis.Left + (int)Math.Round(z.X * bw),
                Top    = basis.Top  + (int)Math.Round(z.Y * bh),
                Right  = basis.Left + (int)Math.Round((z.X + z.W) * bw),
                Bottom = basis.Top  + (int)Math.Round((z.Y + z.H) * bh)
            },

            PlacementKind.CustomRect when rule.CustomRect is { } c => new NativeMethods.RECT
            {
                Left   = basis.Left + c.X,
                Top    = basis.Top  + c.Y,
                Right  = basis.Left + c.X + c.W,
                Bottom = basis.Top  + c.Y + c.H
            },

            _ => (NativeMethods.RECT?)null
        };

        // 四边偏移
        if (target is { } t)
        {
            var o = p.Offsets;
            target = new NativeMethods.RECT
            {
                Left = t.Left + o.Left,
                Top = t.Top + o.Top,
                Right = t.Right + o.Right,
                Bottom = t.Bottom + o.Bottom
            };
        }

        return new Target(p.Borderless, target);
    }

    private static Zone? FindZone(AppConfig cfg, PlacementRule rule)
        => cfg.Layouts.FirstOrDefault(l => l.Id == rule.LayoutId)?
              .Zones.FirstOrDefault(z => z.Id == rule.ZoneId);
}
