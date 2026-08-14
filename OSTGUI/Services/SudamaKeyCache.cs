using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;
/// <summary>
/// Sudama 密钥缓存服务 - 下载并缓存 depot key 与 App 访问令牌
/// </summary>
public class SudamaKeyCache
{
    private readonly HttpClient _http;
    private readonly ConfigService _configService;

    private const string SudamaApiUrl = "https://api.993499094.xyz/depotkeys.json";
    private const string SudamaTokensUrl = "https://api.993499094.xyz/appaccesstokens.json";

    public SudamaKeyCache(HttpClient http, ConfigService configService)
    {
        _http = http;
        _configService = configService;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[SudamaKeyCache] {message}");
    }

    /// <summary>
    /// 下载超时（秒）：与设置里的下载超时联动，至少 300 秒（全量文件较大，网络波动时留足余量）
    /// </summary>
    private int DownloadTimeoutSeconds => Math.Max(300, _configService.Config.DownloadTimeout);

    /// <summary>
    /// 带重试的下载：失败自动重试一次，返回成功响应；均失败返回 null
    /// </summary>
    private async Task<HttpResponseMessage?> TryGetWithRetryAsync(HttpClient client, string url, string label)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var resp = await client.GetAsync(url).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return resp;
                Log($"{label}下载失败 HTTP {(int)resp.StatusCode}" + (attempt == 1 ? "，自动重试中..." : ""));
                resp.Dispose();
            }
            catch (Exception ex)
            {
                Log($"{label}下载异常: {ex.Message}" + (attempt == 1 ? "，自动重试中..." : ""));
            }
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

    /// <summary>
    /// 手动强制刷新缓存（忽略 24h TTL，立即重新下载密钥与令牌并覆盖本地缓存）
    /// </summary>
    public async Task<(bool ok, string message)> RefreshAsync()
    {
        var (keysOk, keysMsg) = await ForceRefreshAsync("sudama_cache.json", SudamaApiUrl, "Sudama 密钥");
        var (tokensOk, tokensMsg) = await ForceRefreshAsync("token_cache.json", SudamaTokensUrl, "App 访问令牌");

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
            using var dlClient = new HttpClient { Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds) };
            using var response = await TryGetWithRetryAsync(dlClient, url, label);
            if (response == null)
            {
                var stale = TryLoadStaleCache(cachePath);
                return (stale.Count > 0,
                    $"{label}下载失败（已重试）" + (stale.Count > 0 ? "，已保留旧缓存" : "，且无可用旧缓存"));
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            if (data.Count == 0)
                return (false, $"{label}返回空数据，未更新");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                var cache = new SudamaCache { Timestamp = DateTime.UtcNow, Data = data };
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache)).ConfigureAwait(false);
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
        try
        {
            using var dlClient = new HttpClient { Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds) };
            using var response = await TryGetWithRetryAsync(dlClient, url, label);
            if (response == null)
                return TryLoadStaleCache(cachePath);

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            if (data.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    var cache = new SudamaCache { Timestamp = DateTime.UtcNow, Data = data };
                    await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache)).ConfigureAwait(false);
                }
                catch { }
            }
            return data;
        }
        catch
        {
            return TryLoadStaleCache(cachePath);
        }
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

    /// <summary>
    /// 统一生成完整 Lua：addappid(主游戏) + addappid(各 depot，自动补 key) + setManifestid(固定版本) + addtoken(访问令牌)
    /// </summary>


}
public class SudamaCache
{
    public DateTime Timestamp { get; set; }
    public Dictionary<string, string> Data { get; set; } = new();
}
