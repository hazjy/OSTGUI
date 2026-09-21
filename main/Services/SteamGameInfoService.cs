using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;
/// <summary>
/// 统一的游戏信息查询服务 - depot / manifest gid / DLC 列表与名称
/// 数据源：SteamCMD API 优先，官方 Store API 回退
/// </summary>
public class SteamGameInfoService
{
    private readonly HttpClient _http;
    private readonly GameSearchService _searchService;

    public SteamGameInfoService(HttpClient http, GameSearchService searchService)
    {
        _http = http;
        _searchService = searchService;
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
    /// 取官方封面 URL（appdetails 的 header_image → capsule_image → capsule_imagev5）。
    /// 只对静态 CDN 链失败的 appid 调用：2024+ 新上架游戏在旧布局（steam/apps/&lt;id&gt;/header.jpg）
    /// 下是 404，静态猜不出来，只能问官方（无需 API key）。8s 超时，任何失败返回 null。
    /// </summary>
    public async Task<string?> GetHeaderImageUrlAsync(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var resp = await _http.GetAsync(
                $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic&cc=us&l=en", cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"封面 API 响应: {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appId, out var appData)) return null;
            if (!appData.TryGetProperty("success", out var success) || !success.GetBoolean()) return null;
            if (!appData.TryGetProperty("data", out var data)) return null;

            foreach (var field in new[] { "header_image", "capsule_image", "capsule_imagev5" })
            {
                if (data.TryGetProperty(field, out var elem))
                {
                    var url = elem.GetString();
                    if (!string.IsNullOrWhiteSpace(url)) return url;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"封面 API 失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 从 SteamCMD API 获取游戏详情（含完整 depots + manifest gid）
    /// 格式: {"data": {"<appid>": {"name": ..., "depots": {"<depotid>": {"manifests": {"public": {"gid": ..., "download": ...}}, "dlcappid": ...}}}}}
    /// </summary>

    private async Task<GameInfo?> GetGameDetailsFromSteamCmdAsync(string appId)
    {
        // [本地补丁 2026-09-09] SteamCMD API 在本机 SSL/连接层偶发失败（schannel 受加速器干扰），
        // 实测日志"重试即成功"——引入最多 3 次重试，且每次使用全新 HttpClient
        // （规避连接池复用失败 TLS 会话），稳定 MHub 前置的 manifest gid 获取。
        const int maxAttempts = 3;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var url = $"https://api.steamcmd.net/v1/info/{appId}";
                Log(attempt == 0
                    ? $"请求 SteamCMD API: {url}"
                    : $"请求 SteamCMD API 重试({attempt + 1}/{maxAttempts}): {url}");
                if (attempt > 0)
                    await Task.Delay(500 * attempt);

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Log($"SteamCMD API 非成功响应 ({(int)response.StatusCode}), 重试({attempt + 1}/{maxAttempts})");
                    continue;
                }

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

                    if (depotData.TryGetProperty("name", out var depotName))
                        depot.Name = depotName.GetString() ?? "";

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

                    if (depotData.TryGetProperty("encrypted", out var encryptedObj) &&
                        encryptedObj.TryGetProperty("key", out var keyElem))
                        depot.DecryptionKey = keyElem.GetString() ?? "";

                    game.Depots[prop.Name] = depot;
                    depotCount++;
                }

                Log($"SteamCMD API 解析到 {depotCount} 个 Depot");
                return depotCount > 0 ? game : null;
            }
            catch (Exception ex)
            {
                Log($"SteamCMD API 异常 ({attempt + 1}/{maxAttempts}): {ex.Message}");
                if (attempt == maxAttempts - 1)
                    return null;
            }
        }
        return null;
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

            if (data.TryGetProperty("name", out var nameElem))
                game.Name = nameElem.GetString() ?? "";

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
                        if (publicManifest.TryGetProperty("gid", out var gidElem))
                        {
                            var gid = gidElem.GetString();
                            if (gid != null)
                            {
                                depot.Manifests.Add(gid);
                                Log($"Depot {depotId}: Manifest GID = {gid}");
                            }
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

        // 只走 SteamCMD API：extended/common 下的 listofdlc（逗号分隔字符串）。
        // 官方 store 兜底已删——大陆网络不可达，只会让每次打开都空转 30s 超时。
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

        return ids;
    }

    /// <summary>
    /// 获取 AppID 的在线游戏名：steamcmd 名优先（steamcmd 不提供 DLC 名字，实测对 DLC 返回空壳），
    /// 取不到时兜底官方 appdetails（可达时带名，8s 截断防拖慢）。
    /// 轻量路径：只取名，不要求 depots 非空（区别于 GetGameDetailsFromSteamAsync 的入库级查询）。
    /// </summary>
    public async Task<string?> GetGameNameOnlineAsync(string appId)
    {
        try
        {
            var response = await _http.GetAsync($"https://api.steamcmd.net/v1/info/{appId}");
            if (response.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty(appId, out var appData) &&
                    appData.TryGetProperty("name", out var nameElem))
                    return nameElem.GetString();
            }
        }
        catch { }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var resp = await _http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&cc=us", cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty(appId, out var apd) &&
                apd.TryGetProperty("data", out var d) &&
                d.TryGetProperty("name", out var ne))
                return ne.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 获取 DLC 列表（含名称；名称每次实时向 API 获取，不参与名称缓存）
    /// </summary>
    public async Task<List<DlcInfo>> GetDlcInfoAsync(string appId)
    {
        var result = new List<DlcInfo>();
        try
        {
            var ids = await GetDlcIdsAsync(appId);
            if (ids.Count == 0) return result;

            // 实时批量取 DLC 名（限并发 6），不读写名称缓存、不走官方 store 兜底
            using var gate = new SemaphoreSlim(6);
            var nameTasks = ids.Select(async id =>
            {
                await gate.WaitAsync();
                try
                {
                    return (id, name: await GetGameNameOnlineAsync(id) ?? "");
                }
                finally
                {
                    gate.Release();
                }
            });
            var got = await Task.WhenAll(nameTasks);
            var names = got
                .Where(t => !string.IsNullOrEmpty(t.name))
                .ToDictionary(t => t.id, t => t.name);

            foreach (var id in ids)
            {
                result.Add(new DlcInfo
                {
                    AppId = id,
                    Name = names.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : $"DLC {id}"
                });
            }
        }
        catch (Exception ex)
        {
            Log($"GetDlcInfoAsync 异常: {ex.Message}");
        }
        return result;
    }
}
