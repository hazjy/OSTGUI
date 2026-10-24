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
        LogService.Event(message);
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
            // 源可用性：不再只看 MHub 一家——任一"已启用 + 已填密钥"的点播源都算（见 GetPerDepotSources）
            if (GetPerDepotSources().Count == 0)
                return new AddGameResult { Success = false, Message = "未配置可用的清单源，请到设置页「清单源」填好密钥并启用" };

            // 1. 从 Steam 官方 API 获取 depot + manifest gid（不依赖 GitHub）
            Log("正在从 Steam API 获取 depot/manifest 信息…");
            var gameDetails = await _gameInfoService.GetGameDetailsFromSteamAsync(appId, ct);
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

            Log($"找到 {manifestFiles.Count} 个清单文件，开始下载…");

            // 2. 下载每个 manifest
            Directory.CreateDirectory(tempDir);
            var downloaded = new List<(string depotId, string manifestGid, long size)>();
            var failedDepots = new List<string>();

            // manifest 文件可能较大，用独立 HttpClient 设置更长超时（默认 120 秒），
            // 避免受全局 HttpClient.Timeout(30 秒) 限制
            var timeoutSeconds = Math.Max(60, _configService.Config.DownloadTimeout);
            using var dlClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

            foreach (var (depotId, manifestGid) in manifestFiles)
            {
                ct.ThrowIfCancellationRequested();   // 取消点①：开始下一个 depot 之前
                Log($"下载 Depot {depotId} 的清单…");
                var file = await DownloadOneManifestAsync(dlClient, depotId, manifestGid, tempDir, ct);
                if (file == null)
                {
                    failedDepots.Add(depotId);   // 状态码 / 异常细节已由方法写进日志
                    continue;
                }
                downloaded.Add((depotId, manifestGid, new FileInfo(file).Length));
                Log($"已下载 {Path.GetFileName(file)}");
            }

            // 仅在实际内容 depot（appinfo 中有 manifest GID）的清单未下载（HTTP 失败/异常）时才警告；
            // 壳型/共享 depot（无 GID）本就不预下载，属正常，不提示。
            if (failedDepots.Count > 0)
            {
                Log($"警告：以下实际 depot 的清单未能下载，未预下载，将由内核运行时获取：{string.Join(", ", failedDepots)}");
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AddGameResult { Success = false, Cancelled = true, Message = "已取消" };
        }
        catch (Exception ex)
        {
            return new AddGameResult { Success = false, Message = $"ManifestHub 入库失败：{ex.Message}" };
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
            Log("正在获取 Depot 信息…");
            var gameDetails = await _gameInfoService.GetGameDetailsFromSteamAsync(appId, ct);
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
                Message = $"成功入库 AppID {appId}（Sudama 密钥源模式）（未下载到清单文件，清单需由清单源获取）Lua {(luaOk ? "已生成" : "生成失败")}",
                MissingKeys = missingKeys,
                MissingManifests = missingManifests,
                ManifestCount = 0,
                DlcCount = dlcCount,
                KeyCount = keyCount,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AddGameResult { Success = false, Cancelled = true, Message = "已取消" };
        }
        catch (Exception ex)
        {
            return new AddGameResult { Success = false, Message = $"Sudama 入库失败：{ex.Message}" };
        }
    }

    /// <summary>
    /// 抓单份清单并落到 <paramref name="destDir"/>（流式 + `.part` + Move）。
    /// 入库的逐 depot 循环与「清单按需投喂」共用这一条路径。
    ///
    /// 多源级联（2026-10-24 加）：按 <see cref="ManifestSource.Priority"/> 依次尝试所有
    /// **已启用 + 已接入 + 能按 depot+gid 点播**的源（当前是 MHub、Ryuu），任一成功即返回。
    /// 落地前必须校验内层清单 magic（<c>0x71F617D0</c>）——投喂 zip 外壳或错误页会被 Steam
    /// 当损坏清单删掉，看起来像"投喂完全没生效"（见 REF-清单与版本.md）。
    ///
    /// 成功返回落盘文件路径；全部源都失败返回 null——失败细节（状态码 / 异常）在方法内写日志，
    /// 调用方只负责汇总，避免两个调用点各写一份措辞。
    /// 用户取消照原样透传：⚠️ HttpClient 的**超时也是 OCE**（TaskCanceledException），那种必须算
    /// "这份没拿到"而不是"整场取消"，否则一次超时会被判成已取消。
    /// </summary>
    public async Task<string?> DownloadOneManifestAsync(
        HttpClient client,
        string depotId,
        string manifestGid,
        string destDir,
        CancellationToken ct = default)
    {
        var sources = GetPerDepotSources();
        if (sources.Count == 0)
        {
            Log($"没有可用的清单源（请检查设置页「清单源」是否已启用并填好密钥）：Depot {depotId}");
            return null;
        }

        var fileName = $"{depotId}_{manifestGid}.manifest";
        var filePath = Path.Combine(destDir, fileName);
        var partPath = filePath + ".part";

        try { Directory.CreateDirectory(destDir); } catch { /* 落盘时再报 */ }

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            var isRyuu = string.Equals(source.Id, "ryuu", StringComparison.OrdinalIgnoreCase);
            if (isRyuu && !SourceQuota.CanUse(_configService.Config.RyuuQuotaUsed, _configService.Config.RyuuQuotaLimit))
            {
                Log($"清单源「{source.Name}」今日额度已用尽，改用下一个源：Depot {depotId}");
                continue;
            }

            // ⚠️ URL 里带密钥：任何日志都不要打完整 URL
            var url = source.BuildUrl(null, depotId, manifestGid);
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                if (isRyuu)
                {
                    _ = TryReadLimit(response, out var limit);
                    await RecordRyuuUsageAsync(limit, response.IsSuccessStatusCode, ct);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    Log($"清单源「{source.Name}」下载失败（HTTP {code}）：Depot {depotId} 的清单 {manifestGid}"
                        + (code is 401 or 403 ? "（密钥无效或未登录）" : code == 429 ? "（额度用尽）" : ""));
                    continue;
                }

                await using (var fs = File.Create(partPath))
                {
                    await using var src = await response.Content.ReadAsStreamAsync(ct);
                    await src.CopyToAsync(fs, ct);
                }

                // 内层清单 magic：d0 17 f6 71
                if (!IsInnerManifest(partPath))
                {
                    TryDelete(partPath);
                    Log($"清单源「{source.Name}」返回的不是内层清单（magic 不符），已丢弃：Depot {depotId} 的清单 {manifestGid}");
                    continue;
                }

                File.Move(partPath, filePath, true);   // 原子落地：要么没有，要么完整
                return filePath;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(partPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(partPath);
                Log($"清单源「{source.Name}」下载异常（Depot {depotId} 的清单 {manifestGid}）：{ex.Message}");
                // 继续下一个源
            }
        }

        Log($"所有清单源都没能拿到 Depot {depotId} 的清单 {manifestGid}");
        return null;
    }

    /// <summary>
    /// 设置页「测活」：只打**这一家**源，用实测可用的 depot+gid 拿一份清单，看状态码与 magic。
    /// ⚠️ 对有限额的源（Ryuu）这算一次真实请求，会消耗 1 次额度——界面按钮的提示里要写明。
    /// </summary>
    public async Task<(bool ok, string text)> ProbeSourceAsync(ManifestSource source, CancellationToken ct = default)
    {
        if (!source.IsEnabled) return (false, "未启用");
        if (source.RequiresToken && string.IsNullOrEmpty(source.ApiKey)) return (false, "未填密钥");
        if (!source.SupportsPerDepotFetch) return (false, "该源不支持按 depot 取清单");

        var isRyuu = string.Equals(source.Id, "ryuu", StringComparison.OrdinalIgnoreCase);
        if (isRyuu && !SourceQuota.CanUse(_configService.Config.RyuuQuotaUsed, _configService.Config.RyuuQuotaLimit))
            return (false, "今日额度已用尽");

        var tmp = Path.Combine(Path.GetTempPath(), "ostgui_probe_" + Guid.NewGuid().ToString("N") + ".manifest");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(30, _configService.Config.DownloadTimeout)) };
            using var response = await client.GetAsync(source.BuildUrl(null, ProbeDepotId, ProbeManifestGid),
                HttpCompletionOption.ResponseHeadersRead, ct);

            if (isRyuu)
            {
                TryReadLimit(response, out var limit);
                await RecordRyuuUsageAsync(limit, response.IsSuccessStatusCode, ct);
            }

            if (!response.IsSuccessStatusCode)
                return (false, $"否（HTTP {(int)response.StatusCode}）");

            await using (var fs = File.Create(tmp))
            {
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await src.CopyToAsync(fs, ct);
            }

            return IsInnerManifest(tmp) ? (true, "是") : (false, "否（返回的不是内层清单）");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"否（{ex.Message}）");
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    // 实测可用的探针配对：CS2(730) 的 depot 731 与其当前 gid（2026-10-24 Ryuu 实测 200、内层 magic 正确）
    private const string ProbeDepotId = "731";
    private const string ProbeManifestGid = "1914580569200573996";

    /// <summary>取可点播的清单源，按优先级升序（MHub 在前、Ryuu 兜底）。</summary>
    private List<ManifestSource> GetPerDepotSources()
    {
        var all = _configService.Config.ManifestSources;
        if (all is null || all.Count == 0) return new List<ManifestSource>();

        // 旧配置兜底：早先只有全局 ManifestHubApiKey 字段，没写进 mhub 源的 ApiKey
        var mhub = all.FirstOrDefault(s => s.Id == "mhub");
        if (mhub is not null && string.IsNullOrEmpty(mhub.ApiKey) &&
            !string.IsNullOrEmpty(_configService.Config.ManifestHubApiKey))
        {
            mhub.ApiKey = _configService.Config.ManifestHubApiKey;
        }

        return all
            .Where(s => s.IsEnabled && ManifestSource.IsImplementedSource(s.Id) && s.SupportsPerDepotFetch)
            .OrderBy(s => s.Priority)
            .ToList();
    }

    /// <summary>读 Ryuu 响应头里的额度上限（没有就返回 0，表示未知）。</summary>
    private static bool TryReadLimit(HttpResponseMessage response, out int limit)
    {
        limit = 0;
        if (!response.Headers.TryGetValues("x-ratelimit-limit", out var values)) return false;
        var raw = values.FirstOrDefault();
        return int.TryParse(raw, out limit) && limit > 0;
    }

    /// <summary>记一次 Ryuu 用量（按日计；上限有变化就一并写回）。额度写盘失败不影响下载。</summary>
    private async Task RecordRyuuUsageAsync(int limit, bool success, CancellationToken ct)
    {
        try
        {
            await _configService.UpdateAndSaveAsync(c =>
            {
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var (date, used) = SourceQuota.Normalize(today, c.RyuuQuotaDate, c.RyuuQuotaUsed);
                c.RyuuQuotaDate = date;
                c.RyuuQuotaUsed = used + 1;
                if (limit > 0) c.RyuuQuotaLimit = limit;
                // 服务端明确拒绝（401/403/429）时把"今日已用"顶到上限，本会话不再重试这一家，
                // 免得一个入库流程把剩余额度全打在失败请求上；上限本身不改（那是服务器的口径）。
                if (!success && c.RyuuQuotaLimit > 0)
                    c.RyuuQuotaUsed = Math.Max(c.RyuuQuotaUsed, c.RyuuQuotaLimit);
            });
        }
        catch (Exception ex)
        {
            Log($"记录 Ryuu 额度失败（不影响本次下载）：{ex.Message}");
        }
    }

    /// <summary>文件头 4 字节是否为内层清单 magic（0x71F617D0，小端即 d0 17 f6 71）。</summary>
    private static bool IsInnerManifest(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            if (fs.Read(head) < 4) return false;
            return head[0] == 0xD0 && head[1] == 0x17 && head[2] == 0xF6 && head[3] == 0x71;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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
