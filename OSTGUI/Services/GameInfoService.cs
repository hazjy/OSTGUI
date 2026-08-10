using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 游戏信息服务 - 游戏详情、DLC 信息（依赖搜索服务提供批量名称）
/// </summary>
public class GameInfoService
{
    private readonly HttpClient _http;
    private readonly GameSearchService _searchService;

    private const string SteamStoreApi = "https://store.steampowered.com/api/appdetails";

    public GameInfoService(HttpClient http, GameSearchService searchService)
    {
        _http = http;
        _searchService = searchService;
    }
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
            var names = await _searchService.GetGameNamesBatchAsync(game.Dlc.Select(d => d.ToString()));

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
}
