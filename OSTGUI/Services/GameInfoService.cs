using System.Text.Json;
using OSTGUI.Models;
using OSTGUI.Services;

namespace OSTGUI.Services;

/// <summary>
/// 游戏信息服务 - 仅使用 SteamCMD API 获取 depot/manifest 信息
/// </summary>
public class GameInfoService
{
    private readonly HttpClient _http;
    private readonly GameSearchService _searchService;

    private const string SteamCmdApi = "https://api.steamcmd.net/v1/info";

    public GameInfoService(HttpClient http, GameSearchService searchService)
    {
        _http = http;
        _searchService = searchService;
    }

    /// <summary>
    /// 获取游戏 depot/manifest 信息（仅 SteamCMD API）
    /// </summary>
    public async Task<GameInfo?> GetGameDetailsAsync(string appId)
    {
        try
        {
            var response = await _http.GetAsync($"{SteamCmdApi}/{appId}");
            if (!response.IsSuccessStatusCode)
            {
                LogService.AddLog($"[GameInfoService] AppID {appId} SteamCMD API 请求失败，状态码: {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            // SteamCMD API 格式: {"data": {"appid": {"depots": {...}, "common": {...}}}}
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty(appId, out var appData) ||
                !appData.TryGetProperty("depots", out var depotsObj))
            {
                LogService.AddLog($"[GameInfoService] AppID {appId} SteamCMD API 返回无效数据或无 depots");
                return null;
            }

            var game = new GameInfo { AppId = appId };

            // 读取 common 字段补充基础信息
            if (appData.TryGetProperty("common", out var common))
            {
                if (common.TryGetProperty("name", out var name))
                    game.Name = name.GetString() ?? "";
            }

            var depotCount = 0;
            foreach (var _ in depotsObj.EnumerateObject()) depotCount++;
            LogService.AddLog($"[GameInfoService] AppID {appId} SteamCMD API 找到 {depotCount} 个 depot");
            ParseDepots(appId, depotsObj, game);

            if (game.Depots.Count == 0)
            {
                LogService.AddLog($"[GameInfoService] AppID {appId} 解析后无有效 depot");
                return null;
            }

            return game;
        }
        catch (Exception ex)
        {
            LogService.AddLog($"[GameInfoService] AppID {appId} SteamCMD API 异常: {ex.Message}");
            return null;
        }
    }

    private void ParseDepots(string appId, JsonElement depotsObj, GameInfo game)
    {
        foreach (var prop in depotsObj.EnumerateObject())
        {
            var depotId = prop.Name;
            if (!depotId.All(char.IsDigit)) continue; // 跳过 branches 等非数字键

            var depotData = prop.Value;
            var depot = new DepotInfo { DepotId = depotId };

            if (depotData.TryGetProperty("name", out var depotName))
                depot.Name = depotName.GetString() ?? "";

            if (depotData.TryGetProperty("maxsize", out var maxSize))
                depot.MaxSize = maxSize.GetInt64();

            // DLC 关联
            if (depotData.TryGetProperty("dlc", out var dlcAppId))
                depot.DlcAppId = dlcAppId.GetString() ?? "";

            // Manifests - 只取 public gid
            if (depotData.TryGetProperty("manifests", out var manifestsObj))
            {
                if (manifestsObj.TryGetProperty("public", out var publicManifest))
                {
                    string? gid = null;
                    if (publicManifest.ValueKind == JsonValueKind.String)
                    {
                        gid = publicManifest.GetString();
                    }
                    else if (publicManifest.ValueKind == JsonValueKind.Object)
                    {
                        // public 可能是对象，尝试读取 gid 字段
                        if (publicManifest.TryGetProperty("gid", out var gidProp))
                            gid = gidProp.GetString();
                    }
                    
                    if (!string.IsNullOrEmpty(gid))
                        depot.Manifests.Add(gid);
                }
                else
                {
                    LogService.AddLog($"[GameInfoService] AppID {appId} Depot {depotId} 无 public manifest");
                }
            }
            else
            {
                LogService.AddLog($"[GameInfoService] AppID {appId} Depot {depotId} 无 manifests 字段");
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
        catch (Exception ex) { LogService.AddLog($"[GameInfoService] GetDlcInfoAsync 错误: {ex.Message}"); }
        return dlcList;
    }

    /// <summary>
    /// 获取单个游戏名称（带缓存）
    /// </summary>
}