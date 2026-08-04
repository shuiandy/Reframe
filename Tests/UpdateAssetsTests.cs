using Reframe.Services;
using Xunit;

namespace Reframe.Core.Tests;

/// <summary>
/// UpdateAssets: the security boundary of the self-update. Reframe runs elevated, so whatever it
/// downloads runs as administrator — these tests exist to make sure the only things that ever get
/// past the gate are the two asset names this project publishes, fetched from a github.com release
/// download URL for this repository. Every "close enough" name and every plausible URL trick is
/// asserted to be rejected.
/// </summary>
public class UpdateAssetsTests
{
    private static readonly UpdateVersion V = new(1, 3, 1);

    private const string RealZipUrl = "https://github.com/shuiandy/Reframe/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip";
    private const string RealExeUrl = "https://github.com/shuiandy/Reframe/releases/download/v1.3.1/Reframe-Setup-v1.3.1-win-x64.exe";

    // ------------------------------------------------------------------ asset names

    [Fact(DisplayName = "Asset names match what tools\\publish.ps1 and Reframe.iss actually produce")]
    public void AssetNames_MatchPublishedArtifacts()
    {
        Assert.Equal("Reframe-Setup-v1.3.1-win-x64.exe", UpdateAssets.InstallerAssetName(V));
        Assert.Equal("Reframe-v1.3.1-win-x64.zip", UpdateAssets.PortableAssetName(V));

        Assert.Equal(UpdateAssets.InstallerAssetName(V), UpdateAssets.AssetNameFor(V, UpdateInstallMode.Installed));
        Assert.Equal(UpdateAssets.PortableAssetName(V), UpdateAssets.AssetNameFor(V, UpdateInstallMode.Portable));
    }

    [Theory(DisplayName = "Portable: only the exact zip name is accepted")]
    [InlineData("Reframe-v1.3.1-win-x64.zip", true)]
    [InlineData("reframe-v1.3.1-win-x64.ZIP", true)]      // case only: Windows file names are case-insensitive
    [InlineData("Reframe-v1.3.1-win-x64.zip.exe", false)] // double extension
    [InlineData("Reframe-v1.3.1-win-x64.exe", false)]     // wrong extension
    [InlineData("Reframe-v1.3.1-win-x86.zip", false)]     // wrong architecture
    [InlineData("Reframe-v1.3.1-win-arm64.zip", false)]
    [InlineData("Reframe-v1.3.10-win-x64.zip", false)]    // different version that shares a prefix
    [InlineData("Reframe-v1.3.0-win-x64.zip", false)]
    [InlineData("evil-Reframe-v1.3.1-win-x64.zip", false)]
    [InlineData("Reframe-v1.3.1-win-x64.zip.sig", false)]
    [InlineData("Reframe-Setup-v1.3.1-win-x64.exe", false)] // the other flavour's asset
    [InlineData(" Reframe-v1.3.1-win-x64.zip", false)]      // leading space
    [InlineData("Reframe-v1.3.1-win-x64.zip ", false)]      // trailing space
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PortableAssetName_ExactMatchOnly(string? name, bool expected)
        => Assert.Equal(expected, UpdateAssets.IsExpectedAssetName(name, V, UpdateInstallMode.Portable));

    [Theory(DisplayName = "Installed: only the exact setup name is accepted")]
    [InlineData("Reframe-Setup-v1.3.1-win-x64.exe", true)]
    [InlineData("Reframe-setup-v1.3.1-win-x64.exe", true)]
    [InlineData("Reframe-Setup-v1.3.1-win-x64.exe.exe", false)]
    [InlineData("Reframe-Setup-v1.3.1-win-x86.exe", false)]
    [InlineData("Reframe-Setup-v1.3.1-win-x64.msi", false)]
    [InlineData("Reframe-v1.3.1-win-x64.zip", false)] // the other flavour's asset
    [InlineData("Setup.exe", false)]
    [InlineData(null, false)]
    public void InstallerAssetName_ExactMatchOnly(string? name, bool expected)
        => Assert.Equal(expected, UpdateAssets.IsExpectedAssetName(name, V, UpdateInstallMode.Installed));

    [Fact(DisplayName = "An asset for a different version never matches")]
    public void DifferentVersion_DoesNotMatch()
    {
        Assert.False(UpdateAssets.IsExpectedAssetName(
            "Reframe-v1.3.1-win-x64.zip", new UpdateVersion(1, 4, 0), UpdateInstallMode.Portable));
        Assert.True(UpdateAssets.IsExpectedAssetName(
            "Reframe-v1.4.0-win-x64.zip", new UpdateVersion(1, 4, 0), UpdateInstallMode.Portable));
    }

    // ------------------------------------------------------------------ download URL trust

    [Fact(DisplayName = "The two real release URLs are trusted")]
    public void RealUrls_Trusted()
    {
        Assert.True(UpdateAssets.IsTrustedDownloadUrl(RealZipUrl, "Reframe-v1.3.1-win-x64.zip"));
        Assert.True(UpdateAssets.IsTrustedDownloadUrl(RealExeUrl, "Reframe-Setup-v1.3.1-win-x64.exe"));
    }

    [Theory(DisplayName = "Every non-github.com / non-https / wrong-repo URL is rejected")]
    // plain http, and https on the wrong host
    [InlineData("http://github.com/shuiandy/Reframe/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip")]
    [InlineData("https://github.com.evil.example/shuiandy/Reframe/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip")]
    [InlineData("https://evilgithub.com/shuiandy/Reframe/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip")]
    [InlineData("https://raw.githubusercontent.com/shuiandy/Reframe/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip")]
    // credentials / non-default port used to make the host read like github.com
    [InlineData("https://github.com@evil.example/shuiandy/Reframe/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip")]
    [InlineData("https://github.com:8443/shuiandy/Reframe/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip")]
    // right host, wrong path: another owner, another repo, or not a release download at all
    [InlineData("https://github.com/someoneelse/Reframe/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip")]
    [InlineData("https://github.com/shuiandy/OtherRepo/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip")]
    [InlineData("https://github.com/shuiandy/Reframe/raw/main/Reframe-v1.3.1-win-x64.zip")]
    [InlineData("https://github.com/shuiandy/Reframe/releases/download/v1.3.1/../../../Reframe-v1.3.1-win-x64.zip")]
    // not a URL, or not a network URL
    [InlineData("file:///C:/temp/Reframe-v1.3.1-win-x64.zip")]
    [InlineData("ftp://github.com/shuiandy/Reframe/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip")]
    [InlineData("/shuiandy/Reframe/releases/download/v1.3.1/Reframe-v1.3.1-win-x64.zip")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void UntrustedUrls_Rejected(string? url)
        => Assert.False(UpdateAssets.IsTrustedDownloadUrl(url, "Reframe-v1.3.1-win-x64.zip"));

    [Fact(DisplayName = "A trusted URL whose final segment is not the expected asset is rejected")]
    public void UrlMustEndWithExpectedAsset()
    {
        // Correct host and path prefix, but serving something else.
        Assert.False(UpdateAssets.IsTrustedDownloadUrl(
            "https://github.com/shuiandy/Reframe/releases/download/v1.3.1/payload.exe",
            "Reframe-v1.3.1-win-x64.zip"));

        // The installer asset offered where the portable one was expected.
        Assert.False(UpdateAssets.IsTrustedDownloadUrl(RealExeUrl, "Reframe-v1.3.1-win-x64.zip"));

        // No expected name to compare against: refuse rather than accept anything.
        Assert.False(UpdateAssets.IsTrustedDownloadUrl(RealZipUrl, null));
        Assert.False(UpdateAssets.IsTrustedDownloadUrl(RealZipUrl, ""));
    }

    [Fact(DisplayName = "The metadata endpoint is the hard-coded api.github.com release URL for this repo")]
    public void ApiUrl_IsPinned()
        => Assert.Equal("https://api.github.com/repos/shuiandy/Reframe/releases/latest", UpdateAssets.LatestReleaseApiUrl);
}
