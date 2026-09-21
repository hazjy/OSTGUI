using System.Net;

namespace OSTGUI.Services;

/// <summary>
/// 游戏封面服务 - 取横版封面并落盘缓存
/// 缓存位置：%LOCALAPPDATA%\OSTGUI\covers\{appid}.jpg
///
/// 三级策略：
///   ① 静态 CDN 链（零请求，覆盖绝大多数游戏）
///   ② 官方 appdetails 取权威 header_image（只对 ① 全失败的 appid：2024+ 新上架游戏在旧布局下是 404，猜不出来）
///   ③ 仍失败 → 写缺失标记 + 记原因，UI 显示占位图标
///
/// 只返回文件路径：BitmapImage 由 ViewModel 在 UI 线程构造（勿在后台线程建 DependencyObject）
/// </summary>
public class CoverImageService
{
    private readonly HttpClient _http;
    private readonly SteamGameInfoService _gameInfo;
    private readonly SemaphoreSlim _gate = new(4);

    private const int MissTtlDays = 1;

    /// <summary>
    /// 缺失标记后缀。⚠️ 改动 URL 链或兜底来源时必须 +1（.miss3 …），
    /// 否则旧标记会在 TTL 内一直挡住新逻辑——2026-09-21 的"封面永远出不来"就是这么来的
    /// </summary>
    private const string MissSuffix = ".miss2";

    /// <summary>静态回退链：旧布局 → 新布局（store_item_assets）；header 为主，capsule 兜比例/缺图</summary>
    private static readonly string[] UrlTemplates =
    {
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/header.jpg",
        "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{0}/header.jpg",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/capsule_616x353.jpg",
        "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{0}/capsule_616x353.jpg",
    };

    /// <summary>不可达的镜像主机 → 等价可达主机（路径与查询串原样保留）。
    /// 本机 hosts 把 akamai / akamaihd / media.steampowered.com 全指向 127.0.0.1，
    /// 而 appdetails 对老游戏返回的 header_image 常常正是这些主机</summary>
    private static readonly (string From, string To)[] HostRewrites =
    {
        ("steamcdn-a.akamaihd.net", "cdn.cloudflare.steamstatic.com"),
        ("media.steampowered.com", "cdn.cloudflare.steamstatic.com"),
        ("cdn.akamai.steamstatic.com", "cdn.cloudflare.steamstatic.com"),
        ("store.akamai.steamstatic.com", "cdn.cloudflare.steamstatic.com"),
        ("community.akamai.steamstatic.com", "cdn.cloudflare.steamstatic.com"),
        ("shared.akamai.steamstatic.com", "shared.cloudflare.steamstatic.com"),
        ("shared.fastly.steamstatic.com", "shared.cloudflare.steamstatic.com"),
    };

    public string CacheDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI", "covers");

    public CoverImageService(HttpClient http, SteamGameInfoService gameInfo)
    {
        _http = http;
        _gameInfo = gameInfo;
    }

    /// <summary>
    /// 取封面本地路径；判定为"无封面"时返回 null
    /// </summary>
    public async Task<string?> EnsureCoverFileAsync(string appId)
    {
        if (!IsAppId(appId)) return null;

        var file = Path.Combine(CacheDir, appId + ".jpg");
        if (File.Exists(file)) return file;

        var miss = Path.Combine(CacheDir, appId + MissSuffix);
        if (File.Exists(miss) && DateTime.Now - File.GetLastWriteTime(miss) < TimeSpan.FromDays(MissTtlDays))
            return null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(file)) return file;   // 等闸期间已被别的调用下好

            // ① 静态链
            var reason = "none";
            foreach (var template in UrlTemplates)
            {
                var (ok, why) = await TryDownloadAsync(string.Format(template, appId), file).ConfigureAwait(false);
                if (ok)
                {
                    TryDelete(miss);
                    Log($"封面已缓存: {appId}");
                    return file;
                }
                reason = why;
                if (why == "net") break;   // 网络异常：后面几条也没意义，且不能记"缺图"
            }

            if (reason == "net")
            {
                Log($"封面暂不可用（网络异常，不记标记）: {appId}");
                return null;
            }

            // ② 官方 appdetails 兜底
            var apiUrl = await _gameInfo.GetHeaderImageUrlAsync(appId).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(apiUrl))
            {
                var (ok, _) = await TryDownloadAsync(apiUrl, file).ConfigureAwait(false);
                if (!ok && TryRewriteHost(apiUrl, out var altUrl))
                    (ok, _) = await TryDownloadAsync(altUrl, file).ConfigureAwait(false);

                if (ok)
                {
                    TryDelete(miss);
                    Log($"封面已缓存(官方源): {appId}");
                    return file;
                }
                reason = "api-url";
            }
            else
            {
                reason = "api-none";
            }

            // ③ 记缺失 + 原因
            Directory.CreateDirectory(CacheDir);
            try { await File.WriteAllBytesAsync(miss, Array.Empty<byte>()).ConfigureAwait(false); }
            catch { }
            Log($"封面缺失（{MissTtlDays} 天内不再重试）: {appId}（{ReasonText(reason)}）");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 下载到目标文件。失败类别：404/403 = 确实没这张图；net = 网络异常；
    /// status/empty = 非 200 或空响应（不确定，但本轮不再试）
    /// </summary>
    private async Task<(bool ok, string reason)> TryDownloadAsync(string url, string file)
    {
        try
        {
            using var resp = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (false, IsDefinitiveMiss(resp.StatusCode) ? "404" : "status");

            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0) return (false, "empty");

            Directory.CreateDirectory(CacheDir);
            try { await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false); }
            catch (IOException) { }   // 并发写同一文件：字节相同，忽略

            return (true, "");
        }
        catch
        {
            return (false, "net");
        }
    }

    /// <summary>把不可达镜像主机换成等价主机；无需改写时返回 false</summary>
    private static bool TryRewriteHost(string url, out string rewritten)
    {
        rewritten = url;
        foreach (var (from, to) in HostRewrites)
        {
            if (url.Contains("://" + from + "/", StringComparison.OrdinalIgnoreCase))
            {
                rewritten = url.Replace("://" + from + "/", "://" + to + "/", StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }
        return false;
    }

    private static string ReasonText(string reason) => reason switch
    {
        "404" => "静态链与官方源都没有这张图",
        "api-none" => "官方接口无图片字段",
        "api-url" => "官方给了 URL 但下不动",
        "status" => "非 200 响应",
        "empty" => "响应为空",
        _ => reason
    };

    private static bool IsDefinitiveMiss(HttpStatusCode code)
        => code is HttpStatusCode.NotFound or HttpStatusCode.Forbidden;

    /// <summary>只接受纯数字 appid；"N/A"（核心配置条目）与 depot 引用一律不请求</summary>
    private static bool IsAppId(string appId)
        => !string.IsNullOrEmpty(appId) && appId.All(char.IsAsciiDigit);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    /// <summary>写日志文件（设置页可打开），不进内存日志栏——封面是后台噪音</summary>
    private static void Log(string message)
        => LogService.AddAppLog($"[Cover] {message}");
}
