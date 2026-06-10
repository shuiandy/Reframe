using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Reframe.Services;

/// <summary>
/// SteamGridDB 在线图标兜底。专做游戏图标的免费服务,Bearer 认证。
/// 端点(已核实 https://www.steamgriddb.com/api/v2):
///   - 基址:https://www.steamgriddb.com/api/v2/
///   - 认证:Authorization: Bearer &lt;key&gt;
///   - 搜索:GET search/autocomplete/{term}     → { success, data: [ { id, name, ... } ] }
///   - 图标:GET icons/game/{gameId}?mimes=...   → { success, data: [ { id, url, thumb, width, height, mime, ... } ] }
///
/// 流程:按名搜索 → 取首个匹配游戏 id → 拉该游戏 icons(优先 png)→ 选 32-64px 的合适项
///       → 下载到 %LOCALAPPDATA%\Reframe\icons\&lt;进程名&gt;.png。
/// 全程 5s 超时 + try/catch,任何失败一律返回 null(调用方静默回落占位)。绝不抛、绝不阻塞 UI 线程
/// (调用方应在后台线程 await)。
/// </summary>
public static class SteamGridDb
{
    private const string Base = "https://www.steamgriddb.com/api/v2/";

    // 单例 HttpClient(避免 socket 耗尽)。超时统一 5s。
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static string IconsDir => Path.Combine(ConfigStore.Dir, "icons");

    /// <summary>进程名(已规范化,小写不含 .exe)对应的本地缓存图标文件路径。</summary>
    public static string CachedIconFile(string normalizedProcessName)
        => Path.Combine(IconsDir, normalizedProcessName + ".png");

    /// <summary>
    /// 取该游戏图标并落盘,返回本地文件路径;失败返回 null。
    /// 已有磁盘缓存则直接返回(不联网)。searchTerms 按优先级尝试(如 Profile.Name 优先,再进程名拆词)。
    /// 须在后台线程调用(内含网络 IO)。
    /// </summary>
    public static async Task<string?> TryFetchIconAsync(
        string apiKey, string normalizedProcessName, IEnumerable<string> searchTerms)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(normalizedProcessName))
            return null;

        string outFile = CachedIconFile(normalizedProcessName);

        // 磁盘已有 → 直接用,绝不再联网。
        try
        {
            if (File.Exists(outFile) && new FileInfo(outFile).Length > 0)
                return outFile;
        }
        catch { /* 探测失败就当没有,继续联网 */ }

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
        catch { /* 网络/解析任意失败:静默 */ }

        return null;
    }

    // ---- 搜索:search/autocomplete/{term} → 首个游戏 id ----
    private static async Task<int?> SearchGameIdAsync(string apiKey, string term)
    {
        try
        {
            string uri = Base + "search/autocomplete/" + Uri.EscapeDataString(term.Trim());
            using var resp = await SendAsync(apiKey, uri).ConfigureAwait(false);
            if (resp is null || !resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

            foreach (var g in data.EnumerateArray())
                if (g.TryGetProperty("id", out var id) && id.TryGetInt32(out int v))
                    return v; // 首个即最相关(autocomplete 已按相关度排序)
        }
        catch { /* 静默 */ }
        return null;
    }

    // ---- 图标:icons/game/{id}?mimes=image/png → 选 32-64px 合适项 ----
    private static async Task<string?> PickIconUrlAsync(string apiKey, int gameId)
    {
        try
        {
            // 限定 png(便于直接落 .png 并交给 WriteableBitmap/BitmapImage 解码)。
            string uri = $"{Base}icons/game/{gameId}?mimes=image/png";
            using var resp = await SendAsync(apiKey, uri).ConfigureAwait(false);
            if (resp is null || !resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
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

                // 偏好 32-64px:落在区间内给最高分(越接近 48 越好);否则离区间越近越好(轻罚)。
                int score;
                if (width >= 32 && width <= 64) score = 1000 - Math.Abs(width - 48);
                else if (width == 0) score = 0; // 无尺寸信息:可用但不优先
                else if (width < 32) score = -100 - (32 - width); // 太小
                else score = -50 - (width - 64);                  // 太大(缩放尚可,优于太小)

                if (score > bestScore)
                {
                    bestScore = score;
                    best = url;
                }
            }
            return best;
        }
        catch { /* 静默 */ }
        return null;
    }

    private static async Task<bool> DownloadAsync(string url, string outFile)
    {
        try
        {
            Directory.CreateDirectory(IconsDir);
            using var req = new HttpRequestMessage(HttpMethod.Get, url); // 图标 CDN 无需 Bearer
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0) return false;

            // 先写临时文件再原子改名,避免半截文件被当成有效缓存。
            string tmp = outFile + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            if (File.Exists(outFile)) File.Delete(outFile);
            File.Move(tmp, outFile);
            return true;
        }
        catch { return false; }
    }

    private static async Task<HttpResponseMessage?> SendAsync(string apiKey, string uri)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return await _http.SendAsync(req).ConfigureAwait(false);
        }
        catch { return null; }
    }
}
