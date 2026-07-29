using System.Text;

namespace Reframe.Core;

/// <summary>
/// Computes a stable fingerprint ("DisplayKey") for a monitor configuration. The same set of monitors
/// (resolution + virtual-desktop position + which one is primary + <b>per-monitor DPI scaling</b>) yields the
/// same key regardless of enumeration order; a streaming single-display setup and a multi-monitor desktop
/// yield different keys, so each configuration's window layout is remembered in its own bucket.
///
/// <para><b>Why scaling is part of the key.</b> Changing the display scale (say 150% → 175%) leaves the
/// physical resolution completely untouched — 7680×2160 before, 7680×2160 after — so a geometry-only key
/// does not move, with two bad consequences:</para>
/// <list type="number">
/// <item><b>No restore, and the good memory gets overwritten.</b> Windows re-scales and shuffles every
/// window when the scale factor changes. With an unchanged key the engine sees no topology change, does not
/// restore, and its next periodic capture happily records the freshly scrambled layout <i>over</i> the good
/// one — permanently, since the memory is on disk.</item>
/// <item><b>Two scale factors sharing one bucket.</b> Pixel rectangles recorded at 150% are wrong at 175%:
/// the window frames come back the remembered size but everything inside them is ~17% larger, so a layout
/// that was tuned at one scale looks subtly wrong at the other. They are genuinely different layouts and
/// belong in different buckets.</item>
/// </list>
/// <para>Including the DPI fixes both. It also makes the capture-time guard in
/// <c>PersistenceEngine.OnCapture</c> (<c>liveKey != _currentKey ⇒ freeze</c>) catch a scaling change on its
/// own, so no extra message subscription is needed even when Windows sends no <c>WM_DISPLAYCHANGE</c>.</para>
///
/// <para>Deliberate exclusions:</para>
/// <list type="bullet">
/// <item>The GDI device name (<c>\\.\DISPLAYn</c>) — it drifts across reconnect/replug, so two
/// physically identical configs could get different names.</item>
/// <item>The work area (<c>rcWork</c>) — it shifts when the taskbar auto-hides or a band appears, which
/// must not re-bucket the layout. The work area is still recorded per-window for relative positioning;
/// it just isn't part of the key.</item>
/// </list>
///
/// <para><b>Old keys are not migrated.</b> Buckets written before the DPI segment existed carry no
/// <c>#dpi</c> and therefore never match a key computed today; guessing which scale factor they were
/// recorded at would be worse than losing them (a wrong guess restores wrong geometry, silently). They are
/// harmless — an unmatched bucket is inert — and <c>LayoutMemory.Trim</c>'s normal aging removes them.</para>
///
/// P1 keys on monitor geometry + scaling only. A later phase may upgrade to CCD/EDID-based stable ids to
/// also disambiguate two identical-resolution monitors that swap virtual positions.
/// </summary>
public static class LayoutKey
{
    /// <summary>Sentinel key when no monitors are reported (e.g. all displays asleep mid-transition).</summary>
    public const string None = "none";

    /// <summary>
    /// Compute the DisplayKey. Each monitor becomes <c>{W}x{H}@{X},{Y}#{Dpi}</c>, with a trailing <c>*</c>
    /// appended for the primary — e.g. <c>7680x2160@0,0#168*</c> for a primary 7680×2160 panel at 175%.
    ///
    /// <para><b>Segment order is fixed:</b> geometry, then <c>#</c> + effective DPI, then the primary
    /// <c>*</c>. So <c>#</c> always follows the <c>@x,y</c> origin and <c>*</c> — when present — is always
    /// the very last character of a token, exactly as it was before the DPI segment was introduced. Nothing
    /// parses these tokens back apart today, but the order is part of the on-disk key format and must stay
    /// stable, or every user's remembered layouts silently re-bucket.</para>
    ///
    /// <para>Tokens are sorted ordinally so the key is independent of enumeration order, then joined with
    /// <c>|</c>. <c>#</c> never occurred in a pre-DPI key, so an old bucket can never collide with a new one.</para>
    /// </summary>
    public static string Compute(IReadOnlyList<MonitorDesc>? monitors)
    {
        if (monitors is null || monitors.Count == 0) return None;

        var tokens = new List<string>(monitors.Count);
        foreach (var m in monitors)
        {
            var sb = new StringBuilder(32);
            sb.Append(m.Width).Append('x').Append(m.Height)
              .Append('@').Append(m.X).Append(',').Append(m.Y)
              .Append('#').Append(m.Dpi);
            if (m.IsPrimary) sb.Append('*');
            tokens.Add(sb.ToString());
        }
        tokens.Sort(StringComparer.Ordinal);
        return string.Join("|", tokens);
    }
}
