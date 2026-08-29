using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;
/// <summary>
/// 清单下载服务 - 从 ManifestHub 下载 manifest，Sudama 仅作密钥源，统一生成 Lua 配置
/// </summary>
public class ManifestDownloadService
{
    private readonly HttpClient _http;
    private readonly SteamService _steamService;
    private readonly ConfigService _configService;
    private readonly SteamGameInfoService _gameInfoService;
    private readonly LuaBuilder _luaBuilder;
    private readonly SudamaKeyCache _sudamaCache;
    private readonly ManifestFileService _manifestFile;

    public ManifestDownloadService(HttpClient http, SteamService steamService, ConfigService configService,
        SteamGameInfoService gameInfoService, LuaBuilder luaBuilder, SudamaKeyCache sudamaCache,
        ManifestFileService manifestFile)
    {
        _http = http;
        _steamService = steamService;
        _configService = configService;
        _gameInfoService = gameInfoService;
        _luaBuilder = luaBuilder;
        _sudamaCache = sudamaCache;
        _manifestFile = manifestFile;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[ManifestDownload] {message}");
    }

    /// <summary>
    /// 从 ManifestHub API 下载清单
    /// </summary>

    public async Task<(bool success, string message, List<string> missingKeys)> DownloadFromManifestHubAsync(
        string appId,
        bool fixedVersion,
        bool addAllDlc,
        IProgress<string>? progress)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ostgui_mhub_" + appId);

        try
        {
            var mhubSource = GetSource("mhub");
            var apiKey = !string.IsNullOrEmpty(mhubSource?.ApiKey)
                ? mhubSource.ApiKey
                : _configService.Config.ManifestHubApiKey;
            if (string.IsNullOrEmpty(apiKey))
                return (false, "未配置 ManifestHub API Key", new List<string>());

            // 1. 从 Steam 官方 API 获取 depot + manifest gid（不依赖 GitHub）
            Log("正在从 Steam API 获取 depot/manifest 信息...");
            var gameDetails = await _gameInfoService.GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return (false, "无法获取游戏 Depot 信息", new List<string>());

            var manifestFiles = new List<(string depotId, string manifestGid)>();
            foreach (var depot in gameDetails.Depots.Values)
            {
                if (depot.Manifests.Count > 0)
                    manifestFiles.Add((depot.DepotId, depot.Manifests[0]));
            }

            if (manifestFiles.Count == 0)
                return (false, "Steam API 未返回任何 manifest 信息，无法下载清单", new List<string>());

            Log($"找到 {manifestFiles.Count} 个清单文件, 开始下载...");

            // 2. 下载每个 manifest
            Directory.CreateDirectory(tempDir);
            var mhubUrlTemplate = !string.IsNullOrEmpty(mhubSource?.BaseUrl) ? mhubSource.BaseUrl : "";
            var downloaded = new List<(string depotId, string manifestGid, long size)>();

            // manifest 文件可能较大，用独立 HttpClient 设置更长超时（默认 120 秒），
            // 避免受全局 HttpClient.Timeout(30 秒) 限制
            var timeoutSeconds = Math.Max(60, _configService.Config.DownloadTimeout);
            using var dlClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

            foreach (var (depotId, manifestGid) in manifestFiles)
            {
                var url = !string.IsNullOrEmpty(mhubUrlTemplate)
                    ? mhubSource!.BuildUrl(null, depotId, manifestGid)
                    : $"https://api.manifesthub2.filegear-sg.me/manifest?apikey={apiKey}&depotid={depotId}&manifestid={manifestGid}";
                Log($"下载 Depot {depotId} 的清单...");

                try
                {
                    var response = await dlClient.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsByteArrayAsync();
                        var fileName = $"{depotId}_{manifestGid}.manifest";
                        var filePath = Path.Combine(tempDir, fileName);
                        await File.WriteAllBytesAsync(filePath, content);
                        downloaded.Add((depotId, manifestGid, content.LongLength));
                        Log($"已下载 {fileName}");
                    }
                    else
                    {
                        Log($"下载失败 ({(int)response.StatusCode}): Depot {depotId}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"下载异常: Depot {depotId} - {ex.Message}");
                }
            }

            if (downloaded.Count == 0)
                return (false, "未能下载任何清单文件", new List<string>());

            var manifestCount = _manifestFile.CopyToDepotCache(
                downloaded.Select(d => Path.Combine(tempDir, $"{d.depotId}_{d.manifestGid}.manifest")).ToList());

            // 3. 生成完整 Lua（自动补 depot key / access token）
            var depots = downloaded
                .Select(d => (depotId: d.depotId, manifestGid: d.manifestGid, manifestSize: d.size))
                .ToList();
            var (lua, missingKeys) = await _luaBuilder.BuildLuaAsync(appId, "ManifestHub", depots, fixedVersion, addAllDlc);
            var luaOk = await _luaBuilder.WriteLuaAsync(appId, lua);

            Log("入库完成!");
            return (true, $"成功入库 AppID {appId}，下载了 {manifestCount} 个清单，Lua {(luaOk ? "已生成" : "生成失败")}", missingKeys);
        }
        catch (Exception ex)
        {
            return (false, $"ManifestHub 入库失败: {ex.Message}", new List<string>());
        }
        finally
        {
            ManifestFileService.TryDeleteDir(tempDir);
        }
    }

    /// <summary>
    /// Sudama 密钥源入库：获取 depot 信息并生成完整 Lua（补 depot key / access token）。
    /// 不下载清单文件——清单由清单源（MHub / GitHub）负责，Sudama 只作为密钥源。
    /// </summary>

    public async Task<(bool success, string message, List<string> missingKeys)> DownloadFromSudamaAsync(
        string appId,
        bool fixedVersion,
        bool addAllDlc,
        IProgress<string>? progress)
    {
        try
        {
            // 1. 获取 depot 信息（含 manifest gid）
            Log("正在获取 Depot 信息...");
            var gameDetails = await _gameInfoService.GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return (false, "无法获取游戏 Depot 信息", new List<string>());

            var depotList = gameDetails.Depots.Values.ToList();
            Log($"找到 {depotList.Count} 个 Depot");

            // 2. 组装 depot 列表（gid 来自 Steam API 当前版本）
            var depots = depotList
                .Select(d => (depotId: d.DepotId, manifestGid: d.Manifests.Count > 0 ? d.Manifests[0] : "", manifestSize: 0L))
                .ToList();

            // 3. 生成完整 Lua（自动补 Sudama depot key / access token）
            var (lua, missingKeys) = await _luaBuilder.BuildLuaAsync(appId, "Sudama", depots, fixedVersion, addAllDlc);
            var luaOk = await _luaBuilder.WriteLuaAsync(appId, lua);

            Log("入库完成!");
            return (true, $"成功入库 AppID {appId} (Sudama 密钥源模式)（未下载到清单文件，清单需由清单源获取）Lua {(luaOk ? "已生成" : "生成失败")}", missingKeys);
        }
        catch (Exception ex)
        {
            return (false, $"Sudama 入库失败: {ex.Message}", new List<string>());
        }
    }

    /// <summary>
    /// 从 Sudama API 获取全量 depot 密钥（24h 缓存）
    /// </summary>

    /// <summary>
    /// 获取游戏详情（含 depot 和 manifest gid）
    /// 优先 SteamCMD API（信息更全），回退 Steam 官方 Store API
    /// </summary>

    private ManifestSource? GetSource(string id)
    {
        var config = _configService.Config;
        return config.ManifestSources?.FirstOrDefault(s => s.Id == id);
    }

}
