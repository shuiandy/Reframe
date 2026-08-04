using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace Reframe.Services;

/// <summary>Outcome of an update check.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The latest release is not newer than what is running (also the answer whenever anything was ambiguous).</summary>
    UpToDate,

    /// <summary>A newer release exists. <see cref="UpdateCheckResult.AssetUrl"/> tells you whether it can be applied automatically.</summary>
    UpdateAvailable,

    /// <summary>The check itself failed (no network, timeout, rate limit, malformed response). <see cref="UpdateCheckResult.Error"/> has the detail.</summary>
    Failed,
}

/// <summary>Result of one <see cref="UpdateService.CheckAsync"/> call. Immutable.</summary>
public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }

    /// <summary>Version parsed from the release tag. Meaningful only when <see cref="Status"/> is <see cref="UpdateCheckStatus.UpdateAvailable"/>.</summary>
    public UpdateVersion Version { get; init; }

    /// <summary>Install flavour this check was made for; decides which asset was looked for.</summary>
    public UpdateInstallMode Mode { get; init; }

    /// <summary>Validated asset name, or null when the release carries no asset we recognise for this flavour.</summary>
    public string? AssetName { get; init; }

    /// <summary>Validated download URL, or null. Null means "there is a new version but Reframe cannot install it for you".</summary>
    public string? AssetUrl { get; init; }

    /// <summary>Asset size reported by the GitHub API, used as a second check on the downloaded bytes. 0 = unknown.</summary>
    public long AssetSize { get; init; }

    /// <summary>Failure detail when <see cref="Status"/> is <see cref="UpdateCheckStatus.Failed"/>.</summary>
    public string? Error { get; init; }

    public static UpdateCheckResult UpToDate() => new() { Status = UpdateCheckStatus.UpToDate };
    public static UpdateCheckResult Fail(string error) => new() { Status = UpdateCheckStatus.Failed, Error = error };
}

/// <summary>
/// Update check and self-update against this project's GitHub Releases.
///
/// <para><b>What it will and will not do.</b> It asks <c>api.github.com</c> for the latest release,
/// compares versions, and reports. It never downloads on its own and never installs silently: the
/// startup check only raises a dismissible banner, and downloading + applying is always a button the
/// user pressed. The security rules around which URL may ever be fetched live in
/// <see cref="UpdateAssets"/> — this class only enforces them, at both the check and the download.</para>
///
/// <para><b>Split of responsibilities.</b> Everything decidable without touching the machine lives in
/// the three pure, unit-tested companions — <see cref="UpdateVersion"/> (parse/compare),
/// <see cref="UpdateAssets"/> (asset names, URL trust), <see cref="UpdateScript"/> (path comparison,
/// script text). This class holds only what needs the network, the registry, the disk and the
/// process table.</para>
///
/// <para><b>Failure policy.</b> The startup check is completely silent — no network, a timeout or a
/// rate-limit answer must never interrupt anyone. A manual check reports its failure in the Settings
/// page status line.</para>
/// </summary>
public sealed class UpdateService
{
    public static UpdateService Instance { get; } = new();

    /// <summary>
    /// Development/test hook: when set, this is used as "the version I am running" instead of the
    /// assembly's. It only ever makes the app believe it is <i>older</i> than it is, so the worst it
    /// can do is offer an update to the genuine latest release — but it exists for the isolated
    /// end-to-end test, not for users.
    /// </summary>
    private const string TestVersionEnvVar = "REFRAME_UPDATE_TEST_VERSION";

    /// <summary>
    /// Inno Setup writes its uninstall entry under <c>{AppId}_is1</c>. The AppId is the fixed GUID in
    /// <c>tools\installer\Reframe.iss</c> (where it is spelled <c>{{8F3C…}</c> — the doubled brace is
    /// Inno's escape for a literal <c>{</c>).
    /// </summary>
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C1A47-2E9B-4D6A-9C7F-1B5E0A2D6F84}_is1";

    /// <summary>Staging folder for the download, the unpacked payload, the script and its log.</summary>
    public static string StagingDir => Path.Combine(Path.GetTempPath(), "Reframe-update");

    // One client per role, kept static: HttpClient is meant to be reused (a per-call instance leaks
    // sockets). The API client has the short 10s budget the check needs; the download client gets a
    // long one because a 36 MB installer over a slow line must not be cut off mid-stream.
    private static readonly HttpClient _api = CreateClient(TimeSpan.FromSeconds(10));
    private static readonly HttpClient _download = CreateClient(TimeSpan.FromMinutes(30));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        // Redirects are followed because a release download starts on github.com and lands on GitHub's
        // CDN. HttpClient refuses to follow https -> http, so the transport stays TLS throughout.
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 };
        var client = new HttpClient(handler) { Timeout = timeout };
        // GitHub answers 403 to an API request with no User-Agent, so this header is mandatory.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateAssets.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(UpdateAssets.AcceptHeader);
        return client;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);

    private UpdateService() { }

    /// <summary>
    /// The version this process reports as its own: <c>AssemblyInformationalVersion</c> with any
    /// <c>+metadata</c> suffix (handled by <see cref="UpdateVersion.TryParse"/>), falling back to the
    /// assembly version, and overridable by <see cref="TestVersionEnvVar"/>. Never throws.
    /// </summary>
    public static string CurrentVersionText
    {
        get
        {
            try
            {
                string? overridden = Environment.GetEnvironmentVariable(TestVersionEnvVar);
                if (!string.IsNullOrWhiteSpace(overridden)) return overridden.Trim();

                var asm = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
                string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info;
                return asm.GetName().Version?.ToString() ?? "";
            }
            catch { return ""; }
        }
    }

    /// <summary>
    /// Is this the copy the Inno installer put in Program Files, or a portable unpack?
    /// Read the installer's uninstall key (64-bit view — the installer registers in 64-bit mode) and
    /// compare its <c>InstallLocation</c> with the folder we are actually running from. Anything else
    /// — key missing, value missing, different folder, no permission — means portable, which is the
    /// safe answer: portable mode only ever overwrites files inside our own folder.
    /// </summary>
    public static UpdateInstallMode DetectInstallMode()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(UninstallKeyPath);
            if (key?.GetValue("InstallLocation") is string location &&
                UpdateScript.IsSameDirectory(location, AppContext.BaseDirectory))
            {
                return UpdateInstallMode.Installed;
            }
        }
        catch { /* no key, no access: treat as portable */ }

        return UpdateInstallMode.Portable;
    }

    /// <summary>
    /// Ask GitHub for the latest release and decide whether it is newer than what is running.
    /// Never throws: every failure comes back as <see cref="UpdateCheckStatus.Failed"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json;
            try
            {
                using var response = await _api.GetAsync(UpdateAssets.LatestReleaseApiUrl, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return UpdateCheckResult.Fail($"HTTP {(int)response.StatusCode}");
                json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return UpdateCheckResult.Fail("timed out");
            }
            catch (Exception ex)
            {
                return UpdateCheckResult.Fail(ex.Message);
            }

            return Evaluate(json, CurrentVersionText, DetectInstallMode());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Turn a GitHub "latest release" payload into a verdict. Split out from the HTTP call so the
    /// decision path is straight-line and readable: parse the tag, compare, then look for the single
    /// asset name that is valid for this version and flavour, and validate its URL.
    /// A malformed payload is treated as "up to date" rather than an error — we simply learned nothing.
    /// </summary>
    private static UpdateCheckResult Evaluate(string json, string currentVersion, UpdateInstallMode mode)
    {
        string? tag;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;

            if (!UpdateVersion.TryParse(tag, out var latest)) return UpdateCheckResult.UpToDate();
            if (!UpdateVersion.IsNewer(currentVersion, tag)) return UpdateCheckResult.UpToDate();

            string wanted = UpdateAssets.AssetNameFor(latest, mode);

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!UpdateAssets.IsExpectedAssetName(name, latest, mode)) continue;

                    string? url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (!UpdateAssets.IsTrustedDownloadUrl(url, wanted)) continue; // never relax this

                    long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long parsed) ? parsed : 0;

                    return new UpdateCheckResult
                    {
                        Status = UpdateCheckStatus.UpdateAvailable,
                        Version = latest,
                        Mode = mode,
                        AssetName = wanted,
                        AssetUrl = url,
                        AssetSize = size,
                    };
                }
            }

            // Newer release, but nothing we are willing to download for this flavour: still tell the
            // user a new version exists, just without an automatic path to it.
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                Version = latest,
                Mode = mode,
            };
        }
        catch (JsonException)
        {
            return UpdateCheckResult.UpToDate();
        }
    }

    /// <summary>
    /// Download the release asset, stage it, write the update script and start it. Returns once the
    /// script is running; the caller is then responsible for exiting the app (the script waits for
    /// this PID to disappear before it touches anything).
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> if the result does not pass validation
    /// again here — the check already validated, and this is the deliberate second gate right before
    /// bytes are fetched and a process is started. Network and disk exceptions propagate to the
    /// caller, which reports them in the UI.</para>
    /// </summary>
    /// <param name="result">A result from <see cref="CheckAsync"/> with a non-null <see cref="UpdateCheckResult.AssetUrl"/>.</param>
    /// <param name="progress">0..1 download progress, reported on the caller's context. Optional.</param>
    /// <param name="ct">Cancels the download.</param>
    public async Task ApplyAsync(UpdateCheckResult result, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status != UpdateCheckStatus.UpdateAvailable || result.AssetUrl is null || result.AssetName is null)
            throw new InvalidOperationException("No downloadable update was found.");

        // Re-validate against the version and mode the result claims. Cheap, and it means a
        // hand-constructed result can never smuggle a URL past the rules in UpdateAssets.
        if (!UpdateAssets.IsExpectedAssetName(result.AssetName, result.Version, result.Mode) ||
            !UpdateAssets.IsTrustedDownloadUrl(result.AssetUrl, result.AssetName))
        {
            throw new InvalidOperationException("The update package failed validation and was not downloaded.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string staging = StagingDir;
            ResetStagingDir(staging);

            string packagePath = Path.Combine(staging, result.AssetName);
            await DownloadAsync(result.AssetUrl, packagePath, result.AssetSize, progress, ct).ConfigureAwait(false);

            string logPath = Path.Combine(staging, UpdateScript.LogFileName);
            string scriptPath = Path.Combine(staging, UpdateScript.ScriptFileName);
            string baseDir = AppContext.BaseDirectory;
            int pid = Environment.ProcessId;

            string script;
            if (result.Mode == UpdateInstallMode.Installed)
            {
                // Setup.exe knows its own target folder; relaunch the exe where we are running from,
                // which for the installed flavour is exactly that folder (that is how we detected it).
                script = UpdateScript.BuildInstallerScript(
                    pid, packagePath, Path.Combine(baseDir, "Reframe.exe"), logPath);
            }
            else
            {
                string extracted = Path.Combine(staging, "extracted");
                Directory.CreateDirectory(extracted);
                // .NET refuses entries that would escape the destination, so a tampered zip cannot
                // write outside the staging folder.
                ZipFile.ExtractToDirectory(packagePath, extracted, overwriteFiles: true);

                script = UpdateScript.BuildPortableScript(
                    pid, extracted, baseDir, Path.Combine(baseDir, "Reframe.exe"), logPath);
            }

            // ASCII by construction (see UpdateScript); written without a BOM so Windows PowerShell
            // reads it as plain ASCII.
            await File.WriteAllTextAsync(scriptPath, script, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);

            StartScript(scriptPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Was the previous update attempt left unfinished? Returns the first <c>ERROR</c> line from the
    /// staged log, or null when there is no log or it records no failure. Never throws.
    /// </summary>
    public static string? TryGetLastUpdateError()
    {
        try
        {
            string log = Path.Combine(StagingDir, UpdateScript.LogFileName);
            if (!File.Exists(log)) return null;

            foreach (string line in File.ReadLines(log))
                if (line.Contains("] " + UpdateScript.ErrorMarker, StringComparison.Ordinal))
                    return line;

            return null;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- internals

    /// <summary>
    /// Empty the staging folder so a previous attempt's package, payload and log cannot be confused
    /// with this one's. This deletes only inside our own <c>%TEMP%\Reframe-update</c> folder — the
    /// generated script, which touches the user's installation, deletes nothing at all.
    /// </summary>
    private static void ResetStagingDir(string staging)
    {
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch { /* leftovers are harmless: the download overwrites, the log is appended to */ }

        Directory.CreateDirectory(staging);
    }

    /// <summary>
    /// Stream the asset to disk, reporting progress, and verify the size. Both the
    /// <c>Content-Length</c> of the response and the size the GitHub API reported must match the bytes
    /// actually written; a mismatch deletes the partial file and throws, so a truncated download can
    /// never be handed to the installer.
    /// </summary>
    private static async Task DownloadAsync(string url, string destination, long expectedSize,
                                            IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _download
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        long total = contentLength ?? (expectedSize > 0 ? expectedSize : 0);
        long written = 0;

        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)written / total));
                }
            }

            if (contentLength is > 0 && written != contentLength.Value)
                throw new IOException($"Download is incomplete: got {written} of {contentLength.Value} bytes.");
            if (expectedSize > 0 && written != expectedSize)
                throw new IOException($"Download size mismatch: got {written} bytes, expected {expectedSize}.");
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch { /* best effort */ }
            throw;
        }

        progress?.Report(1.0);
    }

    /// <summary>
    /// Start the update script detached from this process. <c>UseShellExecute=false</c> means the
    /// child inherits our token, so an elevated Reframe hands its elevation straight over and the user
    /// sees no second UAC prompt; <c>-NoProfile</c> keeps a user profile script out of the update path.
    /// </summary>
    private static void StartScript(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Could not start the update helper.");
    }
}
