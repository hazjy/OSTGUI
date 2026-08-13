using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;
/// <summary>
/// Steam 游戏信息服务 - 从 SteamCMD / Steam 官方 Store API 获取游戏 depot、manifest gid 与 DLC 列表
/// </summary>
public class SteamGameInfoService
{
    private readonly HttpClient _http;

    public SteamGameInfoService(HttpClient http)
    {
        _http = http;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[SteamGameInfo] {message}");
    }
    public async Task<GameInfo?> GetGameDetailsFromSteamAsync(string appId)
    {
        var game = await GetGameDetailsFromSteamCmdAsync(appId);
        if (game != null && game.Depots.Count > 0)
            return game;

        return await GetGameDetailsFromStoreApiAsync(appId);
    }

    /// <summary>
    /// 从 SteamCMD API 获取游戏详情（含完整 depots + manifest gid）
    /// 格式: {"data": {"<appid>": {"name": ..., "depots": {"<depotid>": {"manifests": {"public": {"gid": ..., "download": ...}}, "dlcappid": ...}}}}}
    /// </summary>

    private async Task<GameInfo?> GetGameDetailsFromSteamCmdAsync(string appId)
    {
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            Log($"请求 SteamCMD API: {url}");
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty(appId, out var appData) ||
                !appData.TryGetProperty("depots", out var depotsObj))
                return null;

            var game = new GameInfo { AppId = appId };
            if (appData.TryGetProperty("name", out var nameElem))
                game.Name = nameElem.GetString() ?? "";

            var depotCount = 0;
            foreach (var prop in depotsObj.EnumerateObject())
            {
                if (!prop.Name.All(char.IsDigit))
                    continue;

                var depotData = prop.Value;
                var depot = new DepotInfo { DepotId = prop.Name };

                if (depotData.TryGetProperty("manifests", out var manifestsObj) &&
                    manifestsObj.TryGetProperty("public", out var publicManifest))
                {
                    if (publicManifest.TryGetProperty("gid", out var gidElem))
                    {
                        var gid = gidElem.GetString();
                        if (gid != null)
                            depot.Manifests.Add(gid);
                    }

                    if (publicManifest.TryGetProperty("download", out var downloadElem))
                        depot.MaxSize = GetInt64Safe(downloadElem);
                    else if (publicManifest.TryGetProperty("size", out var sizeElem))
                        depot.MaxSize = GetInt64Safe(sizeElem);
                }

                if (depotData.TryGetProperty("dlcappid", out var dlcElem))
                    depot.DlcAppId = dlcElem.GetString() ?? "";

                game.Depots[prop.Name] = depot;
                depotCount++;
            }

            Log($"SteamCMD API 解析到 {depotCount} 个 Depot");
            return depotCount > 0 ? game : null;
        }
        catch (Exception ex)
        {
            Log($"SteamCMD API 异常: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 兼容 JSON 数字/字符串两种类型的整数读取（SteamCMD API 的 download/size 是字符串）
    /// </summary>

    private static long GetInt64Safe(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
            return number;
        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), out var parsed))
            return parsed;
        return 0;
    }

    /// <summary>
    /// 从 Steam 官方 Store API 获取游戏详情（含 depot 和 manifest）
    /// </summary>

    private async Task<GameInfo?> GetGameDetailsFromStoreApiAsync(string appId)
    {
        try
        {
            Log($"请求 Steam API: https://store.steampowered.com/api/appdetails?appids={appId}&cc=us");
            var response = await _http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&cc=us");
            Log($"Steam API 响应: {(int)response.StatusCode}");

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            Log($"响应长度: {json.Length} 字符");

            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appId, out var appData))
            {
                Log("错误: 响应中没有 AppID 属性");
                return null;
            }

            if (!appData.TryGetProperty("success", out var s) || !s.GetBoolean())
            {
                Log("错误: success 字段为 false 或不存在");
                return null;
            }

            if (!appData.TryGetProperty("data", out var data))
            {
                Log("错误: data 字段不存在");
                return null;
            }

            var game = new GameInfo { AppId = appId };

            if (data.TryGetProperty("depots", out var depotsObj))
            {
                var depotCount = 0;
                foreach (var prop in depotsObj.EnumerateObject())
                {
                    var depotId = prop.Name;
                    if (!depotId.All(char.IsDigit))
                    {
                        Log($"跳过非数字 depot 键: {depotId}");
                        continue;
                    }

                    var depotData = prop.Value;
                    var depot = new DepotInfo { DepotId = depotId };

                    if (depotData.TryGetProperty("manifests", out var manifestsObj) &&
                        manifestsObj.TryGetProperty("public", out var publicManifest))
                    {
                        var gid = publicManifest.GetProperty("gid").GetString();
                        if (gid != null)
                        {
                            depot.Manifests.Add(gid);
                            Log($"Depot {depotId}: Manifest GID = {gid}");
                        }
                    }
                    else
                    {
                        Log($"Depot {depotId}: 无 manifest");
                    }

                    if (depotData.TryGetProperty("encrypted", out var encryptedObj) &&
                        encryptedObj.TryGetProperty("key", out var keyElem))
                    {
                        depot.DecryptionKey = keyElem.GetString() ?? "";
                        Log($"Depot {depotId}: 有密钥");
                    }

                    game.Depots[depotId] = depot;
                    depotCount++;
                }
                Log($"共解析 {depotCount} 个 Depot");
            }
            else
            {
                Log("警告: 响应中没有 depots 字段");
            }

            return game;
        }
        catch (Exception ex)
        {
            Log($"获取游戏详情异常: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取可用的清单源列表
    /// </summary>

    public async Task<List<string>> GetDlcIdsAsync(string appId)
    {
        var ids = new List<string>();

        // 1. SteamCMD API：extended/common 下的 listofdlc（逗号分隔字符串）
        try
        {
            var response = await _http.GetAsync($"https://api.steamcmd.net/v1/info/{appId}");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty(appId, out var appData))
                {
                    foreach (var section in new[] { "extended", "common" })
                    {
                        if (appData.TryGetProperty(section, out var sec) &&
                            sec.TryGetProperty("listofdlc", out var listElem) &&
                            listElem.ValueKind == JsonValueKind.String)
                        {
                            ids = listElem.GetString()!
                                .Split(',')
                                .Select(s => s.Trim())
                                .Where(s => s.Length > 0 && s.All(char.IsDigit))
                                .Distinct()
                                .ToList();
                            if (ids.Count > 0)
                                return ids;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"获取 DLC 列表异常(SteamCMD): {ex.Message}");
        }

        // 2. Steam 官方 API：dlc 数组
        try
        {
            var response = await _http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=schinese&cc=us");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(appId, out var appData) &&
                    appData.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("dlc", out var dlcArr))
                {
                    ids = dlcArr.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _))
                        .Select(e => e.GetInt32().ToString())
                        .Distinct()
                        .ToList();
                }
            }
        }
        catch (Exception ex)
        {
            Log($"获取 DLC 列表异常(Steam API): {ex.Message}");
        }

        return ids;
    }

    /// <summary>
    /// 写入 Lua 配置文件
    /// </summary>


}
