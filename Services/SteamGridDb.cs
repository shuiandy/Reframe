using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Reframe.Services;

/// <summary>
/// SteamGridDB online icon fallback. A free service dedicated to game icons, Bearer auth.
/// Endpoints (verified at https://www.steamgriddb.com/api/v2):
///   - Base: https://www.steamgriddb.com/api/v2/
///   - Auth: Authorization: Bearer &lt;key&gt;
///   - Search: GET search/autocomplete/{term}     → { success, data: [ { id, name, ... } ] }
///   - Icons:  GET icons/game/{gameId}?mimes=...   → { success, data: [ { id, url, thumb, width, height, mime, ... } ] }
///
/// Flow: search by name → take the first matching game id → fetch that game's icons (prefer png) →
///       pick a suitable 32-64px item → download to %LOCALAPPDATA%\Reframe\icons\&lt;process&gt;.png.
/// A 5s timeout + try/catch throughout; any failure returns null (the caller silently falls back to a
/// placeholder). Never throws, never blocks the UI thread (the caller should await on a background thread).
/// </summary>
public static class SteamGridDb
{
    private const string Base = "https://www.steamgriddb.com/api/v2/";

    // Singleton HttpClient (to avoid socket exhaustion). HttpClient.Timeout doesn't cover the chunked
    // reading of the response stream, so each request additionally uses a CancellationTokenSource(5s)
    // to cap the "connected, then dribbles the body slowly" case (see RequestTimeout). HttpClient.Timeout
    // is set to InfiniteTimeSpan, leaving all timing to the CTS (to avoid two timeouts fighting).
    private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    // 2MB icon size cap: blocks an abnormally huge response (guards against OOM / blowing out the disk cache).
    private const long MaxIconBytes = 2 * 1024 * 1024;

    public static string IconsDir => Path.Combine(ConfigStore.Dir, "icons");

    /// <summary>The local cached icon file path for a process name (normalized: lowercase, no .exe).</summary>
    public static string CachedIconFile(string normalizedProcessName)
        => Path.Combine(IconsDir, normalizedProcessName + ".png");

    /// <summary>
    /// Fetch this game's icon, save it to disk, and return the local file path; null on failure.
    /// If a disk cache already exists, return it directly (no network). searchTerms are tried in
    /// priority order (e.g. Profile.Name first, then the camel-split process name). Call on a
    /// background thread (includes network IO).
    /// </summary>
    public static async Task<string?> TryFetchIconAsync(
        string apiKey, string normalizedProcessName, IEnumerable<string> searchTerms)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(normalizedProcessName))
            return null;

        string outFile = CachedIconFile(normalizedProcessName);

        // Already on disk → use it directly, never go online again.
        try
        {
            if (File.Exists(outFile) && new FileInfo(outFile).Length > 0)
                return outFile;
        }
        catch { /* if the probe fails, treat as absent and go online */ }

        try
        {
            foreach (var term in searchTerms)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;

                int? gameId = await SearchGameIdAsync(apiKey, term).ConfigureAwait(false);
                if (gameId is null) continue;

                string? url = await PickIconUrlAsync(apiKey, gameId.Value).ConfigureAwait(false);
                if (url is null) continue;

                if (await DownloadAsync(url, outFile).ConfigureAwait(false))
                    return outFile;
            }
        }
        catch { /* any network/parse failure: silent */ }

        return null;
    }

    // ---- Search: search/autocomplete/{term} → first game id ----
    private static async Task<int?> SearchGameIdAsync(string apiKey, string term)
    {
        try
        {
            string uri = Base + "search/autocomplete/" + Uri.EscapeDataString(term.Trim());
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var resp = await SendAsync(apiKey, uri, cts.Token).ConfigureAwait(false);
            if (resp is null || !resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

            foreach (var g in data.EnumerateArray())
                if (g.TryGetProperty("id", out var id) && id.TryGetInt32(out int v))
                    return v; // the first is the most relevant (autocomplete is already sorted by relevance)
        }
        catch { /* silent (including timeout cancellation) */ }
        return null;
    }

    // ---- Icons: icons/game/{id}?mimes=image/png → pick a suitable 32-64px item ----
    private static async Task<string?> PickIconUrlAsync(string apiKey, int gameId)
    {
        try
        {
            // Restrict to png (so it can be saved directly as .png and handed to WriteableBitmap/BitmapImage to decode).
            string uri = $"{Base}icons/game/{gameId}?mimes=image/png";
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var resp = await SendAsync(apiKey, uri, cts.Token).ConfigureAwait(false);
            if (resp is null || !resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

            string? best = null;
            int bestScore = int.MinValue;
            foreach (var icon in data.EnumerateArray())
            {
                if (!icon.TryGetProperty("url", out var urlEl)) continue;
                string? url = urlEl.GetString();
                if (string.IsNullOrEmpty(url)) continue;

                int width = icon.TryGetProperty("width", out var w) && w.TryGetInt32(out int wv) ? wv : 0;

                // Prefer 32-64px: items inside the range get the highest score (closer to 48 is better); otherwise the nearer the range the better (a light penalty).
                int score;
                if (width >= 32 && width <= 64) score = 1000 - Math.Abs(width - 48);
                else if (width == 0) score = 0; // no size info: usable but not preferred
                else if (width < 32) score = -100 - (32 - width); // too small
                else score = -50 - (width - 64);                  // too large (downscaling is OK, better than too small)

                if (score > bestScore)
                {
                    bestScore = score;
                    best = url;
                }
            }
            return best;
        }
        catch { /* silent */ }
        return null;
    }

    private static async Task<bool> DownloadAsync(string url, string outFile)
    {
        // The tmp name carries a GUID: prevents concurrent downloads of the same-named icon in one process from clobbering each other's tmp.
        string tmp = outFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(IconsDir);
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, url); // the icon CDN needs no Bearer
            // ResponseHeadersRead: get the headers first (so Content-Length can be pre-checked against the cap); the body is still timed by cts.
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            // Content-Length pre-check: reject outright if it declares over 2MB (an abnormally huge image). Without that header, rely on the hard cap during reading below.
            if (resp.Content.Headers.ContentLength is long len && len > MaxIconBytes) return false;

            // Stream and enforce a hard 2MB cap (in case the server omits or lies about Content-Length).
            await using (var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[64 * 1024];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > MaxIconBytes) return false; // over the cap: give up (finally cleans the tmp)
                    await dst.WriteAsync(buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                }
                if (total == 0) return false; // empty body: not a valid cache
            }

            // Atomic overwrite (same volume): File.Move(tmp, out, overwrite:true), to avoid a half-written file being treated as a valid cache.
            File.Move(tmp, outFile, overwrite: true);
            return true;
        }
        catch { return false; }
        finally
        {
            // Clean up the tmp left by a failure/timeout (on the success path tmp was already Move'd away, so Exists is false).
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static async Task<HttpResponseMessage?> SendAsync(string apiKey, string uri, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            // ResponseHeadersRead: return as early as possible (the status code is enough to decide); the body is read by the caller under the same ct.
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch { return null; }
    }
}
