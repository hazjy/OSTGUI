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
    /// 从 ManifestHub API 下载清单（GUI「搜索入库」用）。
    ///
    /// 取消语义（2026-09-22 加，同日改为"近乎立即"）：<paramref name="ct"/> 贯通**所有会等的环节**——
    /// 取 depot 信息（SteamGameInfoService 的 HTTP 与重试退避）、每个 depot 的下载及其临时写盘、
    /// manifest 逐份拷贝之间、生成 lua 里的两处网络（DLC 列表 / 补全缺失 depot）。
    /// **刻意不留检查点的只有最后一步**：`WriteLuaAsync` 的原子写（临时文件 + Move，毫秒级长度），
    /// 因此取消永远不会留下半个 .lua。取消后已进 depotcache 的清单是**完整文件、无害**：
    /// 它们按 gid 命名、只有 lua 会引用，而 lua 没写（详见 ManifestFileService.CopyToDepotCache 注释）。
    /// </summary>
    public async Task<AddGameResult> DownloadFromManifestHubAsync(
        string appId,
        bool fixedVersion,
        bool addAllDlc,
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ostgui_mhub_" + appId);

        try
        {
            var mhubSource = GetSource("mhub");
            var apiKey = !string.IsNullOrEmpty(mhubSource?.ApiKey)
                ? mhubSource.ApiKey
                : _configService.Config.ManifestHubApiKey;
            if (string.IsNullOrEmpty(apiKey))
                return new AddGameResult { Success = false, Message = "未配置 ManifestHub API Key" };

            // 1. 从 Steam 官方 API 获取 depot + manifest gid（不依赖 GitHub）
            Log("正在从 Steam API 获取 depot/manifest 信息...");
            var gameDetails = await _gameInfoService.GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return new AddGameResult { Success = false, Message = "无法获取游戏 Depot 信息" };

            var manifestFiles = new List<(string depotId, string manifestGid)>();
            foreach (var depot in gameDetails.Depots.Values)
            {
                if (depot.Manifests.Count > 0)
                    manifestFiles.Add((depot.DepotId, depot.Manifests[0]));
            }

            if (manifestFiles.Count == 0)
                return new AddGameResult { Success = false, Message = "Steam API 未返回任何 manifest 信息，无法下载清单" };

            Log($"找到 {manifestFiles.Count} 个清单文件, 开始下载...");

            // 2. 下载每个 manifest
            Directory.CreateDirectory(tempDir);
            var mhubUrlTemplate = !string.IsNullOrEmpty(mhubSource?.BaseUrl) ? mhubSource.BaseUrl : "";
            var downloaded = new List<(string depotId, string manifestGid, long size)>();
            var failedDepots = new List<string>();

            // manifest 文件可能较大，用独立 HttpClient 设置更长超时（默认 120 秒），
            // 避免受全局 HttpClient.Timeout(30 秒) 限制
            var timeoutSeconds = Math.Max(60, _configService.Config.DownloadTimeout);
            using var dlClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

            foreach (var (depotId, manifestGid) in manifestFiles)
            {
                ct.ThrowIfCancellationRequested();   // 取消点①：开始下一个 depot 之前
                var url = !string.IsNullOrEmpty(mhubUrlTemplate)
                    ? mhubSource!.BuildUrl(null, depotId, manifestGid)
                    : $"https://api.manifesthub2.filegear-sg.me/manifest?apikey={apiKey}&depotid={depotId}&manifestid={manifestGid}";
                Log($"下载 Depot {depotId} 的清单...");

                try
                {
                    var response = await dlClient.GetAsync(url, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsByteArrayAsync(ct);
                        var fileName = $"{depotId}_{manifestGid}.manifest";
                        var filePath = Path.Combine(tempDir, fileName);
                        await File.WriteAllBytesAsync(filePath, content, ct);
                        downloaded.Add((depotId, manifestGid, content.LongLength));
                        Log($"已下载 {fileName}");
                    }
                    else
                    {
                        failedDepots.Add(depotId);
                        Log($"下载失败 ({(int)response.StatusCode}): Depot {depotId}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;   // 取消不是"这个 depot 下载失败"，别被下面的 catch 吞掉记进 failedDepots
                }
                catch (Exception ex)
                {
                    failedDepots.Add(depotId);
                    Log($"下载异常: Depot {depotId} - {ex.Message}");
                }
            }

            // 仅在实际内容 depot（appinfo 中有 manifest GID）的清单未下载（HTTP 失败/异常）时才警告；
            // 壳型/共享 depot（无 GID）本就不预下载，属正常，不提示。
            if (failedDepots.Count > 0)
            {
                Log($"警告: 以下实际 depot 的清单未能下载，未预下载，将由内核运行时获取: {string.Join(", ", failedDepots)}");
            }

            if (downloaded.Count == 0)
                return new AddGameResult { Success = false, Message = "未能下载任何清单文件" };

            ct.ThrowIfCancellationRequested();   // 取消点②：往 Steam depotcache 复制之前
            var manifestCount = _manifestFile.CopyToDepotCache(
                downloaded.Select(d => Path.Combine(tempDir, $"{d.depotId}_{d.manifestGid}.manifest")).ToList(),
                ct);

            // 3. 生成完整 Lua（自动补 depot key / access token）
            var depots = downloaded
                .Select(d => (depotId: d.depotId, manifestGid: d.manifestGid, manifestSize: d.size))
                .ToList();
            var (lua, missingKeys, dlcCount, keyCount) = await _luaBuilder.BuildLuaAsync(appId, "ManifestHub", depots, fixedVersion, addAllDlc, ct);
            // ↓ 之后不再响应取消：原子写极小，宁可让它写完，也不要在"临时文件 + Move"中途撒手
            var luaOk = await _luaBuilder.WriteLuaAsync(appId, lua);

            Log("入库完成!");
            return new AddGameResult
            {
                Success = true,
                Message = $"成功入库 AppID {appId}，下载了 {manifestCount} 个清单，Lua {(luaOk ? "已生成" : "生成失败")}",
                MissingKeys = missingKeys,
                MissingManifests = failedDepots,
                ManifestCount = manifestCount,
                DlcCount = dlcCount,
                KeyCount = keyCount,
            };
        }
        catch (OperationCanceledException)
        {
            return new AddGameResult { Success = false, Cancelled = true, Message = "已取消" };
        }
        catch (Exception ex)
        {
            return new AddGameResult { Success = false, Message = $"ManifestHub 入库失败: {ex.Message}" };
        }
        finally
        {
            ManifestFileService.TryDeleteDir(tempDir);   // 取消时同样清掉半份临时清单
        }
    }

    /// <summary>
    /// Sudama 密钥源入库：获取 depot 信息并生成完整 Lua（补 depot key / access token）。
    /// 不下载清单文件——清单由清单源（MHub / GitHub）负责，Sudama 只作为密钥源。
    /// <paramref name="ct"/> 只在生成 Lua 之前查一次：这条路径没有下载段，取 depot 信息、补密钥
    /// （本地缓存）与生成 Lua 都会把 ct 一路传下去，真正不可打断的只剩 `WriteLuaAsync` 的原子写。
    /// </summary>
    public async Task<AddGameResult> DownloadFromSudamaAsync(
        string appId,
        bool fixedVersion,
        bool addAllDlc,
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        try
        {
            // 1. 获取 depot 信息（含 manifest gid）
            Log("正在获取 Depot 信息...");
            var gameDetails = await _gameInfoService.GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return new AddGameResult { Success = false, Message = "无法获取游戏 Depot 信息" };

            var depotList = gameDetails.Depots.Values.ToList();
            Log($"找到 {depotList.Count} 个 Depot");

            // 2. 组装 depot 列表（gid 来自 Steam API 当前版本）
            var depots = depotList
                .Select(d => (depotId: d.DepotId, manifestGid: d.Manifests.Count > 0 ? d.Manifests[0] : "", manifestSize: 0L))
                .ToList();

            ct.ThrowIfCancellationRequested();   // 取消点：生成 lua 之前（这一步里面有网络，进去了就是链路的最后一站）
            // 3. 生成完整 Lua（自动补 Sudama depot key / access token）
            var (lua, missingKeys, dlcCount, keyCount) = await _luaBuilder.BuildLuaAsync(appId, "Sudama", depots, fixedVersion, addAllDlc, ct);
            // ↓ 之后不再响应取消：原子写极小（见 ManifestDownloadService 顶部注释）
            var luaOk = await _luaBuilder.WriteLuaAsync(appId, lua);

            Log("入库完成!");
            // Sudama 不下清单：把本身带 manifest GID 的 depot 记入缺失清单，由调用方决定是否提示
            var missingManifests = depots
                .Where(d => !string.IsNullOrEmpty(d.manifestGid))
                .Select(d => d.depotId)
                .ToList();
            return new AddGameResult
            {
                Success = true,
                Message = $"成功入库 AppID {appId} (Sudama 密钥源模式)（未下载到清单文件，清单需由清单源获取）Lua {(luaOk ? "已生成" : "生成失败")}",
                MissingKeys = missingKeys,
                MissingManifests = missingManifests,
                ManifestCount = 0,
                DlcCount = dlcCount,
                KeyCount = keyCount,
            };
        }
        catch (OperationCanceledException)
        {
            return new AddGameResult { Success = false, Cancelled = true, Message = "已取消" };
        }
        catch (Exception ex)
        {
            return new AddGameResult { Success = false, Message = $"Sudama 入库失败: {ex.Message}" };
        }
    }

    private ManifestSource? GetSource(string id)
    {
        var config = _configService.Config;
        return config.ManifestSources?.FirstOrDefault(s => s.Id == id);
    }

}

/// <summary>入库结果汇总：成功标记、详细日志、缺失清单/密钥、各类计数（供通知展示）</summary>
public class AddGameResult
{
    public bool Success { get; set; }

    /// <summary>被用户取消（区别于失败：UI 提示「已取消入库」、不弹错误通知、不再尝试下一个源）</summary>
    public bool Cancelled { get; set; }

    public string Message { get; set; } = "";
    public List<string> MissingKeys { get; set; } = new();
    public List<string> MissingManifests { get; set; } = new();
    public int DlcCount { get; set; }
    public int ManifestCount { get; set; }
    public int KeyCount { get; set; }
}
