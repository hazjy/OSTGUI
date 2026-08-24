using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;
/// <summary>
/// Sudama 密钥缓存服务 - 下载并缓存 depot key 与 App 访问令牌
/// </summary>
public class SudamaKeyCache
{
    private readonly ConfigService _configService;

    private const string SudamaApiUrl = "https://api.993499094.xyz/depotkeys.json";
    private const string SudamaTokensUrl = "https://api.993499094.xyz/appaccesstokens.json";

    public SudamaKeyCache(ConfigService configService)
    {
        _configService = configService;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[SudamaKeyCache] {message}");
    }

    /// <summary>
    /// 单次尝试超时（秒）：与设置里的下载超时联动。
    /// gzip 通道实测 MB/s 级，120 秒余量已远超全量文件所需；
    /// 之前固定 300 秒会让卡死的连接挂满 5 分钟才报错，用户只觉得"慢+失败"。
    /// </summary>
    private int DownloadTimeoutSeconds => Math.Max(120, _configService.Config.DownloadTimeout);

    /// <summary>
    /// 创建启用自动解压的下载客户端：
    /// Sudama 明文传输极慢（实测约 23KB/s），gzip 压缩后 MB/s 级。
    /// 注意不能启用 Brotli：服务器对含 br 的协商返回 brotli，而 br 通道极慢，
    /// 只协商 gzip/deflate 才能走快通道
    /// </summary>
    private static HttpClient CreateDownloadClient(int timeoutSeconds)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    /// <summary>
    /// 带重试的流式下载：边收边写入内存流（避免整包 byte[] + 大字符串双重复制），
    /// 失败间隔 1.5s 重试一次。成功返回解析后的字典，均失败返回 null。
    /// </summary>
    private async Task<Dictionary<string, string>?> DownloadJsonAsync(string url, string label)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = CreateDownloadClient(DownloadTimeoutSeconds);
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"{label}下载失败 HTTP {(int)resp.StatusCode}" + (attempt == 1 ? "，1.5s 后重试..." : ""));
                }
                else
                {
                    using var ms = new MemoryStream();
                    await resp.Content.CopyToAsync(ms).ConfigureAwait(false);
                    sw.Stop();
                    ms.Position = 0;
                    var data = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(ms).ConfigureAwait(false);
                    if (data is not { Count: > 0 })
                    {
                        Log($"{label}返回空数据");
                        return null;
                    }
                    Log($"{label}下载完成：{data.Count} 条，{ms.Length / 1024:N0} KB，耗时 {sw.Elapsed.TotalSeconds:F1}s");
                    return data;
                }
            }
            catch (Exception ex)
            {
                Log($"{label}下载异常({sw.Elapsed.TotalSeconds:F0}s): {ex.Message}" + (attempt == 1 ? "，1.5s 后重试..." : ""));
            }

            if (attempt == 1)
                await Task.Delay(1500).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<Dictionary<string, string>> GetSudamaKeysAsync()
    {
        return await GetCachedJsonAsync("sudama_cache.json", SudamaApiUrl, "Sudama 密钥");
    }

    /// <summary>
    /// 从 Sudama API 获取全量 App 访问令牌（24h 缓存）
    /// </summary>

    public async Task<Dictionary<string, string>> GetAccessTokensAsync()
    {
        return await GetCachedJsonAsync("token_cache.json", SudamaTokensUrl, "App 访问令牌");
    }

    private static string CacheFilePath(string cacheFileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI", cacheFileName);

    private static async Task WriteCacheAsync(string cacheFileName, Dictionary<string, string> data)
    {
        var cachePath = CacheFilePath(cacheFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var cache = new SudamaCache { Timestamp = DateTime.UtcNow, Data = data };
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache)).ConfigureAwait(false);
    }

    /// <summary>
    /// 手动导入本地下载的缓存文件（浏览器直连下载通常远快于应用内下载）。
    /// 文件名含 depotkey/token 即可自动识别类型；识别不出时按内容启发：
    /// 密钥值均为 64 位十六进制，令牌为长数字串。兼容包装格式 {"Data":{...}}，
    /// 也兼容明文 {"id":"value"} 字典。导入成功即重置 24h 缓存计时。
    /// </summary>
    public async Task<(bool ok, string message)> ImportFilesAsync(IEnumerable<string> filePaths)
    {
        var msgs = new List<string>();
        var okCount = 0;
        var total = 0;

        foreach (var path in filePaths)
        {
            total++;
            var name = Path.GetFileName(path);
            try
            {
                if (!File.Exists(path)) { msgs.Add($"{name}：文件不存在"); continue; }
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);

                Dictionary<string, string>? data = null;
                try
                {
                    var wrapper = JsonSerializer.Deserialize<SudamaCache>(json);
                    if (wrapper?.Data is { Count: > 0 }) data = wrapper.Data;
                }
                catch { }
                data ??= JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data is not { Count: > 0 }) { msgs.Add($"{name}：未解析出有效数据"); continue; }

                var kind = DetectKind(name, data);
                if (kind == null) { msgs.Add($"{name}：无法识别是密钥还是令牌"); continue; }

                await WriteCacheAsync(
                    kind == "keys" ? "sudama_cache.json" : "token_cache.json", data).ConfigureAwait(false);
                okCount++;
                msgs.Add($"{name} → {(kind == "keys" ? "密钥" : "访问令牌")}已导入（{data.Count} 条）");
            }
            catch (Exception ex)
            {
                msgs.Add($"{name}：导入失败 {ex.Message}");
            }
        }

        return (okCount > 0 && okCount == total, string.Join("；", msgs));
    }

    private static string? DetectKind(string fileName, Dictionary<string, string> data)
    {
        var n = fileName.ToLowerInvariant();
        if (n.Contains("depotkey")) return "keys";
        if (n.Contains("token")) return "tokens";

        var sample = data.Values.Take(20).ToList();
        var hex64 = sample.Count(v => v.Length == 64 && v.All(Uri.IsHexDigit));
        if (sample.Count > 0 && hex64 == sample.Count) return "keys";
        if (sample.Count > 0 && hex64 == 0) return "tokens";
        return null;
    }

    /// <summary>
    /// 手动强制刷新缓存：密钥与令牌并行下载（互不阻塞），忽略 24h TTL
    /// </summary>
    public async Task<(bool ok, string message)> RefreshAsync()
    {
        var keysTask = ForceRefreshAsync("sudama_cache.json", SudamaApiUrl, "Sudama 密钥");
        var tokensTask = ForceRefreshAsync("token_cache.json", SudamaTokensUrl, "App 访问令牌");
        await Task.WhenAll(keysTask, tokensTask).ConfigureAwait(false);

        var (keysOk, keysMsg) = keysTask.Result;
        var (tokensOk, tokensMsg) = tokensTask.Result;

        if (keysOk && tokensOk)
            return (true, $"Sudama 缓存已更新：{keysMsg}；{tokensMsg}");
        if (keysOk || tokensOk)
            return (false, $"Sudama 缓存刷新不完整：{keysMsg}；{tokensMsg}");
        return (false, $"Sudama 缓存刷新失败：{keysMsg}；{tokensMsg}");
    }

    /// <summary>
    /// 强制下载单个缓存文件并覆盖本地缓存；失败时尝试保留旧缓存
    /// </summary>
    private async Task<(bool ok, string message)> ForceRefreshAsync(
        string cacheFileName, string url, string label)
    {
        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OSTGUI", cacheFileName);

        Log($"正在刷新 {label}...");
        try
        {
            var data = await DownloadJsonAsync(url, label).ConfigureAwait(false);
            if (data == null)
            {
                var stale = TryLoadStaleCache(cachePath);
                return (stale.Count > 0,
                    $"{label}下载失败（已重试）" + (stale.Count > 0 ? "，已保留旧缓存" : "，且无可用旧缓存"));
            }

            try
            {
                await WriteCacheAsync(cacheFileName, data).ConfigureAwait(false);
            }
            catch { }

            return (true, $"{label}已更新（{data.Count} 条）");
        }
        catch (Exception ex)
        {
            var stale = TryLoadStaleCache(cachePath);
            return (stale.Count > 0,
                $"{label}刷新异常: {ex.Message}，已保留旧缓存");
        }
    }

    /// <summary>
    /// 通用缓存 JSON 下载（24h 有效，失败时尽量用旧缓存）
    /// </summary>

    private async Task<Dictionary<string, string>> GetCachedJsonAsync(string cacheFileName, string url, string label)
    {
        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OSTGUI", cacheFileName);

        // 尝试读取缓存
        if (File.Exists(cachePath))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(cachePath);
                var cache = JsonSerializer.Deserialize<SudamaCache>(cachedJson);
                if (cache != null && DateTime.UtcNow.Subtract(cache.Timestamp).TotalHours < 24)
                {
                    Log($"使用本地缓存的 {label}");
                    return cache.Data;
                }
            }
            catch { }
        }

        // 下载新数据
        Log($"正在下载 {label}...");
        var data = await DownloadJsonAsync(url, label).ConfigureAwait(false);
        if (data != null)
        {
            try
            {
                await WriteCacheAsync(cacheFileName, data).ConfigureAwait(false);
            }
            catch { }
            return data;
        }
        return TryLoadStaleCache(cachePath);
    }

    /// <summary>
    /// 下载失败时尝试使用过期缓存兜底
    /// </summary>

    private static Dictionary<string, string> TryLoadStaleCache(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                var cache = JsonSerializer.Deserialize<SudamaCache>(File.ReadAllText(cachePath));
                if (cache?.Data != null && cache.Data.Count > 0)
                    return cache.Data;
            }
        }
        catch { }
        return new();
    }


}
public class SudamaCache
{
    public DateTime Timestamp { get; set; }
    public Dictionary<string, string> Data { get; set; } = new();
}
