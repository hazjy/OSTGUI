using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;
/// <summary>
/// 清单下载服务 - 从 GitHub / ManifestHub / Sudama 下载 manifest 并生成 Lua 配置
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

    private const string GithubRepo = "SteamAutoCracks/ManifestHub";
    private const string GithubApiBase = "https://api.github.com/repos/" + GithubRepo;
    private const string GithubRawBase = "https://raw.githubusercontent.com/" + GithubRepo;

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
    public async Task<(bool success, string message, List<string> missingKeys)> DownloadFromGithubAsync(
        string appId,
        bool fixedVersion,
        bool addAllDlc,
        bool patchDepotKey,
        IProgress<string>? progress)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ostgui_" + appId);
        var extractPath = Path.Combine(tempDir, "extract");

        try
        {
            var config = _configService.Config;
            var githubToken = GetSourceApiKey("github_auiowu", config.GithubToken);
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "OSTGUI",
                ["Accept"] = "application/vnd.github.v3+json"
            };
            if (!string.IsNullOrEmpty(githubToken))
                headers["Authorization"] = $"Bearer {githubToken}";

            // 1. 检查分支是否存在
            Log("检查分支是否存在: " + $"{GithubApiBase}/branches/{appId}");
            var branchRequest = new HttpRequestMessage(HttpMethod.Get, $"{GithubApiBase}/branches/{appId}");
            foreach (var h in headers)
                branchRequest.Headers.TryAddWithoutValidation(h.Key, h.Value);
            var branchResponse = await _http.SendAsync(branchRequest);
            Log($"分支检查响应: {(int)branchResponse.StatusCode}");

            if (branchResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (false, "未在 GitHub 仓库中找到该游戏的清单 (404)", new List<string>());
            if (!branchResponse.IsSuccessStatusCode)
                return (false, $"GitHub API 错误: {(int)branchResponse.StatusCode}", new List<string>());

            var branchData = await branchResponse.Content.ReadAsStringJsonAsync();
            var commitSha = branchData.GetProperty("commit").GetProperty("sha").GetString()!;
            Log($"获取到 commit SHA: {commitSha}");

            // 2. 获取文件树
            Log("正在获取文件列表...");
            var treeRequest = new HttpRequestMessage(HttpMethod.Get, $"{GithubApiBase}/git/trees/{commitSha}?recursive=1");
            foreach (var h in headers)
                treeRequest.Headers.TryAddWithoutValidation(h.Key, h.Value);
            var treeResponse = await _http.SendAsync(treeRequest);
            if (!treeResponse.IsSuccessStatusCode)
                return (false, "无法获取文件树", new List<string>());

            var treeData = await treeResponse.Content.ReadAsStringJsonAsync();
            var files = treeData.GetProperty("tree").EnumerateArray()
                .Where(f => f.GetProperty("type").GetString() == "blob")
                .ToList();

            if (files.Count == 0)
                return (false, "仓库中没有文件", new List<string>());

            Log($"找到 {files.Count} 个文件，开始下载...");

            // 3. 下载分支下的 manifest 文件
            Directory.CreateDirectory(extractPath);
            var downloaded = 0;
            foreach (var file in files)
            {
                var filePath = file.GetProperty("path").GetString()!;
                if (!filePath.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileUrl = $"{GithubRawBase}/{appId}/{filePath}";
                try
                {
                    var fileRequest = new HttpRequestMessage(HttpMethod.Get, fileUrl);
                    foreach (var h in headers)
                        fileRequest.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    var fileResponse = await _http.SendAsync(fileRequest);
                    if (fileResponse.IsSuccessStatusCode)
                    {
                        var targetPath = Path.Combine(extractPath, Path.GetFileName(filePath));
                        var content = await fileResponse.Content.ReadAsByteArrayAsync();
                        await File.WriteAllBytesAsync(targetPath, content);
                        downloaded++;
                        Log($"已下载: {Path.GetFileName(filePath)}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"下载异常: {filePath} - {ex.Message}");
                }
            }

            if (downloaded == 0)
                return (false, "未能下载任何 manifest 文件", new List<string>());

            var manifestFiles = Directory.GetFiles(extractPath, "*.manifest", SearchOption.AllDirectories).ToList();
            var manifestCount = _manifestFile.CopyToDepotCache(manifestFiles);

            // 4. 从文件名解析 depot 信息
            var depots = ManifestFileService.ParseDepotsFromFiles(manifestFiles);
            Log($"解析到 {depots.Count} 个 depot");

            // 5. 生成完整 Lua（自动补 depot key / access token / DLC）
            var (lua, missingKeys) = await _luaBuilder.BuildLuaAsync(appId, "GitHub", depots, fixedVersion, patchDepotKey, addAllDlc);
            var luaOk = await _luaBuilder.WriteLuaAsync(appId, lua);

            Log("入库完成!");
            return (true, $"成功入库 AppID {appId}，复制了 {manifestCount} 个清单，Lua {(luaOk ? "已生成" : "生成失败")}", missingKeys);
        }
        catch (Exception ex)
        {
            return (false, $"GitHub 入库失败: {ex.Message}", new List<string>());
        }
        finally
        {
            ManifestFileService.TryDeleteDir(tempDir);
        }
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
            var apiKey = GetSourceApiKey("mhub", _configService.Config.ManifestHubApiKey);
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
            var mhubSource = GetSource("mhub");
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
            var (lua, missingKeys) = await _luaBuilder.BuildLuaAsync(appId, "ManifestHub", depots, fixedVersion, true, addAllDlc);
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
    /// 从 Sudama 获取 depot key / access token，并补全 manifest（GitHub 分支 zip + 镜像）
    /// </summary>

    public async Task<(bool success, string message, List<string> missingKeys)> DownloadFromSudamaAsync(
        string appId,
        bool fixedVersion,
        bool addAllDlc,
        IProgress<string>? progress)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ostgui_sudama_" + appId);

        try
        {
            // 1. 获取 depot 信息（含 manifest gid）
            Log("正在获取 Depot 信息...");
            var gameDetails = await _gameInfoService.GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return (false, "无法获取游戏 Depot 信息", new List<string>());

            var depotList = gameDetails.Depots.Values.ToList();
            Log($"找到 {depotList.Count} 个 Depot");

            // 2. 下载 manifest（GitHub 分支 zip，直连失败自动走镜像）
            var extractPath = Path.Combine(tempDir, "extract");
            var manifests = await _manifestFile.DownloadFromGithubZipAsync(appId, extractPath);
            var manifestCount = _manifestFile.CopyToDepotCache(manifests);

            // 3. 组装 depot 列表（优先用 Steam API 的 manifest gid）
            var depots = depotList
                .Select(d => (depotId: d.DepotId, manifestGid: d.Manifests.Count > 0 ? d.Manifests[0] : "", manifestSize: 0L))
                .ToList();

            if (depots.Count == 0 && manifestCount > 0)
            {
                // zip 下载到了 manifest 但 Steam API 没有 gid，从文件名解析
                depots = ManifestFileService.ParseDepotsFromFiles(manifests);
            }

            // 4. 生成完整 Lua（自动补 depot key / access token）
            var (lua, missingKeys) = await _luaBuilder.BuildLuaAsync(appId, "Sudama", depots, fixedVersion, true, addAllDlc);
            var luaOk = await _luaBuilder.WriteLuaAsync(appId, lua);

            Log("入库完成!");
            var manifestInfo = manifestCount > 0 ? $"，复制了 {manifestCount} 个清单" : "（未下载到清单文件）";
            return (true, $"成功入库 AppID {appId} (Sudama 模式){manifestInfo}，Lua {(luaOk ? "已生成" : "生成失败")}", missingKeys);
        }
        catch (Exception ex)
        {
            return (false, $"Sudama 入库失败: {ex.Message}", new List<string>());
        }
        finally
        {
            ManifestFileService.TryDeleteDir(tempDir);
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

    /// <summary>
    /// 获取源配置的 API Key，未配置时回退旧全局字段
    /// </summary>

    private string GetSourceApiKey(string sourceId, string legacyFallback)
    {
        var source = GetSource(sourceId);
        return !string.IsNullOrEmpty(source?.ApiKey) ? source.ApiKey : legacyFallback;
    }

    /// <summary>
    /// 获取源配置的 URL 模板，未配置时回退默认 URL
    /// </summary>

    private string GetSourceBaseUrl(string sourceId, string defaultUrl)
    {
        var source = GetSource(sourceId);
        return !string.IsNullOrEmpty(source?.BaseUrl) ? source.BaseUrl : defaultUrl;
    }

    /// <summary>
    /// 从 GitHub 下载清单并生成 Lua 配置
    /// </summary>


}
