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
    ///
    /// <para><b>Transient fallback topologies still get a key.</b> <see cref="IsTransient"/> configurations are
    /// not special-cased here: the format is unconditional and a fallback topology hashes to an ordinary key
    /// like <c>1024x768@0,0#96*</c>. The key is merely never <i>used</i> — <c>PersistenceEngine</c> refuses to
    /// capture, restore or persist under it, and <c>LayoutMemory</c> refuses to create a bucket for it. Keeping
    /// <c>Compute</c> total (never returning a sentinel for these) means the on-disk key format has exactly one
    /// producer and one shape, and that <see cref="IsTransientKey"/> can recognise the garbage buckets earlier
    /// builds already wrote.</para>
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

    // ---- Transient (fallback) topologies ------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="monitors"/> is Windows' <b>no-usable-display fallback</b> rather than a display
    /// configuration a human could work on.
    ///
    /// <para><b>The failure this exists to stop.</b> A large TV/monitor driven from several inputs (console,
    /// PC, streaming box) genuinely leaves the graphics adapter for a few seconds when the input is switched
    /// back to the PC: the panel drops off the bus, Windows has no EDID to read, and the Basic Display path
    /// brings the desktop up at 1024×768 on a single virtual primary at the origin — then the panel returns and
    /// the real mode comes back. Every window on the desktop is squeezed into that 1024×768 box in the
    /// meantime. To a key-based engine that is just another display configuration: the key changes, capture
    /// freezes, the topology settles, and the periodic capture then faithfully records <i>the squeezed
    /// geometry</i> into a 1024×768 bucket — and, on disk, forever. That bucket is pure garbage (three of them
    /// were found in the wild, holding five to six mangled windows each) and the round trip is also what makes
    /// windows come back "mysteriously resized" after switching the TV back to the PC.</para>
    ///
    /// <para><b>The test is deliberately narrow</b> — <i>single</i> display, one of the two classic fallback
    /// modes, at the virtual-desktop origin, flagged primary. Every clause is load-bearing:</para>
    /// <list type="bullet">
    /// <item><b>Exactly one monitor.</b> A 1024×768 panel sitting <i>next to</i> a real one is somebody's small
    /// secondary screen, KVM preview or capture monitor — a configuration they arranged on purpose and expect
    /// to be remembered. The fallback never has company: it exists precisely because nothing else is there.</item>
    /// <item><b>1024×768 or 800×600 only.</b> These are the two modes the Microsoft Basic Display Adapter comes
    /// up in with no EDID (800×600 on older builds / early boot). <i>Not</i> "any small resolution": a virtual
    /// display for streaming (Sunshine's VDD and friends) is routinely 1280×800, 1920×1080 or 1920×1200 and is
    /// a first-class configuration whose layout must be remembered. Widening this to a size threshold would
    /// silently stop persisting for those users, which is a worse bug than the one being fixed.</item>
    /// <item><b>At (0,0) and primary.</b> A synthesised fallback desktop is always the origin and always
    /// primary. Requiring both costs nothing and keeps a deliberately-positioned real 1024×768 screen out.</item>
    /// </list>
    ///
    /// <para>False negatives are cheap here (the engine simply behaves as it did before, one garbage bucket),
    /// false positives are expensive (a real configuration stops being remembered at all) — hence the bias.</para>
    ///
    /// <para>Pure: no Win32, no state, no clock. An empty/null list is <b>not</b> transient — that case is
    /// already covered by <see cref="None"/>, which every consumer ignores separately.</para>
    /// </summary>
    public static bool IsTransient(IReadOnlyList<MonitorDesc>? monitors)
    {
        if (monitors is null || monitors.Count != 1) return false;
        var m = monitors[0];
        return m.IsPrimary && m.X == 0 && m.Y == 0 && IsFallbackSize(m.Width, m.Height);
    }

    /// <summary>
    /// The same judgement made from a <i>key string</i> instead of live monitor descriptions — for buckets read
    /// off disk, which is all we have for the garbage earlier builds already wrote.
    ///
    /// <para>Parses one <c>{W}x{H}@{X},{Y}[#dpi][*]</c> token and applies exactly the clauses of
    /// <see cref="IsTransient"/>: a <c>|</c> anywhere means more than one monitor and is rejected outright, the
    /// trailing <c>*</c> (primary) is required, the origin must be <c>0,0</c>, and the size must be one of the
    /// two fallback modes. The <c>#dpi</c> segment is optional so <b>pre-DPI keys</b> (<c>1024x768@0,0*</c>,
    /// written before that segment existed) are recognised too — the user's real file contains both shapes.</para>
    ///
    /// <para>Never throws: anything that isn't a well-formed single token — a sentinel, a hand-edited file, a
    /// future format, plain junk — is simply "not transient", which leaves it to the normal aging rules.
    /// Digits are matched by hand rather than via <c>int.TryParse</c> so no culture setting can change what a
    /// key means.</para>
    /// </summary>
    public static bool IsTransientKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;

        var s = key.AsSpan();
        if (s.IndexOf('|') >= 0) return false;   // multi-monitor ⇒ a real configuration, whatever its sizes
        if (s[^1] != '*') return false;          // the single display must be the primary
        s = s[..^1];

        // Optional "#dpi" tail. Its value is irrelevant to the judgement (the fallback is reported at 96, 120
        // or 168 depending on what the user's scale was), but it must be well-formed to trust the rest.
        int hash = s.IndexOf('#');
        if (hash >= 0)
        {
            if (!TryParseInt(s[(hash + 1)..], out _)) return false;
            s = s[..hash];
        }

        int at = s.IndexOf('@');
        if (at < 0) return false;
        var size = s[..at];
        var origin = s[(at + 1)..];

        int by = size.IndexOf('x');
        if (by < 0) return false;
        if (!TryParseInt(size[..by], out int w)) return false;
        if (!TryParseInt(size[(by + 1)..], out int h)) return false;

        int comma = origin.IndexOf(',');
        if (comma < 0) return false;
        if (!TryParseInt(origin[..comma], out int x)) return false;
        if (!TryParseInt(origin[(comma + 1)..], out int y)) return false;

        return x == 0 && y == 0 && IsFallbackSize(w, h);
    }

    /// <summary>
    /// The two modes the Microsoft Basic Display Adapter synthesises when there is no EDID to read. Hard-coded
    /// on purpose — see <see cref="IsTransient"/> on why this must not become a size threshold.
    /// </summary>
    private static bool IsFallbackSize(int width, int height)
        => (width == 1024 && height == 768) || (width == 800 && height == 600);

    /// <summary>Culture-free non-negative/negative integer parse over a key fragment; rejects empty and any non-digit.</summary>
    private static bool TryParseInt(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        if (s.Length == 0) return false;
        bool negative = s[0] == '-';
        if (negative) { s = s[1..]; if (s.Length == 0) return false; }

        long acc = 0;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
            acc = acc * 10 + (c - '0');
            if (acc > int.MaxValue) return false;
        }
        value = (int)(negative ? -acc : acc);
        return true;
    }
}
