using System.Text;

namespace Reframe.Core;

/// <summary>
/// Computes a stable fingerprint ("DisplayKey") for a monitor configuration. The same set of monitors
/// (resolution + virtual-desktop position + which one is primary) yields the same key regardless of
/// enumeration order; a streaming single-display setup and a multi-monitor desktop yield different keys,
/// so each configuration's window layout is remembered in its own bucket.
///
/// <para>Deliberate exclusions:</para>
/// <list type="bullet">
/// <item>The GDI device name (<c>\\.\DISPLAYn</c>) — it drifts across reconnect/replug, so two
/// physically identical configs could get different names.</item>
/// <item>The work area (<c>rcWork</c>) — it shifts when the taskbar auto-hides or a band appears, which
/// must not re-bucket the layout. The work area is still recorded per-window for relative positioning;
/// it just isn't part of the key.</item>
/// </list>
/// P1 keys on monitor geometry only. A later phase may upgrade to CCD/EDID-based stable ids to also
/// disambiguate two identical-resolution monitors that swap virtual positions.
/// </summary>
public static class LayoutKey
{
    /// <summary>Sentinel key when no monitors are reported (e.g. all displays asleep mid-transition).</summary>
    public const string None = "none";

    /// <summary>
    /// Compute the DisplayKey. Each monitor becomes <c>{W}x{H}@{X},{Y}</c> (with a trailing <c>*</c> for
    /// the primary); tokens are sorted ordinally so the key is independent of enumeration order, then
    /// joined with <c>|</c>.
    /// </summary>
    public static string Compute(IReadOnlyList<MonitorDesc>? monitors)
    {
        if (monitors is null || monitors.Count == 0) return None;

        var tokens = new List<string>(monitors.Count);
        foreach (var m in monitors)
        {
            var sb = new StringBuilder(24);
            sb.Append(m.Width).Append('x').Append(m.Height)
              .Append('@').Append(m.X).Append(',').Append(m.Y);
            if (m.IsPrimary) sb.Append('*');
            tokens.Add(sb.ToString());
        }
        tokens.Sort(StringComparer.Ordinal);
        return string.Join("|", tokens);
    }
}
