namespace Reframe.Services;

/// <summary>
/// The release-asset contract with <c>https://github.com/shuiandy/Reframe/releases</c>, and the
/// security boundary drawn around it.
///
/// <para><b>SECURITY RED LINE.</b> Reframe runs elevated (<c>requireAdministrator</c> in
/// app.manifest). Anything it downloads and executes therefore runs as administrator, so the update
/// path is deliberately narrow:</para>
/// <list type="number">
/// <item>The <b>only</b> metadata source is <see cref="LatestReleaseApiUrl"/> on
/// <c>api.github.com</c> — there is no user-configurable feed, no mirror, no fallback host.</item>
/// <item>An asset is only ever considered if its name is <b>exactly</b> one of the two names this
/// project publishes for that version (see <see cref="InstallerAssetName"/> /
/// <see cref="PortableAssetName"/>). A near-miss — different architecture, an extra extension, a
/// prefix, another version — is rejected, not "best-effort matched".</item>
/// <item>The download URL must survive <see cref="IsTrustedDownloadUrl"/>: https, host exactly
/// <c>github.com</c>, default port, no userinfo, path under
/// <c>/shuiandy/Reframe/releases/download/</c>, and a final segment equal to the expected asset
/// name. A <c>browser_download_url</c> that does not look like that is discarded even though it came
/// back from the GitHub API.</item>
/// </list>
/// <para>Consequence: the app never executes or writes a file fetched from a URL that some other
/// party could have put in the response body. GitHub redirects release downloads from
/// <c>github.com</c> to its CDN; <c>HttpClient</c> follows that redirect, and it will not follow an
/// https→http downgrade, so the transport stays TLS end to end.</para>
///
/// <para>Pure by design (string/URI logic only, no I/O): linked into
/// <c>Tests\Reframe.Core.Tests.csproj</c> so the matching rules are unit tested.</para>
/// </summary>
public static class UpdateAssets
{
    /// <summary>GitHub owner of the one repository this app will ever update from.</summary>
    public const string Owner = "shuiandy";

    /// <summary>GitHub repository name.</summary>
    public const string Repo = "Reframe";

    /// <summary>The single metadata endpoint. Hard-coded on purpose — see the type remarks.</summary>
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

    /// <summary>GitHub requires a User-Agent on every API request; a missing one is answered with 403.</summary>
    public const string UserAgent = "Reframe-UpdateCheck";

    /// <summary>Versioned media type for the GitHub REST API.</summary>
    public const string AcceptHeader = "application/vnd.github+json";

    private const string DownloadHost = "github.com";
    private const string DownloadPathPrefix = "/" + Owner + "/" + Repo + "/releases/download/";

    /// <summary>Installer asset published for <paramref name="version"/>, e.g. <c>Reframe-Setup-v1.3.1-win-x64.exe</c>.</summary>
    public static string InstallerAssetName(UpdateVersion version) => $"Reframe-Setup-v{version}-win-x64.exe";

    /// <summary>Portable asset published for <paramref name="version"/>, e.g. <c>Reframe-v1.3.1-win-x64.zip</c>.</summary>
    public static string PortableAssetName(UpdateVersion version) => $"Reframe-v{version}-win-x64.zip";

    /// <summary>The one asset name that is valid for this version and install flavour.</summary>
    public static string AssetNameFor(UpdateVersion version, UpdateInstallMode mode) =>
        mode == UpdateInstallMode.Installed ? InstallerAssetName(version) : PortableAssetName(version);

    /// <summary>
    /// Exact-match test against <see cref="AssetNameFor"/>. Case-insensitive only because Windows file
    /// names are; every other character must match, so <c>Reframe-v1.3.1-win-x64.zip.exe</c>,
    /// <c>Reframe-v1.3.1-win-x86.zip</c> and <c>evil-Reframe-v1.3.1-win-x64.zip</c> are all rejected.
    /// </summary>
    public static bool IsExpectedAssetName(string? name, UpdateVersion version, UpdateInstallMode mode) =>
        name is not null && string.Equals(name, AssetNameFor(version, mode), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// May this URL be handed to the downloader? See the type remarks for why every clause is here.
    /// <paramref name="expectedAssetName"/> is the name the caller already validated with
    /// <see cref="IsExpectedAssetName"/>; the URL's last path segment must equal it.
    /// </summary>
    public static bool IsTrustedDownloadUrl(string? url, string? expectedAssetName)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(expectedAssetName)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        // https only: no http, no file://, no ftp://, no anything else.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return false;

        // Exact host, never a suffix test - "github.com.evil.example" and "evilgithub.com" must fail.
        if (!string.Equals(uri.Host, DownloadHost, StringComparison.OrdinalIgnoreCase)) return false;

        // Non-default port or embedded credentials mean someone is doing something we did not publish.
        if (!uri.IsDefaultPort) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        // Uri has already canonicalised "." / ".." segments, so a prefix test is sound here.
        string path = uri.AbsolutePath;
        if (!path.StartsWith(DownloadPathPrefix, StringComparison.Ordinal)) return false;

        // Final segment must be exactly the asset we asked for (no query/fragment smuggling: those are
        // not part of AbsolutePath, and the file we write is named from expectedAssetName, not the URL).
        string last = path.Substring(path.LastIndexOf('/') + 1);
        return string.Equals(last, expectedAssetName, StringComparison.OrdinalIgnoreCase);
    }
}
