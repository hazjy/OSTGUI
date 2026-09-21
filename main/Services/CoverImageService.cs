using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    /// 落盘宽度（像素）。卡片是 120×56 逻辑像素，200% DPI 的解码上限正好 240×112——
    /// 存原始 460×215 等于 4/5 的字节白存（实测最大一张 125 KB → 10 KB）。
    /// ⚠️ 卡片尺寸改大时：这里改大，并把 <see cref="MigrationMarker"/> 名字 +1（触发重编码）
    /// </summary>
    private const int StoreWidth = 240;

    private const int JpegQuality = 85;

    /// <summary>迁移标记：做过一次就不再重编码（改名 = 触发重编码）</summary>
    private const string MigrationMarker = ".v2";

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

    private readonly object _migrationLock = new();
    private Task? _migration;

    /// <summary>
    /// 一次性迁移：把旧版按原始尺寸（460×215）存的封面重编码成 <see cref="StoreWidth"/>。
    /// 惰性跑一次、跑在后台线程；不联网、不丢数据（就地重写）。
    /// </summary>
    private Task EnsureMigratedAsync()
    {
        if (_migration is not null) return _migration;
        lock (_migrationLock)
            return _migration ??= Task.Run(MigrateOldCoversAsync);
    }

    private async Task MigrateOldCoversAsync()
    {
        var marker = Path.Combine(CacheDir, MigrationMarker);
        try
        {
            if (File.Exists(marker)) return;

            var files = Directory.Exists(CacheDir) ? Directory.GetFiles(CacheDir, "*.jpg") : Array.Empty<string>();
            var done = 0;
            foreach (var file in files)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                    if (TrySaveDownscaled(file, bytes)) done++;
                }
                catch { }
            }

            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(marker, Array.Empty<byte>()).ConfigureAwait(false);
            Log($"封面已迁移为 {StoreWidth}px 宽: 重编码 {done}/{files.Length} 张");
        }
        catch { }
    }

    /// <summary>
    /// 取封面本地路径；判定为"无封面"时返回 null
    /// </summary>
    public async Task<string?> EnsureCoverFileAsync(string appId)
    {
        if (!IsAppId(appId)) return null;

        await EnsureMigratedAsync().ConfigureAwait(false);

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
    /// 取搜索结果缩略图的字节：**只走内存、不落盘**（搜索结果是临时的，缓存反而占盘）。
    /// 取值链：① 搜索结果自带的 ImageUrl → ② 官方 appdetails 的权威 URL（按 AppID 搜索那条路径
    /// 根本不填 ImageUrl，不补这一步就永远没图）→ ③ null（调用方显示占位图标）
    /// </summary>
    public async Task<byte[]?> FetchThumbnailBytesAsync(string appId, string? imageUrl)
    {
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            var bytes = await TryGetBytesAsync(imageUrl).ConfigureAwait(false);
            if (bytes is not null) return bytes;
        }

        if (!IsAppId(appId)) return null;

        var officialUrl = await _gameInfo.GetHeaderImageUrlAsync(appId).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(officialUrl)
            ? null
            : await TryGetBytesAsync(officialUrl).ConfigureAwait(false);
    }

    /// <summary>GET 字节；非 2xx / 异常 / 空响应都返回 null。失败时按主机改写规则重试一次</summary>
    private async Task<byte[]?> TryGetBytesAsync(string url)
    {
        var bytes = await GetBytesOnceAsync(url).ConfigureAwait(false);
        if (bytes is null && TryRewriteHost(url, out var altUrl))
            bytes = await GetBytesOnceAsync(altUrl).ConfigureAwait(false);
        return bytes;
    }

    private async Task<byte[]?> GetBytesOnceAsync(string url)
    {
        try
        {
            using var resp = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return bytes.Length == 0 ? null : bytes;
        }
        catch
        {
            return null;
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
            // 按显示尺寸重编码后落盘；编码失败就原样存（至少能显示，不因压缩把图丢了）
            if (!TrySaveDownscaled(file, bytes))
            {
                try { await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false); }
                catch (IOException) { }   // 并发写同一文件：字节相同，忽略
            }

            return (true, "");
        }
        catch
        {
            return (false, "net");
        }
    }

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");

    /// <summary>
    /// 解码 → 等比缩到 <see cref="StoreWidth"/> → JPEG q85 落盘（只缩不放）。
    /// 失败返回 false，由调用方决定是否原样存。
    /// </summary>
    private static bool TrySaveDownscaled(string path, byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);   // 必须活到 DrawImage 之后
            using var source = Image.FromStream(stream);

            var width = Math.Min(StoreWidth, source.Width);
            var height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));

            using var target = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, width, height);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var quality = new EncoderParameters(1);
            quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)JpegQuality);
            target.Save(path, JpegCodec, quality);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把不可达镜像主机换成等价主机；无需改写时返回 false</summary>
    private static bool TryRewriteHost(string url, out string rewritten)    {
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
