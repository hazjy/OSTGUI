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
            // 源可用性：点播源（MHub）与整包源（Ryuu）任一存在即可；两家都没有才拒跑
            if (GetPerDepotSources().Count == 0 && GetWholeAppSources().Count == 0)
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
            // 把本次实际参与级联的源与顺序写进日志：源被漏掉（未勾选 / 能力标记不对 / 配置没同步）
            // 过去完全看不出来，只能从"只有某一家的 404"倒推（2026-10-27 排查 Ryuu 404 时就是靠猜）。
            Log($"清单源级联顺序：{string.Join(" → ", GetPerDepotSources().Select(s => s.Name))}");
            if (GetWholeAppSources().Count > 0)
                Log($"整包兜底源：{string.Join(" → ", GetWholeAppSources().Select(s => s.Name))}（点播源全失败时才用）");

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

            // 点播源没拿全（或没有点播源）时退到整包源：一次下整个 app 的 zip，解出 <depot>_<gid>.manifest。
            // ⚠️ 整包里的 gid 未必等于 Steam API 给的 gid（站点自己爬到的版本），落盘名与 Lua 必须用 zip 里的那个。
            if (failedDepots.Count > 0 || downloaded.Count == 0)
            {
                var (whole, _) = await DownloadWholeAppAsync(dlClient, appId, tempDir, ct);
                if (whole.Count > 0)
                {
                    var have = downloaded.Select(d => d.depotId).ToHashSet();
                    foreach (var item in whole)
                    {
                        if (have.Contains(item.depotId)) continue;
                        downloaded.Add(item);
                        failedDepots.Remove(item.depotId);
                    }
                }
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
    /// **已启用 + 已接入 + 能按 depot+gid 点播**的源，任一成功即返回。
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

            // ⚠️ URL 里带密钥：任何日志都不要打完整 URL
            var url = source.BuildUrl(null, depotId, manifestGid);
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

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
    /// </summary>
    public async Task<(bool ok, string text)> ProbeSourceAsync(ManifestSource source, CancellationToken ct = default)
    {
        if (!source.IsEnabled) return (false, "未启用");
        if (source.RequiresToken && string.IsNullOrEmpty(source.ApiKey)) return (false, "未填密钥");
        if (!source.PerDepotFetch) return (false, "该源不支持按 depot 取清单");

        var tmp = Path.Combine(Path.GetTempPath(), "ostgui_probe_" + Guid.NewGuid().ToString("N") + ".manifest");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(30, _configService.Config.DownloadTimeout)) };
            using var response = await client.GetAsync(source.BuildUrl(null, ProbeDepotId, ProbeManifestGid),
                HttpCompletionOption.ResponseHeadersRead, ct);

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

    // 探针配对：CS2(730) 的 depot 731 与其固定 gid（2026-10-24 实测 MHub 200、内层 magic 正确）
    private const string ProbeDepotId = "731";
    private const string ProbeManifestGid = "1914580569200573996";

    /// <summary>取可点播的清单源，按优先级升序。</summary>
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
            .Where(s => s.IsEnabled && ManifestSource.IsImplementedSource(s.Id) && s.PerDepotFetch)
            .OrderBy(s => s.Priority)
            .ToList();
    }

    /// <summary>整包源本次运行被明确拒绝过（401/403/429）就不再打它，免得把额度烧在必然失败的请求上。</summary>
    private bool _wholeAppRejectedThisSession;

    /// <summary>取整包源（按 appid 一次拿 zip 的那种），按优先级升序。</summary>
    private List<ManifestSource> GetWholeAppSources()
    {
        var all = _configService.Config.ManifestSources;
        if (all is null || all.Count == 0) return new List<ManifestSource>();

        return all
            .Where(s => s.IsEnabled && ManifestSource.IsImplementedSource(s.Id) && s.WholeAppFetch)
            .OrderBy(s => s.Priority)
            .ToList();
    }

    /// <summary>
    /// 整包兜底：向整包源要这个 app 的 zip，解出全部 <c>&lt;depot&gt;_&lt;gid&gt;.manifest</c> 落到
    /// <paramref name="destDir"/>，返回 (depotId, gid, size) 列表（已校验内层 magic）。
    /// 日志记一行条目数与字节数；单源失败不影响下一个源。
    /// </summary>
    public async Task<(List<(string depotId, string manifestGid, long size)> got, string? failReason)> DownloadWholeAppAsync(
        HttpClient client, string appId, string destDir, CancellationToken ct = default)
    {
        var sources = GetWholeAppSources();
        if (sources.Count == 0) return (new(), null);

        Directory.CreateDirectory(destDir);
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (_wholeAppRejectedThisSession)
            {
                Log($"整包源「{source.Name}」本次运行已被拒绝过（密钥无效或额度用尽），跳过：AppID {appId}");
                break;
            }
            var url = source.BuildUrl(appId);   // 替换 {appid} 与 {key}；日志里不打 URL（带密钥）
            var zipPath = Path.Combine(Path.GetTempPath(), $"ostgui_whole_{appId}_{Guid.NewGuid():N}.zip");
            try
            {
                long bytes;
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var code = (int)response.StatusCode;
                        if (code is 401 or 403 or 429) _wholeAppRejectedThisSession = true;
                        Log($"整包源「{source.Name}」下载失败（HTTP {code}）"
                            + (code is 401 or 403 ? "（密钥无效或未登录）" : code == 429 ? "（当日额度用尽）" : "")
                            + $"：AppID {appId}");
                        continue;
                    }

                    await using (var fs = File.Create(zipPath))
                    await using (var src = await response.Content.ReadAsStreamAsync(ct))
                        await src.CopyToAsync(fs, ct);
                    bytes = new FileInfo(zipPath).Length;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var got = ExtractManifests(zipPath, destDir);
                sw.Stop();
                if (got.Count == 0)
                {
                    Log($"整包源「{source.Name}」的包里没有可用清单：AppID {appId}（{bytes / 1024} KB）");
                    continue;
                }

                var entries = CountManifestEntries(zipPath);
                Log($"整包源「{source.Name}」解出 {got.Count} 份清单：AppID {appId}，"
                    + $"zip {bytes / 1024} KB / {entries} 条目 / 解包 {sw.ElapsedMilliseconds} ms");
                return (got, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"整包源「{source.Name}」下载异常：AppID {appId}，{ex.Message}");
            }
            finally
            {
                TryDelete(zipPath);
            }
        }

        Log($"所有整包源都没能拿到 AppID {appId} 的清单");
        return (new(), "整包源都没能拿到清单");
    }

    /// <summary>
    /// 把整包 zip 里的 <c>&lt;depot&gt;_&lt;gid&gt;.manifest</c> 解到 <paramref name="destDir"/>，逐份校验
    /// 内层 magic；不符合的直接删掉。其余条目（<c>&lt;appid&gt;.lua</c>、目录项）忽略。
    /// </summary>
    internal static List<(string depotId, string manifestGid, long size)> ExtractManifests(string zipPath, string destDir)
    {
        var got = new List<(string, string, long)>();
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (!TryParseManifestEntryName(entry.FullName, out var depotId, out var gid)) continue;

            var target = Path.Combine(destDir, $"{depotId}_{gid}.manifest");
            try
            {
                entry.ExtractToFile(target, true);
            }
            catch
            {
                continue;
            }

            if (!IsInnerManifest(target))
            {
                TryDelete(target);
                continue;
            }
            got.Add((depotId, gid, new FileInfo(target).Length));
        }
        return got;
    }

    private static int CountManifestEntries(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 解析整包 zip 里的条目名：<c>&lt;depot&gt;_&lt;gid&gt;.manifest</c>（允许带目录前缀）。
    /// 只认"两段全数字 + .manifest 结尾"，避免把 <c>730.lua</c>、<c>__MACOSX/…</c> 当清单。
    /// </summary>
    internal static bool TryParseManifestEntryName(string entryName, out string depotId, out string gid)
    {
        depotId = gid = string.Empty;
        if (string.IsNullOrEmpty(entryName)) return false;

        var name = entryName.Replace('\\', '/');
        var parts = name.Split('/');
        // 允许普通目录前缀（有的 zip 会套一层），但跳过 macOS 垃圾条目（__MACOSX/._xxx）
        if (parts.Length > 1)
        {
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].StartsWith("__", StringComparison.Ordinal) || parts[i].StartsWith('.')) return false;
            }
        }
        name = parts[^1];
        if (!name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) return false;
        name = name[..^".manifest".Length];

        var us = name.IndexOf('_');
        if (us <= 0 || us == name.Length - 1) return false;
        var maybeDepot = name[..us];
        var maybeGid = name[(us + 1)..];
        if (!maybeDepot.All(char.IsAsciiDigit) || !maybeGid.All(char.IsAsciiDigit)) return false;

        depotId = maybeDepot;
        gid = maybeGid;
        return true;
    }

    /// <summary>
    /// 自检（挂 <c>OSTGUI.exe --trainer-selftest</c> 链）：整包解包链路的纯逻辑——条目名解析 + zip 解包
    /// + 内层 magic 过滤。真网络请求不在这里做（要 auth code），只在入库时跑。
    /// </summary>
    public static string SelfCheck()
    {
        // 条目名解析：认清单、拒其它
        var accept = new (string name, string depot, string gid)[]
        {
            ("731_1914580569200573996.manifest", "731", "1914580569200573996"),
            ("folder/731_1914580569200573996.manifest", "731", "1914580569200573996"),
            ("folder\\731_1914580569200573996.MANIFEST", "731", "1914580569200573996"),
        };
        foreach (var (name, depot, gid) in accept)
        {
            if (!TryParseManifestEntryName(name, out var d, out var g) || d != depot || g != gid)
                return $"整包自检：条目名「{name}」应被接受并解析为 {depot}/{gid}";
        }

        foreach (var name in new[] { "730.lua", "__MACOSX/731_1.manifest", "731.manifest", "_1.manifest", "731_.manifest", "a_1.manifest", "731_1.manifest.bak" })
        {
            if (TryParseManifestEntryName(name, out _, out _))
                return $"整包自检：条目名「{name}」应被拒绝";
        }

        // 解包：合法清单留下、magic 不符的删掉、lua 忽略
        var tmpDir = Path.Combine(Path.GetTempPath(), "ostgui_wholecheck_" + Guid.NewGuid().ToString("N"));
        var zipPath = tmpDir + ".zip";
        try
        {
            Directory.CreateDirectory(tmpDir);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "730.lua", "addappid(730,0,\"x\")"u8.ToArray());
                WriteEntry(zip, "731_1914580569200573996.manifest", [0xD0, 0x17, 0xF6, 0x71, 1, 2, 3, 4]);
                WriteEntry(zip, "732_1.manifest", "<html>error</html>"u8.ToArray());
            }

            var got = ExtractManifests(zipPath, tmpDir);
            if (got.Count != 1) return $"整包自检：应从 3 个条目里解出 1 份清单，实际 {got.Count} 份";
            if (got[0].depotId != "731" || got[0].manifestGid != "1914580569200573996")
                return $"整包自检：解出的清单名不对（{got[0].depotId}/{got[0].manifestGid}）";
            if (File.Exists(Path.Combine(tmpDir, "732_1.manifest")))
                return "整包自检：magic 不符的条目没有删掉";
        }
        finally
        {
            TryDelete(zipPath);
            ManifestFileService.TryDeleteDir(tmpDir);
        }

        return string.Empty;

        static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
        {
            var entry = zip.CreateEntry(name);
            using var s = entry.Open();
            s.Write(bytes);
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
