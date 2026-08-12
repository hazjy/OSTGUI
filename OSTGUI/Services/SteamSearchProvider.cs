using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 搜索源实现 - Steam 官方 API 与 CaiGames 备用源
/// </summary>
public class SteamSearchProvider
{
    private readonly HttpClient _http;
    private readonly GameNameCacheService _nameCache;

    private const string CaiApiBase = "https://api.9178666.xyz";
    private const string SteamStoreApi = "https://store.steampowered.com/api/appdetails";
    private const string SteamStoreSearchApi = "https://store.steampowered.com/api/storesearch";
    private const string SteamSearchWeb = "https://store.steampowered.com/search/";
    private const string SteamKeywordApi = "https://store.steampowered.com/api/search/keywords/";

    /// <summary>名称搜索最多返回的结果数</summary>
    private const int MaxSearchResults = 20;

    public const string SteamNotFoundMessage = "Steam 上未找到该 AppID 对应的游戏";

    // 匹配 Steam 商店搜索页中的游戏链接：<a href="https://store.steampowered.com/app/730/...">...</a>
    private static readonly Regex SteamAppLinkRegex = new(
        @"<a\s+href=""https://store\.steampowered\.com/app/(\d+)/?[^""]*""[^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // 匹配链接内容中的标题：<span class="title">...</span>
    private static readonly Regex TitleSpanRegex = new(
        @"<span[^>]*class=""[^""]*\btitle\b[^""]*""[^>]*>(.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public SteamSearchProvider(HttpClient http, GameNameCacheService nameCache)
    {
        _http = http;
        _nameCache = nameCache;
    }

    /// <summary>
    /// 通过 Steam 官方 Store API 按 AppID 搜索
    /// </summary>
    public async Task<SearchResult> SearchBySteamApiAsync(string appId)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync($"{SteamStoreApi}?appids={appId}&l=schinese");
        }
        catch (Exception ex)
        {
            return new SearchResult { AppId = appId, Success = false, ErrorMessage = $"Steam API 请求失败: {ex.Message}" };
        }

        if (!response.IsSuccessStatusCode)
            return new SearchResult { AppId = appId, Success = false, ErrorMessage = $"Steam API 返回 HTTP {(int)response.StatusCode}" };

        try
        {
            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty(appId, out var appData))
                return new SearchResult { AppId = appId, Success = false, ErrorMessage = "Steam API 响应格式异常" };

            // 明确返回未找到
            if (!appData.TryGetProperty("success", out var success) || !success.GetBoolean())
                return new SearchResult { AppId = appId, Success = false, ErrorMessage = SteamNotFoundMessage };

            if (appData.TryGetProperty("data", out var data) &&
                data.TryGetProperty("name", out var nameElem) &&
                !string.IsNullOrEmpty(nameElem.GetString()))
            {
                var name = nameElem.GetString()!;
                _nameCache.Set(appId, name);
                return new SearchResult { AppId = appId, Name = name, Success = true };
            }

            return new SearchResult { AppId = appId, Success = false, ErrorMessage = "Steam API 未返回游戏名称" };
        }
        catch (Exception ex)
        {
            return new SearchResult { AppId = appId, Success = false, ErrorMessage = $"Steam API 响应解析失败: {ex.Message}" };
        }
    }

    /// <summary>
    /// 通过 CaiGames API 按 AppID 搜索（备用源）
    /// </summary>
    public async Task<SearchResult> SearchByCaiApiAsync(string appId)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{CaiApiBase}/cmd/{appId}");
            request.Headers.TryAddWithoutValidation("X-Client-Auth", "CaiGames-pvzcxw");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new SearchResult { AppId = appId, Success = false, ErrorMessage = $"备用源返回 HTTP {(int)response.StatusCode}" };

            var raw = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<CaiApiResponse>(raw);
            if (data?.Success == true &&
                data.Data.TryGetProperty("name", out var nameElem) &&
                !string.IsNullOrEmpty(nameElem.GetString()))
            {
                var name = nameElem.GetString()!;
                _nameCache.Set(appId, name);
                return new SearchResult { AppId = appId, Name = name, Success = true };
            }

            return new SearchResult { AppId = appId, Success = false, ErrorMessage = "备用源未返回游戏名称" };
        }
        catch (Exception ex)
        {
            return new SearchResult { AppId = appId, Success = false, ErrorMessage = $"备用源请求失败: {ex.Message}" };
        }
    }

    /// <summary>
    /// 通过 Steam 官方 storesearch JSON 接口搜索（主源）
    /// </summary>
    public async Task<List<SearchResult>> SearchStoreSearchAsync(string query)
    {
        var results = new List<SearchResult>();
        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"{SteamStoreSearchApi}?term={encodedQuery}&l=schinese&cc=cn";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return results;

            var json = await response.Content.ReadAsStringAsync();
            if (!json.StartsWith("{"))
                return results;

            var root = JsonSerializer.Deserialize<JsonElement>(json);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var itemsArr))
                return results;

            foreach (var item in itemsArr.EnumerateArray())
            {
                // 只保留应用本体，过滤音乐包/合集等衍生条目
                if (item.TryGetProperty("type", out var typeElem) &&
                    typeElem.ValueKind == JsonValueKind.String &&
                    typeElem.GetString() != "app")
                    continue;

                var appId = item.TryGetProperty("id", out var idElem)
                    ? idElem.ValueKind == JsonValueKind.Number ? idElem.GetInt32().ToString() : idElem.GetString() ?? ""
                    : "";
                var name = item.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : "";
                var image = item.TryGetProperty("tiny_image", out var imgElem) ? imgElem.GetString() : "";

                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name))
                    continue;

                results.Add(new SearchResult
                {
                    AppId = appId,
                    Name = name,
                    ImageUrl = image ?? "",
                    Success = true
                });
                _nameCache.Set(appId, name);

                if (results.Count >= MaxSearchResults)
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Steam storesearch 失败: " + ex.Message);
        }
        return results;
    }

    /// <summary>
    /// 通过 Steam 官方商店搜索页搜索（HTML 解析）
    /// </summary>
    public async Task<List<SearchResult>> SearchWebAsync(string query)
    {
        var results = new List<SearchResult>();
        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"{SteamSearchWeb}?term={encodedQuery}&l=schinese&cc=cn&ndl=1";
            // 商店搜索页体积较大，单独限制超时，避免拖慢整体搜索
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await _http.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
                return results;

            var html = await response.Content.ReadAsStringAsync();
            var seen = new HashSet<string>();

            foreach (Match match in SteamAppLinkRegex.Matches(html))
            {
                var appId = match.Groups[1].Value;
                if (!seen.Add(appId))
                    continue;

                var titleMatch = TitleSpanRegex.Match(match.Groups[2].Value);
                var name = titleMatch.Success
                    ? System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim()
                    : "";

                if (string.IsNullOrEmpty(name))
                    continue;

                results.Add(new SearchResult
                {
                    AppId = appId,
                    Name = name,
                    ImageUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
                    Success = true
                });
                _nameCache.Set(appId, name);

                if (results.Count >= MaxSearchResults)
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Steam 商店搜索失败: " + ex.Message);
        }
        return results;
    }

    /// <summary>
    /// 通过 Steam 关键词搜索接口搜索（备用源 1）
    /// </summary>
    public async Task<List<SearchResult>> SearchKeywordApiAsync(string query)
    {
        var results = new List<SearchResult>();
        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"{SteamKeywordApi}?keyword={encodedQuery}&cc=us&l=english";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return results;

            var json = await response.Content.ReadAsStringAsync();
            if (!(json.StartsWith("{") || json.StartsWith("[")))
                return results;

            var root = JsonSerializer.Deserialize<JsonElement>(json);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var itemsArr))
                return results;

            foreach (var item in itemsArr.EnumerateArray())
            {
                var appId = item.TryGetProperty("id", out var idElem)
                    ? idElem.ValueKind == JsonValueKind.Number ? idElem.GetInt32().ToString() : idElem.GetString() ?? ""
                    : "";
                var name = item.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : "";
                var logo = item.TryGetProperty("tiny_image", out var imgElem) ? imgElem.GetString() : "";

                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name))
                    continue;

                results.Add(new SearchResult
                {
                    AppId = appId,
                    Name = name,
                    ImageUrl = logo ?? "",
                    Success = true
                });
                _nameCache.Set(appId, name);

                if (results.Count >= MaxSearchResults)
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Steam 关键词搜索失败: " + ex.Message);
        }
        return results;
    }

    /// <summary>
    /// 通过 CaiGames API 按名称搜索（备用源 2）
    /// </summary>
    public async Task<List<SearchResult>> SearchByCaiApiByNameAsync(string query)
    {
        var results = new List<SearchResult>();
        try
        {
            var url = $"{CaiApiBase}/search?term={Uri.EscapeDataString(query)}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-Client-Auth", "CaiGames-pvzcxw");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return results;

            var raw = await response.Content.ReadAsStringAsync();
            if (!(raw.StartsWith("{") || raw.StartsWith("[")))
                return results;

            var root = JsonSerializer.Deserialize<JsonElement>(raw);
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var dataArr) ||
                dataArr.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var item in dataArr.EnumerateArray())
            {
                var appId = item.TryGetProperty("appid", out var idElem)
                    ? idElem.ValueKind == JsonValueKind.Number ? idElem.GetInt32().ToString() : idElem.GetString() ?? ""
                    : "";
                var name = item.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : "";
                var image = item.TryGetProperty("image", out var imgElem) ? imgElem.GetString() : "";

                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name))
                    continue;

                results.Add(new SearchResult
                {
                    AppId = appId,
                    Name = name,
                    ImageUrl = image ?? "",
                    Success = true
                });
                _nameCache.Set(appId, name);

                if (results.Count >= MaxSearchResults)
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("CaiGames 搜索失败: " + ex.Message);
        }
        return results;
    }

    private class CaiApiResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
        [JsonPropertyName("data")]
        public JsonElement Data { get; set; }
    }
}
