using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Reframe.Interop;

namespace Reframe.Core;

/// <summary>Matching: window ↔ profile.</summary>
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

    /// <summary>
    /// A cached <see cref="Regex"/> compiled once per pattern (<see cref="MatchKind.TitleRegex"/> is called
    /// at high frequency every tick, so Compiled pays off; an invalid pattern caches null to avoid repeatedly
    /// throwing on construction).
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex?> _regexCache = new();

    /// <summary>
    /// Match-timeout cap for user regexes: catastrophic backtracking (e.g. <c>(a+)+$</c> against a long run of
    /// a's) blows up exponentially in the .NET regex engine; without a timeout it would hang the whole scan
    /// tick (and the engine). Capped at 100ms; a timeout is treated as "no match".
    /// </summary>
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);

    private static bool SafeRegex(string pattern, string input)
    {
        // Get/create the cached Regex instance: an invalid pattern → cache null, then just report no match
        // thereafter without repeatedly constructing and throwing.
        var re = _regexCache.GetOrAdd(pattern, static pat =>
        {
            try
            {
                return new Regex(pat, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexMatchTimeout);
            }
            catch
            {
                return null; // Invalid expression: cache null
            }
        });

        if (re is null) return false;

        try
        {
            return re.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            // Catastrophic-backtracking timeout: treat as no match, don't let one bad pattern drag down the scan.
            return false;
        }
        catch
        {
            // Fallback: treat any other runtime exception as no match; never throw upward (MatchEngine is on the per-tick hot path).
            return false;
        }
    }
}

/// <summary>Resolution: the monitor the window is currently on + the profile's rule table → target geometry.</summary>
public static class PlacementResolver
{
    /// <summary>
    /// Resolution result. A null Rect means "leave geometry alone"; Topmost is turned into HWND_TOPMOST/NOTOPMOST by WindowOps.
    /// </summary>
    public readonly record struct Target(bool MakeBorderless, NativeMethods.RECT? Rect, bool Topmost);

    /// <summary>
    /// Fetch monitor info + the window's current rect, then delegate to the pure function <see cref="ResolveRect"/>.
    /// This is the only entry point that touches Win32; all geometry math lives in ResolveRect (unit-testable).
    /// </summary>
    public static Target Resolve(WindowInfo w, Profile p, AppConfig cfg)
    {
        IntPtr hMon = NativeMethods.MonitorFromWindow(w.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi))
            return new Target(p.Borderless, null, p.Topmost);

        // If GetWindowRect fails (handle just went invalid, etc.): don't feed a zero rect into MoveOnly/letterbox
        // (it would produce a garbage rect that ClipCursor then clamps to). On failure, keep only the
        // border-strip/topmost intent and return null geometry (leave it alone).
        if (!NativeMethods.GetWindowRect(w.Handle, out var cur))
            return new Target(p.Borderless, null, p.Topmost);

        var rect = ResolveRect(mi.rcMonitor, mi.rcWork, cur, p, cfg);
        return new Target(p.Borderless, rect, p.Topmost);
    }

    /// <summary>
    /// The pure geometry core (no Win32; the unit-test target). Returns the target rect (physical pixels,
    /// virtual-desktop coordinates); null = no rule matched this monitor or the rule produces no rect, so the
    /// caller "leaves geometry alone".
    /// </summary>
    /// <param name="rcMonitor">The whole rect of the window's monitor (including the taskbar).</param>
    /// <param name="rcWork">That monitor's work area (excluding the taskbar).</param>
    /// <param name="currentWindowRect">The window's current rect; only KeepAspectRatio uses its aspect ratio for letterboxing.</param>
    /// <param name="p">The profile (rule table, Offsets, KeepAspectRatio).</param>
    /// <param name="cfg">Used to look up a Zone's ratios by LayoutId/ZoneId.</param>
    public static NativeMethods.RECT? ResolveRect(
        NativeMethods.RECT rcMonitor, NativeMethods.RECT rcWork,
        NativeMethods.RECT currentWindowRect, Profile p, AppConfig cfg)
    {
        int monW = rcMonitor.Right - rcMonitor.Left;
        int monH = rcMonitor.Bottom - rcMonitor.Top;

        // Top-down, the first rule whose Monitor matches wins (matched on the whole-monitor resolution).
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

        // Per-edge offsets
        var o = p.Offsets;
        t = new NativeMethods.RECT
        {
            Left = t.Left + o.Left,
            Top = t.Top + o.Top,
            Right = t.Right + o.Right,
            Bottom = t.Bottom + o.Bottom
        };

        // The current window rect's width/height is invalid (GetWindowRect failed and gave a zero rect, or a
        // synthesized placeholder WindowInfo): both MoveOnly and letterbox depend on it, and a garbage size
        // yields a garbage rect. Fall back to the plain target rect t (still positioned as intended, just
        // without the secondary "current size / aspect ratio" processing).
        int cw = currentWindowRect.Right - currentWindowRect.Left;
        int ch = currentWindowRect.Bottom - currentWindowRect.Top;
        bool curValid = cw > 0 && ch > 0;

        // Move only (MoveOnly): place the window's top-left at the target rect's top-left, keeping the
        // window's current size. For Unity games with render resolution pinned in the registry — a resize
        // would just stretch the whole frame. When MoveOnly and KeepAspectRatio conflict, MoveOnly wins
        // (letterboxing is meaningless at a fixed render resolution).
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

        // Keep aspect ratio: using currentWindowRect's aspect ratio, maximize proportionally within the target rect and center (letterbox).
        if (p.KeepAspectRatio && curValid)
            t = Letterbox(t, currentWindowRect);

        return t;
    }

    /// <summary>
    /// Zone ratios (0..1, relative to <paramref name="basis"/>) → an absolute rect (virtual-desktop physical
    /// pixels). <c>basis.Left + Round(z.X·bw)</c> etc., semantics identical to the Zone branch of
    /// <see cref="ResolveRect"/>. A pure function shared by ResolveRect, <see cref="DragSnapService"/> and
    /// HotkeyService, keeping the rounding consistent across all three.
    /// </summary>
    /// <param name="z">The zone (ratio coordinates).</param>
    /// <param name="basis">The projection basis rect: the whole monitor (rcMonitor) or the work area (rcWork).</param>
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
    /// Fit content into outer at content's aspect ratio, maximizing proportionally and centering. If content's width/height is invalid (0), return outer unchanged.
    /// </summary>
    private static NativeMethods.RECT Letterbox(NativeMethods.RECT outer, NativeMethods.RECT content)
    {
        int ow = outer.Right - outer.Left, oh = outer.Bottom - outer.Top;
        int cw = content.Right - content.Left, ch = content.Bottom - content.Top;
        if (ow <= 0 || oh <= 0 || cw <= 0 || ch <= 0) return outer;

        // Compare content's and outer's aspect ratios: the constrained side sets the scale, the other gets black bars.
        // cw/ch ?= ow/oh  →  cw*oh ?= ow*ch, avoiding floating point.
        long contentWide = (long)cw * oh;
        long outerWide = (long)ow * ch;

        int w, h;
        if (contentWide > outerWide)
        {
            // content is "wider" than outer: width fills, height scales proportionally.
            w = ow;
            h = (int)Math.Round((double)w * ch / cw);
        }
        else
        {
            // content is "taller" (or same ratio): height fills, width scales proportionally.
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
