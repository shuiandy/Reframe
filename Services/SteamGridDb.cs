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

    // 单例 HttpClient(避免 socket 耗尽)。HttpClient.Timeout 不覆盖响应流的逐块读取,
    // 故每次请求另用 CancellationTokenSource(5s) 兜住"连上后慢慢吐 body"的情形(见 RequestTimeout)。
    // 这里把 HttpClient.Timeout 设为 InfiniteTimeSpan,完全交给 CTS 统一控时(避免两套超时打架)。
    private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    // 图标体积上限 2MB:挡住异常巨大的响应(防 OOM / 写爆磁盘缓存)。
    private const long MaxIconBytes = 2 * 1024 * 1024;

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
                    return v; // 首个即最相关(autocomplete 已按相关度排序)
        }
        catch { /* 静默(含超时取消) */ }
        return null;
    }

    // ---- 图标:icons/game/{id}?mimes=image/png → 选 32-64px 合适项 ----
    private static async Task<string?> PickIconUrlAsync(string apiKey, int gameId)
    {
        try
        {
            // 限定 png(便于直接落 .png 并交给 WriteableBitmap/BitmapImage 解码)。
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
        // tmp 带 GUID:防同一进程内并发下载同名图标互相踩 tmp。
        string tmp = outFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(IconsDir);
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, url); // 图标 CDN 无需 Bearer
            // ResponseHeadersRead:先拿到头(可读 Content-Length 做上限预检),body 仍受 cts 控时。
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            // Content-Length 预检:声明超 2MB 直接拒(异常巨图)。无该头则靠下面读取时的硬上限兜底。
            if (resp.Content.Headers.ContentLength is long len && len > MaxIconBytes) return false;

            // 流式读取并强制 2MB 上限(防服务器不报或谎报 Content-Length)。
            await using (var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[64 * 1024];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > MaxIconBytes) return false; // 超限:放弃(finally 清 tmp)
                    await dst.WriteAsync(buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                }
                if (total == 0) return false; // 空 body:不当有效缓存
            }

            // 原子覆盖(同卷):File.Move(tmp, out, overwrite:true),避免半截文件被当成有效缓存。
            File.Move(tmp, outFile, overwrite: true);
            return true;
        }
        catch { return false; }
        finally
        {
            // 失败/超时残留的 tmp 清掉(成功路径 tmp 已被 Move 走,Exists 为 false)。
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static async Task<HttpResponseMessage?> SendAsync(string apiKey, string uri, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            // ResponseHeadersRead:尽早返回(状态码即可判断),body 由调用方在同一 ct 下读取。
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch { return null; }
    }
}
