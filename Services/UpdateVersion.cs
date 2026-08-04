using System.Globalization;

namespace Reframe.Services;

/// <summary>
/// Which flavour of Reframe this process is running as. Decides which release asset the self-update
/// downloads and which update script it generates.
/// </summary>
public enum UpdateInstallMode
{
    /// <summary>A portable copy (the release zip, unpacked anywhere the user likes). Updated by overwriting files in place.</summary>
    Portable,

    /// <summary>Installed by the Inno Setup package into Program Files. Updated by running the new Setup.exe silently.</summary>
    Installed,
}

/// <summary>
/// A three-component <c>major.minor.patch</c> version, plus the parsing and comparison rules the
/// update check is built on.
///
/// <para><b>Pure by design</b> (no I/O, no registry, no WinUI): this file is linked into
/// <c>Tests\Reframe.Core.Tests.csproj</c> so the version logic — the thing that decides whether the
/// app downloads anything at all — is unit tested directly.</para>
///
/// <para>Accepted spellings, because the two sides of the comparison are spelled differently:
/// <list type="bullet">
/// <item>The GitHub release tag is <c>v1.3.1</c> — a leading <c>v</c>/<c>V</c> is stripped.</item>
/// <item>The running app's <c>AssemblyInformationalVersion</c> may be <c>1.3.1+9f2c1ab</c> when the SDK
/// appends the source revision — everything from the first <c>+</c> is dropped.</item>
/// <item>A fallback to <c>Assembly.GetName().Version</c> yields four components (<c>1.3.1.0</c>) — the
/// fourth (revision) is parsed for validity but ignored in comparisons, since releases are tagged
/// with three.</item>
/// </list></para>
///
/// <para><b>Conservative on failure.</b> Anything else — a pre-release tail (<c>1.4.0-rc1</c>), a
/// non-numeric component, five components, an empty string, an overflowing number — fails to parse,
/// and <see cref="IsNewer"/> answers <c>false</c>. A version string we do not understand must never be
/// read as "newer", because that is what would put an update prompt (and eventually a download) in
/// front of the user.</para>
/// </summary>
public readonly struct UpdateVersion : IEquatable<UpdateVersion>, IComparable<UpdateVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    public UpdateVersion(int major, int minor, int patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    /// <summary>
    /// Parse a version string per the rules documented on the type. Returns false (and
    /// <c>default</c>) for anything not understood; never throws.
    /// </summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();

        // Release tag form: "v1.3.1" / "V1.3.1".
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);

        // InformationalVersion form: "1.3.1+9f2c1ab" -> drop the build metadata.
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);

        s = s.Trim();
        if (s.Length == 0) return false;

        string[] parts = s.Split('.');
        // 1..4 components. Four is tolerated only because Assembly.GetName().Version is the fallback
        // source for the current version; the revision is validated then discarded.
        if (parts.Length > 4) return false;

        int major = 0, minor = 0, patch = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!TryParseComponent(parts[i], out int n)) return false;
            switch (i)
            {
                case 0: major = n; break;
                case 1: minor = n; break;
                case 2: patch = n; break;
                default: break; // revision: validated above, deliberately ignored
            }
        }

        version = new UpdateVersion(major, minor, patch);
        return true;
    }

    /// <summary>
    /// One numeric component. Digits only — which is what rejects <c>1a</c>, <c>-1</c>, <c>+1</c>,
    /// <c> 1</c>, <c>1e3</c>, and any pre-release tail such as <c>1-rc1</c>. Overflow (a component
    /// wider than <see cref="int"/>) fails too.
    /// </summary>
    private static bool TryParseComponent(string part, out int value)
    {
        value = 0;
        if (part.Length == 0) return false;
        foreach (char c in part)
            if (c < '0' || c > '9') return false;
        return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Numeric ordering: major, then minor, then patch. Returns &lt;0, 0 or &gt;0.</summary>
    public static int Compare(UpdateVersion a, UpdateVersion b)
    {
        if (a.Major != b.Major) return a.Major < b.Major ? -1 : 1;
        if (a.Minor != b.Minor) return a.Minor < b.Minor ? -1 : 1;
        if (a.Patch != b.Patch) return a.Patch < b.Patch ? -1 : 1;
        return 0;
    }

    /// <summary>
    /// Is <paramref name="candidate"/> strictly newer than <paramref name="current"/>? If either side
    /// fails to parse the answer is <c>false</c> — "I can't tell" always means "no update" (see the
    /// type remarks).
    /// </summary>
    public static bool IsNewer(string? current, string? candidate)
    {
        if (!TryParse(current, out var cur)) return false;
        if (!TryParse(candidate, out var cand)) return false;
        return Compare(cand, cur) > 0;
    }

    /// <summary>Canonical three-component text, e.g. <c>1.3.1</c>. This is the form used to build asset names.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public int CompareTo(UpdateVersion other) => Compare(this, other);
    public bool Equals(UpdateVersion other) => Compare(this, other) == 0;
    public override bool Equals(object? obj) => obj is UpdateVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch);

    public static bool operator ==(UpdateVersion a, UpdateVersion b) => Compare(a, b) == 0;
    public static bool operator !=(UpdateVersion a, UpdateVersion b) => Compare(a, b) != 0;
    public static bool operator <(UpdateVersion a, UpdateVersion b) => Compare(a, b) < 0;
    public static bool operator >(UpdateVersion a, UpdateVersion b) => Compare(a, b) > 0;
    public static bool operator <=(UpdateVersion a, UpdateVersion b) => Compare(a, b) <= 0;
    public static bool operator >=(UpdateVersion a, UpdateVersion b) => Compare(a, b) >= 0;
}
