using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 游戏搜索服务 - 以 Steam 官方 API 为主，兼容 CaiGames 备用源
/// </summary>
public class GameSearchService
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, (string name, DateTime time)> _nameCache = new();
    private readonly string _cachePath;

    private const string CaiApiBase = "https://api.9178666.xyz";
    private const string SteamStoreApi = "https://store.steampowered.com/api/appdetails";
    private const string SteamStoreSearchApi = "https://store.steampowered.com/api/storesearch";
    private const string SteamSearchWeb = "https://store.steampowered.com/search/";
    private const string SteamKeywordApi = "https://store.steampowered.com/api/search/keywords/";

    // 名称缓存有效期（天）
    private const int CacheTtlDays = 30;
    // 批量取名时的最大并发请求数，避免触发 Steam API 限流
    private const int BatchMaxConcurrency = 6;
    // 名称搜索最多返回的结果数
    private const int MaxSearchResults = 20;

    private const string SteamNotFoundMessage = "Steam 上未找到该 AppID 对应的游戏";

    // 匹配 Steam 商店搜索页中的游戏链接：<a href="https://store.steampowered.com/app/730/...">...</a>
    private static readonly Regex SteamAppLinkRegex = new(
        @"<a\s+href=""https://store\.steampowered\.com/app/(\d+)/?[^""]*""[^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // 匹配链接内容中的标题：<span class="title">...</span>
    private static readonly Regex TitleSpanRegex = new(
        @"<span[^>]*class=""[^""]*\btitle\b[^""]*""[^>]*>(.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public GameSearchService(HttpClient http)
    {
        _http = http;
        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OSTGUI", "name_cache.json");
        LoadCache();
    }

    /// <summary>
    /// 通过 AppID 搜索游戏信息（Steam 官方 API 优先，CaiGames 备用）
    /// </summary>
    public async Task<SearchResult> SearchByAppIdAsync(string appId)
    {
        if (!int.TryParse(appId, out _))
            return new SearchResult { Success = false, ErrorMessage = "无效的 AppID" };

        // 检查本地缓存（30 天内有效）
        if (TryGetCachedName(appId, out var cachedName))
        {
            return new SearchResult
            {
                AppId = appId,
                Name = cachedName,
                Success = true
            };
        }

        // 1. Steam 官方 Store API 优先
        var steamResult = await SearchBySteamApiAsync(appId);
        if (steamResult.Success)
            return steamResult;

        // Steam 明确返回"未找到"时，无需再尝试备用源
        if (steamResult.ErrorMessage == SteamNotFoundMessage)
            return steamResult;

        // 2. 备用：CaiGames API
        var caiResult = await SearchByCaiApiAsync(appId);
        if (caiResult.Success)
            return caiResult;

        // 所有来源均失败
        return new SearchResult
        {
            AppId = appId,
            Success = false,
            ErrorMessage = $"{steamResult.ErrorMessage}; {caiResult.ErrorMessage}"
        };
    }

    /// <summary>
    /// 通过游戏名称搜索（Steam 官方商店优先，多源兜底）
    /// </summary>
    public async Task<List<SearchResult>> SearchByNameAsync(string query)
    {
        // 1. Steam 官方 storesearch JSON 接口（稳定且相关性准确）
        var results = await SearchSteamStoreSearchAsync(query);

        // 2. 备用：Steam 官方商店搜索页（HTML 解析）
        if (results.Count == 0)
            results = await SearchSteamWebAsync(query);

        // 3. 备用：Steam 关键词搜索接口
        if (results.Count == 0)
            results = await SearchSteamKeywordApiAsync(query);

        // 4. 备用：CaiGames 搜索
        if (results.Count == 0)
            results = await SearchByCaiApiByNameAsync(query);

        // 5. 兜底：如果查询本身就是数字，尝试按 AppID 精确搜索
        if (results.Count == 0 && int.TryParse(query.Trim(), out _))
        {
            var detail = await SearchByAppIdAsync(query.Trim());
            if (detail.Success)
                results.Add(detail);
        }

        return results;
    }

    /// <summary>
    /// 通过 Steam 官方 Store API 按 AppID 搜索
    /// </summary>
    private async Task<SearchResult> SearchBySteamApiAsync(string appId)
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
                CacheName(appId, name);
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
    private async Task<SearchResult> SearchByCaiApiAsync(string appId)
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
                CacheName(appId, name);
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
    /// 批量获取游戏名称
    /// </summary>
    public async Task<Dictionary<string, string>> GetGameNamesBatchAsync(IEnumerable<string> appIds)
    {
        var missing = appIds
            .Where(id => !TryGetCachedName(id, out _))
            .Distinct()
            .ToList();

        // 限制并发，避免同时请求过多触发 Steam API 限流
        if (missing.Count > 0)
        {
            using var gate = new SemaphoreSlim(BatchMaxConcurrency);
            var tasks = missing.Select(async id =>
            {
                await gate.WaitAsync();
                try
                {
                    await SearchByAppIdAsync(id);
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);
        }

        var result = new Dictionary<string, string>();
        foreach (var id in appIds.Distinct())
        {
            if (TryGetCachedName(id, out var name))
                result[id] = name;
        }
        return result;
    }

    /// <summary>
    /// 获取详细游戏信息（含 depots）
    /// </summary>
    public async Task<GameInfo?> GetGameDetailsAsync(string appId)
    {
        try
        {
            var response = await _http.GetAsync($"{SteamStoreApi}?appids={appId}&l=schinese");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appId, out var appData) ||
                !appData.TryGetProperty("success", out var s) || !s.GetBoolean() ||
                !appData.TryGetProperty("data", out var data))
                return null;

            var game = new GameInfo
            {
                AppId = appId,
                Name = data.TryGetProperty("name", out var n) ? n.GetString()! : $"AppID {appId}",
                HeaderImage = data.TryGetProperty("header_image", out var h) ? h.GetString()! : "",
                ShortDescription = data.TryGetProperty("short_description", out var d) ? d.GetString()! : "",
                IsFree = data.TryGetProperty("is_free", out var f) && f.GetBoolean(),
            };

            // DLC列表
            if (data.TryGetProperty("dlc", out var dlcArr))
            {
                foreach (var dlcElem in dlcArr.EnumerateArray())
                    if (dlcElem.TryGetInt32(out var dlcId))
                        game.Dlc.Add(dlcId);
            }

            // Depots - Steam API 格式: {"depots": {"depotId": {"name": "...", "manifests": {"public": {"gid": "..."}}, "encrypted": {"key": "..."}}}}
            if (data.TryGetProperty("depots", out var depotsObj))
            {
                foreach (var prop in depotsObj.EnumerateObject())
                {
                    var depotId = prop.Name;
                    if (!depotId.All(char.IsDigit)) continue; // 跳过非数字键如 "branches"

                    var depotData = prop.Value;
                    var depot = new DepotInfo { DepotId = depotId };

                    if (depotData.TryGetProperty("name", out var depotName))
                        depot.Name = depotName.GetString() ?? "";

                    if (depotData.TryGetProperty("maxsize", out var maxSize))
                        depot.MaxSize = maxSize.GetInt64();

                    // DLC 关联
                    if (depotData.TryGetProperty("dlc", out var dlcAppId))
                        depot.DlcAppId = dlcAppId.GetString() ?? "";

                    // Manifests
                    if (depotData.TryGetProperty("manifests", out var manifestsObj))
                    {
                        if (manifestsObj.TryGetProperty("public", out var publicManifest))
                        {
                            var gid = publicManifest.GetProperty("gid").GetString();
                            if (gid != null)
                                depot.Manifests.Add(gid);
                        }
                    }

                    // 加密密钥 (depot key)
                    if (depotData.TryGetProperty("encrypted", out var encryptedObj))
                    {
                        if (encryptedObj.TryGetProperty("key", out var keyElem))
                            depot.DecryptionKey = keyElem.GetString() ?? "";
                    }

                    game.Depots[depotId] = depot;
                }
            }

            // 开发商/发行商
            if (data.TryGetProperty("developers", out var devArr))
                foreach (var devElem in devArr.EnumerateArray())
                    game.Developers.Add(devElem.GetString()!);

            if (data.TryGetProperty("publishers", out var pubArr))
                foreach (var pubElem in pubArr.EnumerateArray())
                    game.Publishers.Add(pubElem.GetString()!);

            return game;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取游戏的 DLC 信息
    /// </summary>
    public async Task<List<DlcInfo>> GetDlcInfoAsync(string appId)
    {
        var dlcList = new List<DlcInfo>();
        try
        {
            var game = await GetGameDetailsAsync(appId);
            if (game == null) return dlcList;

            // 批量获取 DLC 名称（内部走缓存 + 限并发）
            var names = await GetGameNamesBatchAsync(game.Dlc.Select(d => d.ToString()));

            foreach (var dlcId in game.Dlc)
            {
                dlcList.Add(new DlcInfo
                {
                    AppId = dlcId.ToString(),
                    Name = names.TryGetValue(dlcId.ToString(), out var dlcName) && !string.IsNullOrEmpty(dlcName)
                        ? dlcName
                        : $"DLC {dlcId}"
                });
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("GetDlcInfoAsync error: " + ex.Message); }
        return dlcList;
    }

    /// <summary>
    /// 获取单个游戏名称（带缓存）
    /// </summary>
    public async Task<string?> GetGameNameAsync(string appId)
    {
        if (TryGetCachedName(appId, out var cachedName))
            return cachedName;

        var result = await SearchByAppIdAsync(appId);
        return result.Success && !string.IsNullOrEmpty(result.Name) ? result.Name : null;
    }

    /// <summary>
    /// 从 Steam 链接解析 AppID
    /// </summary>
    public static string? ParseAppIdFromUrl(string url)
    {
        // https://store.steampowered.com/app/730/
        var match = Regex.Match(url, @"/app/(\d+)");
        if (match.Success) return match.Groups[1].Value;

        // steam://rungameid/730
        match = Regex.Match(url, @"rungameid/(\d+)");
        if (match.Success) return match.Groups[1].Value;

        return null;
    }

    /// <summary>
    /// 通过 Steam 官方 storesearch JSON 接口搜索（主源）
    /// </summary>
    private async Task<List<SearchResult>> SearchSteamStoreSearchAsync(string query)
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
                CacheName(appId, name);

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
    private async Task<List<SearchResult>> SearchSteamWebAsync(string query)
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
                CacheName(appId, name);

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
    private async Task<List<SearchResult>> SearchSteamKeywordApiAsync(string query)
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
                CacheName(appId, name);

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
    private async Task<List<SearchResult>> SearchByCaiApiByNameAsync(string query)
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
                CacheName(appId, name);

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

    /// <summary>
    /// 尝试读取未过期的名称缓存
    /// </summary>
    private bool TryGetCachedName(string appId, out string name)
    {
        if (_nameCache.TryGetValue(appId, out var cached) &&
            DateTime.Now - cached.time < TimeSpan.FromDays(CacheTtlDays))
        {
            name = cached.name;
            return true;
        }
        name = "";
        return false;
    }

    /// <summary>
    /// 写入名称缓存并落盘
    /// </summary>
    private void CacheName(string appId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        _nameCache[appId] = (name, DateTime.Now);
        SaveCache();
    }

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                if (data != null)
                {
                    foreach (var (key, entry) in data)
                    {
                        // 只加载未过期的缓存
                        if (!string.IsNullOrEmpty(entry.Name) &&
                            DateTime.Now - entry.Time < TimeSpan.FromDays(CacheTtlDays))
                            _nameCache[key] = (entry.Name, entry.Time);
                    }
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("加载名称缓存失败: " + ex.Message); }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var data = _nameCache.ToDictionary(k => k.Key, v => new CacheEntry { Name = v.Value.name, Time = v.Value.time });
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(data));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("保存名称缓存失败: " + ex.Message); }
    }

    private class CaiApiResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
        [JsonPropertyName("data")]
        public JsonElement Data { get; set; }
    }

    private class CacheEntry
    {
        public string Name { get; set; } = "";
        public DateTime Time { get; set; }
    }
}
