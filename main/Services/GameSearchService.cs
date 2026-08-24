using System.Text.RegularExpressions;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 游戏搜索服务 - 搜索编排与名称批量查询（搜索源见 SteamSearchProvider，缓存见 GameNameCacheService）
/// </summary>
public class GameSearchService
{
    private readonly SteamSearchProvider _search;
    private readonly GameNameCacheService _nameCache;

    // 批量取名时的最大并发请求数，避免触发 Steam API 限流
    private const int BatchMaxConcurrency = 6;

    public GameSearchService(SteamSearchProvider search, GameNameCacheService nameCache)
    {
        _search = search;
        _nameCache = nameCache;
    }

    /// <summary>
    /// 通过 AppID 搜索游戏信息（Steam 官方 API）
    /// </summary>
    public async Task<SearchResult> SearchByAppIdAsync(string appId)
    {
        if (!int.TryParse(appId, out _))
            return new SearchResult { Success = false, ErrorMessage = "无效的 AppID" };

        // 检查本地缓存（30 天内有效）
        if (_nameCache.TryGet(appId, out var cachedName))
        {
            return new SearchResult
            {
                AppId = appId,
                Name = cachedName,
                Success = true
            };
        }

        // 1. Steam 官方 Store API 优先
        var steamResult = await _search.SearchBySteamApiAsync(appId);
        if (steamResult.Success)
            return steamResult;

        // Steam 明确返回"未找到"时，无需再尝试备用源
        if (steamResult.ErrorMessage == SteamSearchProvider.SteamNotFoundMessage)
            return steamResult;

        // 所有来源均失败
        return new SearchResult
        {
            AppId = appId,
            Success = false,
            ErrorMessage = steamResult.ErrorMessage
        };
    }

    /// <summary>
    /// 通过游戏名称搜索（Steam 官方商店优先，多源兜底）
    /// </summary>
    public async Task<List<SearchResult>> SearchByNameAsync(string query)
    {
        // 1. Steam 官方 storesearch JSON 接口（稳定且相关性准确）
        var results = await _search.SearchStoreSearchAsync(query);

        // 2. 备用：Steam 官方商店搜索页（HTML 解析）
        if (results.Count == 0)
            results = await _search.SearchWebAsync(query);

        // 3. 备用：Steam 关键词搜索接口
        if (results.Count == 0)
            results = await _search.SearchKeywordApiAsync(query);

        // 4. 兜底：如果查询本身就是数字，尝试按 AppID 精确搜索
        if (results.Count == 0 && int.TryParse(query.Trim(), out _))
        {
            var detail = await SearchByAppIdAsync(query.Trim());
            if (detail.Success)
                results.Add(detail);
        }

        return results;
    }

    /// <summary>
    /// 批量获取游戏名称
    /// </summary>
    public async Task<Dictionary<string, string>> GetGameNamesBatchAsync(IEnumerable<string> appIds)
    {
        var missing = appIds
            .Where(id => !_nameCache.TryGet(id, out _))
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
            if (_nameCache.TryGet(id, out var name))
                result[id] = name;
        }
        return result;
    }

    /// <summary>
    /// 获取单个游戏名称（带缓存）
    /// </summary>
    public async Task<string?> GetGameNameAsync(string appId)
    {
        if (_nameCache.TryGet(appId, out var cachedName))
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
}
