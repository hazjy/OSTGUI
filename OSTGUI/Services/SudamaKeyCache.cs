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

    private const string SudamaApiUrl = "https://api.993499094.xyz/depotkeys.json";
    private const string SudamaTokensUrl = "https://api.993499094.xyz/appaccesstokens.json";

    public SudamaKeyCache(HttpClient http)
    {
        _http = http;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[SudamaKeyCache] {message}");
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
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return TryLoadStaleCache(cachePath);

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            if (data.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    var cache = new SudamaCache { Timestamp = DateTime.UtcNow, Data = data };
                    await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache));
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
