using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 清单下载服务 - 从 GitHub / ManifestHub / Sudama 下载 manifest、depot key 与访问令牌，
/// 统一生成完整的 Lua 解锁配置
/// </summary>
public class ManifestService
{
    private readonly HttpClient _http;
    private readonly SteamService _steamService;
    private readonly ConfigService _configService;

    private const string GithubRepo = "SteamAutoCracks/ManifestHub";
    private const string GithubApiBase = "https://api.github.com/repos/" + GithubRepo;
    private const string GithubRawBase = "https://raw.githubusercontent.com/" + GithubRepo;
    private const string SudamaApiUrl = "https://api.993499094.xyz/depotkeys.json";
    private const string SudamaTokensUrl = "https://api.993499094.xyz/appaccesstokens.json";

    private static readonly Regex RepairAddAppIdRegex = new(
        @"addappid\s*\(\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RepairSetManifestIdRegex = new(
        @"^\s*setManifestid\s*\(\s*(\d+)\s*,\s*""(\d+)""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public ManifestService(HttpClient http, SteamService steamService, ConfigService configService)
    {
        _http = http;
        _steamService = steamService;
        _configService = configService;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[ManifestService] {message}");
    }

    /// <summary>
    /// 按 Id 获取源配置
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
    public async Task<(bool success, string message)> DownloadFromGithubAsync(
        string appId,
        bool fixedVersion,
        bool addAllDlc,
        bool patchDepotKey,
        IProgress<string>? progress = null)
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
                return (false, "未在 GitHub 仓库中找到该游戏的清单 (404)");
            if (!branchResponse.IsSuccessStatusCode)
                return (false, $"GitHub API 错误: {(int)branchResponse.StatusCode}");

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
                return (false, "无法获取文件树");

            var treeData = await treeResponse.Content.ReadAsStringJsonAsync();
            var files = treeData.GetProperty("tree").EnumerateArray()
                .Where(f => f.GetProperty("type").GetString() == "blob")
                .ToList();

            if (files.Count == 0)
                return (false, "仓库中没有文件");

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
                return (false, "未能下载任何 manifest 文件");

            var manifestFiles = Directory.GetFiles(extractPath, "*.manifest", SearchOption.AllDirectories).ToList();
            var manifestCount = CopyManifestsToDepotCache(manifestFiles);

            // 4. 从文件名解析 depot 信息
            var depots = ParseDepotsFromManifestFiles(manifestFiles);
            Log($"解析到 {depots.Count} 个 depot");

            // 5. 生成完整 Lua（自动补 depot key / access token / DLC）
            var lua = await BuildLuaAsync(appId, "GitHub", depots, fixedVersion, patchDepotKey, addAllDlc);
            var luaOk = await WriteLuaAsync(appId, lua);

            Log("入库完成!");
            return (true, $"成功入库 AppID {appId}，复制了 {manifestCount} 个清单，Lua {(luaOk ? "已生成" : "生成失败")}");
        }
        catch (Exception ex)
        {
            return (false, $"GitHub 入库失败: {ex.Message}");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    /// <summary>
    /// 从 ManifestHub API 下载清单
    /// </summary>
    public async Task<(bool success, string message)> DownloadFromManifestHubAsync(
        string appId,
        bool fixedVersion,
        bool addAllDlc = false,
        IProgress<string>? progress = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ostgui_mhub_" + appId);

        try
        {
            var apiKey = GetSourceApiKey("mhub", _configService.Config.ManifestHubApiKey);
            if (string.IsNullOrEmpty(apiKey))
                return (false, "未配置 ManifestHub API Key");

            // 1. 从 Steam 官方 API 获取 depot + manifest gid（不依赖 GitHub）
            Log("正在从 Steam API 获取 depot/manifest 信息...");
            var gameDetails = await GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return (false, "无法获取游戏 Depot 信息");

            var manifestFiles = new List<(string depotId, string manifestGid)>();
            foreach (var depot in gameDetails.Depots.Values)
            {
                if (depot.Manifests.Count > 0)
                    manifestFiles.Add((depot.DepotId, depot.Manifests[0]));
            }

            if (manifestFiles.Count == 0)
                return (false, "Steam API 未返回任何 manifest 信息，无法下载清单");

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
                return (false, "未能下载任何清单文件");

            var manifestCount = CopyManifestsToDepotCache(
                downloaded.Select(d => Path.Combine(tempDir, $"{d.depotId}_{d.manifestGid}.manifest")).ToList());

            // 3. 生成完整 Lua（自动补 depot key / access token）
            var depots = downloaded
                .Select(d => (depotId: d.depotId, manifestGid: d.manifestGid, manifestSize: d.size))
                .ToList();
            var lua = await BuildLuaAsync(appId, "ManifestHub", depots, fixedVersion, true, addAllDlc);
            var luaOk = await WriteLuaAsync(appId, lua);

            Log("入库完成!");
            return (true, $"成功入库 AppID {appId}，下载了 {manifestCount} 个清单，Lua {(luaOk ? "已生成" : "生成失败")}");
        }
        catch (Exception ex)
        {
            return (false, $"ManifestHub 入库失败: {ex.Message}");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    /// <summary>
    /// 从 Sudama 获取 depot key / access token，并补全 manifest（GitHub 分支 zip + 镜像）
    /// </summary>
    public async Task<(bool success, string message)> DownloadFromSudamaAsync(
        string appId,
        bool fixedVersion,
        bool addAllDlc = false,
        IProgress<string>? progress = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ostgui_sudama_" + appId);

        try
        {
            // 1. 获取 depot 信息（含 manifest gid）
            Log("正在获取 Depot 信息...");
            var gameDetails = await GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return (false, "无法获取游戏 Depot 信息");

            var depotList = gameDetails.Depots.Values.ToList();
            Log($"找到 {depotList.Count} 个 Depot");

            // 2. 下载 manifest（GitHub 分支 zip，直连失败自动走镜像）
            var extractPath = Path.Combine(tempDir, "extract");
            var manifests = await DownloadManifestsFromGithubZipAsync(appId, extractPath);
            var manifestCount = CopyManifestsToDepotCache(manifests);

            // 3. 组装 depot 列表（优先用 Steam API 的 manifest gid）
            var depots = depotList
                .Select(d => (depotId: d.DepotId, manifestGid: d.Manifests.Count > 0 ? d.Manifests[0] : "", manifestSize: 0L))
                .ToList();

            if (depots.Count == 0 && manifestCount > 0)
            {
                // zip 下载到了 manifest 但 Steam API 没有 gid，从文件名解析
                depots = ParseDepotsFromManifestFiles(manifests);
            }

            // 4. 生成完整 Lua（自动补 depot key / access token）
            var lua = await BuildLuaAsync(appId, "Sudama", depots, fixedVersion, true, addAllDlc);
            var luaOk = await WriteLuaAsync(appId, lua);

            Log("入库完成!");
            var manifestInfo = manifestCount > 0 ? $"，复制了 {manifestCount} 个清单" : "（未下载到清单文件）";
            return (true, $"成功入库 AppID {appId} (Sudama 模式){manifestInfo}，Lua {(luaOk ? "已生成" : "生成失败")}");
        }
        catch (Exception ex)
        {
            return (false, $"Sudama 入库失败: {ex.Message}");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    /// <summary>
    /// 从 Sudama API 获取全量 depot 密钥（24h 缓存）
    /// </summary>
    private async Task<Dictionary<string, string>> GetSudamaKeysAsync()
    {
        return await GetCachedJsonAsync("sudama_cache.json", SudamaApiUrl, "Sudama 密钥");
    }

    /// <summary>
    /// 从 Sudama API 获取全量 App 访问令牌（24h 缓存）
    /// </summary>
    private async Task<Dictionary<string, string>> GetAccessTokensAsync()
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
    private async Task<string> BuildLuaAsync(
        string appId,
        string sourceName,
        List<(string depotId, string manifestGid, long manifestSize)> depots,
        bool fixedVersion,
        bool patchDepotKey,
        bool addAllDlc = false)
    {
        // 密钥与令牌获取失败不阻断，尽力而为
        var keys = patchDepotKey ? await GetSudamaKeysAsync() : new Dictionary<string, string>();
        var tokens = await GetAccessTokensAsync();

        var lines = new List<string>
        {
            $"-- OpenSteamTool 入库配置 - AppID {appId}",
            $"-- 来源: {sourceName}",
            ""
        };

        lines.Add($"addappid({appId})");
        lines.Add("");

        foreach (var (depotId, _, _) in depots)
        {
            // OpenSteamTool 只接受恰好 64 字符的 depot key
            var hasKey = keys.TryGetValue(depotId, out var key) && key.Length == 64;
            lines.Add(hasKey ? $"addappid({depotId}, 1, \"{key}\")" : $"addappid({depotId})");
        }

        // 添加所有 DLC（可选）：获取 DLC 列表，跳过已在 depots 中的，逐个 addappid
        if (addAllDlc)
        {
            var existingIds = new HashSet<string> { appId };
            foreach (var (depotId, _, _) in depots)
                existingIds.Add(depotId);

            var dlcIds = await GetDlcIdsAsync(appId);
            var newDlcs = dlcIds.Where(d => !existingIds.Contains(d)).ToList();
            if (newDlcs.Count > 0)
            {
                lines.Add("");
                lines.Add("-- 所有 DLC");
                foreach (var dlcId in newDlcs)
                    lines.Add($"addappid({dlcId})");
                Log($"已添加 {newDlcs.Count} 个 DLC");
            }
        }

        if (fixedVersion)
        {
            var fixedLines = depots
                .Where(d => !string.IsNullOrEmpty(d.manifestGid))
                .Select(d => d.manifestSize > 0
                    ? $"setManifestid({d.depotId}, \"{d.manifestGid}\", {d.manifestSize})"
                    : $"setManifestid({d.depotId}, \"{d.manifestGid}\")")
                .ToList();

            if (fixedLines.Count > 0)
            {
                lines.Add("");
                lines.Add("-- 固定版本配置");
                lines.AddRange(fixedLines);
            }
        }

        if (tokens.TryGetValue(appId, out var token) && !string.IsNullOrEmpty(token))
        {
            lines.Add("");
            lines.Add($"addtoken({appId}, \"{token}\")");
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// 获取游戏的 DLC AppID 列表（SteamCMD API 优先，Steam 官方 API 兜底）
    /// </summary>
    private async Task<List<string>> GetDlcIdsAsync(string appId)
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
            var response = await _http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=schinese");
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
    private async Task<bool> WriteLuaAsync(string appId, string content)
    {
        try
        {
            var luaDir = _steamService.GetLuaConfigDir();
            if (string.IsNullOrEmpty(luaDir))
            {
                Log("警告: 未找到 Lua 配置目录");
                return false;
            }

            Directory.CreateDirectory(luaDir);
            var luaFilePath = Path.Combine(luaDir, $"{appId}.lua");
            await WriteFileAtomicallyAsync(luaFilePath, content);
            Log($"已生成 Lua 文件: {luaFilePath}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"写入 Lua 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 原子写入文件（先写临时文件再覆盖），避免文件监视器读到半截内容；
    /// 统一使用无 BOM UTF-8，兼容 OpenSteamTool 的 Lua 解析
    /// </summary>
    private static async Task WriteFileAtomicallyAsync(string path, string content)
    {
        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, content, new System.Text.UTF8Encoding(false));
        File.Move(tmpPath, path, true);
    }

    /// <summary>
    /// 复制 manifest 文件到 Steam 的 depotcache 目录（config/depotcache 与 depotcache 各一份）
    /// </summary>
    private int CopyManifestsToDepotCache(List<string> manifestFiles)
    {
        var depotcachePaths = new[]
        {
            _steamService.GetConfigDepotCacheDir(),
            _steamService.GetDepotCacheDir()
        };
        foreach (var p in depotcachePaths)
        {
            if (!string.IsNullOrEmpty(p))
                Directory.CreateDirectory(p);
        }

        var count = 0;
        foreach (var manifestFile in manifestFiles)
        {
            var fileName = Path.GetFileName(manifestFile);
            foreach (var depotcache in depotcachePaths)
            {
                if (!string.IsNullOrEmpty(depotcache))
                    File.Copy(manifestFile, Path.Combine(depotcache, fileName), true);
            }
            count++;
        }
        return count;
    }

    /// <summary>
    /// 从 manifest 文件名解析 depot 信息（格式: {depotId}_{manifestGid}.manifest）
    /// </summary>
    private static List<(string depotId, string manifestGid, long manifestSize)> ParseDepotsFromManifestFiles(List<string> manifestFiles)
    {
        var depots = new List<(string, string, long)>();
        foreach (var manifestFile in manifestFiles)
        {
            var fileName = Path.GetFileName(manifestFile);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var parts = stem.Split('_');
            if (parts.Length >= 2 && parts[0].All(char.IsDigit) && parts[1].All(char.IsDigit))
                depots.Add((parts[0], parts[1], new FileInfo(manifestFile).Length));
        }
        return depots;
    }

    /// <summary>
    /// 从 GitHub 分支 zip 下载 manifest（直连失败自动走镜像），返回 manifest 文件路径列表
    /// </summary>
    private async Task<List<string>> DownloadManifestsFromGithubZipAsync(string appId, string extractPath)
    {
        var urls = new List<string>
        {
            $"https://codeload.github.com/{GithubRepo}/zip/refs/heads/{appId}",
            $"https://gh-proxy.org/https://codeload.github.com/{GithubRepo}/zip/refs/heads/{appId}",
            $"https://cdn.gh-proxy.org/https://codeload.github.com/{GithubRepo}/zip/refs/heads/{appId}",
            $"https://edgeone.gh-proxy.org/https://codeload.github.com/{GithubRepo}/zip/refs/heads/{appId}",
        };

        var zipPath = Path.Combine(Path.GetTempPath(), $"ostgui_zip_{appId}.zip");
        try
        {
            // zip 可能较大，用独立 HttpClient 避免受全局 30 秒超时限制
            using var dlClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

            foreach (var url in urls)
            {
                try
                {
                    Log($"尝试下载分支 zip: {url}");
                    var response = await dlClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(zipPath, bytes);

                    if (Directory.Exists(extractPath))
                        Directory.Delete(extractPath, true);
                    Directory.CreateDirectory(extractPath);
                    ZipFile.ExtractToDirectory(zipPath, extractPath);

                    var manifests = Directory.GetFiles(extractPath, "*.manifest", SearchOption.AllDirectories).ToList();
                    Log($"分支 zip 解压出 {manifests.Count} 个清单");
                    return manifests;
                }
                catch (Exception ex)
                {
                    Log($"zip 下载/解压失败: {ex.Message}");
                }
            }
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }

        return new();
    }

    /// <summary>
    /// 删除临时目录（失败忽略，不影响入库结果）
    /// </summary>
    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }
    }

    /// <summary>
    /// 获取游戏详情（含 depot 和 manifest gid）
    /// 优先 SteamCMD API（信息更全），回退 Steam 官方 Store API
    /// </summary>
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
            Log($"请求 Steam API: https://store.steampowered.com/api/appdetails?appids={appId}");
            var response = await _http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}");
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
    public List<ManifestSource> GetAvailableSources()
    {
        var sources = ManifestSource.GetPresetSources();
        var config = _configService.Config;

        foreach (var source in sources)
        {
            if (config.ManifestSourceEnabled.TryGetValue(source.Id, out var enabled))
                source.IsEnabled = enabled;
        }

        return sources;
    }

    /// <summary>
    /// 补全游戏清单（重新下载 manifest）
    /// </summary>
    public async Task<(bool success, string message)> RepairManifestAsync(string appId)
    {
        var apiKey = GetSourceApiKey("mhub", _configService.Config.ManifestHubApiKey);
        if (string.IsNullOrEmpty(apiKey))
            return (false, "未配置 ManifestHub API Key，无法自动修复清单");

        try
        {
            return await DownloadMissingManifestsAsync(appId);
        }
        catch (Exception ex)
        {
            return (false, $"自动修复异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 修复 Lua 错误：重新生成完整 Lua 配置（含 depot key / token / DLC / 固定版本），不下载清单文件
    /// </summary>
    public async Task<(bool success, string message)> RepairLuaAsync(string appId, bool fixedVersion)
    {
        try
        {
            Log($"自动修复 Lua: AppID {appId}");

            var gameDetails = await GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return (false, "无法获取游戏 Depot 信息，无法重新生成 Lua");

            var depots = gameDetails.Depots.Values
                .Select(d => (d.DepotId, d.Manifests.Count > 0 ? d.Manifests[0] : "", 0L))
                .ToList();

            var lua = await BuildLuaAsync(appId, "自动修复", depots, fixedVersion, true, true);
            var luaOk = await WriteLuaAsync(appId, lua);

            return luaOk
                ? (true, $"已重新生成 Lua 配置 (AppID {appId})")
                : (false, "Lua 文件写入失败");
        }
        catch (Exception ex)
        {
            return (false, $"修复 Lua 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 补齐版本配置：检测固定版本方面的错误并修复
    /// （Lua 损坏/无 setManifestid 配置 → 重建固定配置；清单缺失 → 下载补齐）
    /// </summary>
    public async Task<(bool success, string message)> RepairVersionConfigAsync(string appId)
    {
        var apiKey = GetSourceApiKey("mhub", _configService.Config.ManifestHubApiKey);
        if (string.IsNullOrEmpty(apiKey))
            return (false, "未配置 ManifestHub API Key，无法补齐版本配置");

        try
        {
            var notes = new List<string>();
            var luaDir = _steamService.GetLuaConfigDir();
            var luaPath = Path.Combine(luaDir ?? "", $"{appId}.lua");

            // 记录修复前的实际版本模式：修复后不自动切换（原固定版本保持固定，原自动更新保持自动）
            var wasFixed = false;
            if (!string.IsNullOrEmpty(luaDir) && File.Exists(luaPath))
            {
                var before = await File.ReadAllTextAsync(luaPath);
                wasFixed = RepairSetManifestIdRegex.IsMatch(before);
            }

            // 1. Lua 损坏（缺失/无 addappid/残缺）→ 重建为固定版本配置
            if (await EnsureLuaValidAsync(appId, true))
                notes.Add("已修复 Lua 配置");

            // 2. Lua 有效但缺少 setManifestid 配置（含注释形式）→ 重建补充
            var hasConfig = false;
            if (!string.IsNullOrEmpty(luaDir))
            {
                var currentPath = Path.Combine(luaDir, $"{appId}.lua");
                if (File.Exists(currentPath))
                {
                    var content = await File.ReadAllTextAsync(currentPath);
                    hasConfig = content.Contains("setManifestid", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (!hasConfig)
            {
                if (await RebuildLuaAsync(appId, true))
                    notes.Add("已补充固定版本配置");
                else
                    return (false, "无法生成固定版本配置（未获取到 Depot 信息）");
            }

            // 3. 原非固定版本 → 将 setManifestid 转为注释（配置就绪但保持自动更新，不自动切换）
            if (!wasFixed && !string.IsNullOrEmpty(luaDir))
            {
                var currentPath = Path.Combine(luaDir, $"{appId}.lua");
                if (File.Exists(currentPath))
                {
                    var content = await File.ReadAllTextAsync(currentPath);
                    if (RepairSetManifestIdRegex.IsMatch(content))
                    {
                        var commented = Regex.Replace(content, @"^(setManifestid\s*\()", "--$1", RegexOptions.Multiline);
                        if (commented != content)
                        {
                            await WriteLuaAsync(appId, commented);
                            notes.Add("已保持自动更新模式（版本配置就绪）");
                        }
                    }
                }
            }

            // 4. 补齐缺失清单
            var (ok, message) = await DownloadMissingManifestsAsync(appId);
            if (notes.Count > 0)
                message = string.Join("；", notes) + "；" + message;

            return (ok, message);
        }
        catch (Exception ex)
        {
            return (false, $"补齐版本配置异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 收集该游戏 Lua 中所有条目的 depot + manifest gid，下载 depotcache 缺失的清单
    /// </summary>
    private async Task<(bool ok, string message)> DownloadMissingManifestsAsync(string appId)
    {
        // 1. 收集该游戏 Lua 中出现的所有 AppID（主游戏 + depot/DLC 条目）
        var luaIds = await GetAppIdsFromLuaAsync(appId);
        Log($"自动修复: 在 Lua 中发现 {luaIds.Count} 个条目");

        // 2. 为每个条目获取 depot + manifest gid（SteamCMD 优先）
        var manifestPairs = new Dictionary<string, string>();
        var lockObj = new object();
        using var sem = new SemaphoreSlim(4);
        var tasks = luaIds.Select(async id =>
        {
            await sem.WaitAsync();
            try
            {
                var game = await GetGameDetailsFromSteamAsync(id);
                if (game == null) return;
                foreach (var depot in game.Depots.Values)
                {
                    if (depot.Manifests.Count > 0)
                    {
                        lock (lockObj)
                            manifestPairs.TryAdd(depot.DepotId, depot.Manifests[0]);
                    }
                }
            }
            finally
            {
                sem.Release();
            }
        });
        await Task.WhenAll(tasks);

        if (manifestPairs.Count == 0)
            return (false, "未能获取任何 depot/manifest 信息");

        // 3. 找出缺失的清单文件
        var missing = manifestPairs
            .Where(p => !ManifestExists(p.Key, p.Value))
            .ToList();
        if (missing.Count == 0)
            return (true, "清单已完整，无需修复");

        Log($"发现 {missing.Count} 个缺失清单，开始下载...");

        // 4. 逐个下载缺失清单
        var errors = new List<string>();
        var okCount = 0;
        foreach (var (depotId, gid) in missing)
        {
            var (ok, err) = await DownloadSingleManifestAsync(depotId, gid);
            if (ok) okCount++;
            else errors.Add($"Depot {depotId}: {err}");
        }

        if (errors.Count == 0)
            return (true, $"已补齐 {okCount} 个缺失清单");
        return (false, $"修复未完全成功: 成功 {okCount} 个，失败 {errors.Count} 个（{string.Join("; ", errors)}）");
    }

    /// <summary>
    /// 检查 Lua 配置有效性：文件缺失 / 无 addappid / 内容残缺时重建（按原固定意图补 setManifestid 配置）
    /// </summary>
    private async Task<bool> EnsureLuaValidAsync(string appId, bool preferFixed)
    {
        var luaDir = _steamService.GetLuaConfigDir();
        if (string.IsNullOrEmpty(luaDir)) return false;

        var luaPath = Path.Combine(luaDir, $"{appId}.lua");
        if (!File.Exists(luaPath))
            return await RebuildLuaAsync(appId, preferFixed);

        var content = await File.ReadAllTextAsync(luaPath);
        if (!RepairAddAppIdRegex.IsMatch(content) || !IsLuaContentBalanced(content))
        {
            var hadManifestConfig = content.Contains("setManifestid", StringComparison.OrdinalIgnoreCase);
            return await RebuildLuaAsync(appId, preferFixed || hadManifestConfig);
        }

        return false;
    }

    /// <summary>
    /// 重建 Lua 配置（含 depot key / token / DLC / 固定版本配置）
    /// </summary>
    private async Task<bool> RebuildLuaAsync(string appId, bool fixedVersion)
    {
        try
        {
            Log($"重建 Lua 配置: AppID {appId}");
            var gameDetails = await GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return false;

            var depots = gameDetails.Depots.Values
                .Select(d => (d.DepotId, d.Manifests.Count > 0 ? d.Manifests[0] : "", 0L))
                .ToList();

            var lua = await BuildLuaAsync(appId, "自动修复", depots, fixedVersion, true, true);
            var ok = await WriteLuaAsync(appId, lua);
            if (ok) Log("已重建 Lua 配置");
            return ok;
        }
        catch (Exception ex)
        {
            Log($"重建 Lua 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查 Lua 内容括号/引号是否配对（发现截断残留）
    /// </summary>
    private static bool IsLuaContentBalanced(string content)
    {
        var parenBalance = 0;
        var quoteCount = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;
            var commentIdx = line.IndexOf("--", StringComparison.Ordinal);
            if (commentIdx >= 0)
                line = line.Substring(0, commentIdx);

            parenBalance += line.Count(c => c == '(');
            parenBalance -= line.Count(c => c == ')');
            quoteCount += line.Count(c => c == '"');
        }
        return parenBalance == 0 && quoteCount % 2 == 0;
    }

    /// <summary>
    /// 读取该游戏 Lua 中出现的所有 addappid（主游戏 + depot/DLC 条目），文件不存在时仅返回主 AppID
    /// </summary>
    private async Task<List<string>> GetAppIdsFromLuaAsync(string appId)
    {
        var luaDir = _steamService.GetLuaConfigDir();
        var ids = new List<string>();

        if (!string.IsNullOrEmpty(luaDir))
        {
            var luaPath = Path.Combine(luaDir, $"{appId}.lua");
            if (File.Exists(luaPath))
            {
                var content = await File.ReadAllTextAsync(luaPath);
                foreach (Match m in RepairAddAppIdRegex.Matches(content))
                {
                    var id = m.Groups[1].Value;
                    if (!ids.Contains(id))
                        ids.Add(id);
                }
            }
        }

        if (ids.Count == 0)
            ids.Add(appId);
        return ids;
    }

    /// <summary>
    /// 检查 depotcache 目录中是否存在 {depotId}_{gid}.manifest
    /// </summary>
    private bool ManifestExists(string depotId, string gid)
    {
        var name = $"{depotId}_{gid}.manifest";
        var dirs = new[]
        {
            _steamService.GetConfigDepotCacheDir(),
            _steamService.GetDepotCacheDir()
        };
        foreach (var dir in dirs)
        {
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 通过 ManifestHub 下载单个缺失清单并写入 depotcache（两个目录各一份）
    /// </summary>
    private async Task<(bool ok, string err)> DownloadSingleManifestAsync(string depotId, string gid)
    {
        var apiKey = GetSourceApiKey("mhub", _configService.Config.ManifestHubApiKey);
        if (string.IsNullOrEmpty(apiKey))
            return (false, "未配置 ManifestHub API Key");

        var mhubSource = GetSource("mhub");
        var url = !string.IsNullOrEmpty(mhubSource?.BaseUrl)
            ? mhubSource!.BuildUrl(null, depotId, gid)
            : $"https://api.manifesthub2.filegear-sg.me/manifest?apikey={apiKey}&depotid={depotId}&manifestid={gid}";

        var timeoutSeconds = Math.Max(60, _configService.Config.DownloadTimeout);
        using var dlClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

        try
        {
            Log($"下载缺失清单 Depot {depotId} ...");
            var response = await dlClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode}");

            var content = await response.Content.ReadAsByteArrayAsync();
            var fileName = $"{depotId}_{gid}.manifest";
            var depotcachePaths = new[]
            {
                _steamService.GetConfigDepotCacheDir(),
                _steamService.GetDepotCacheDir()
            };
            foreach (var p in depotcachePaths)
            {
                if (!string.IsNullOrEmpty(p))
                {
                    Directory.CreateDirectory(p);
                    await File.WriteAllBytesAsync(Path.Combine(p, fileName), content);
                }
            }
            Log($"已补齐 {fileName}");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

public class SudamaCache
{
    public DateTime Timestamp { get; set; }
    public Dictionary<string, string> Data { get; set; } = new();
}

/// <summary>
/// JsonElement 扩展方法
/// </summary>
public static class JsonElementExtensions
{
    public static async Task<JsonElement> ReadAsStringJsonAsync(this HttpContent content)
    {
        var json = await content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement;
    }
}
