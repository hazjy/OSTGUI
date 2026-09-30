using System.Text;
using System.Text.RegularExpressions;

namespace OSTGUI.Services;

/// <summary>
/// 清单按需投喂（缓存式）：尾随 **Steam 客户端自己的** <c>&lt;Steam&gt;\logs\content_log.txt</c>，
/// 看到它报告某份清单取不到时，按缓存命中/未命中处理：
///
/// <list type="bullet">
/// <item>持久层 <c>config\depotcache</c> 里已有 → 就地拷进根 depotcache（不联网）；</item>
/// <item>两边都没有 → 从清单源下一份，**双写持久层与根**（持久层即缓存后备，根是客户端真正读的那份）。</item>
/// </list>
///
/// <para><b>为什么读 Steam 的日志而不是内核的 manifest.log（2026-09-30 改）</b>：
/// 内核那份日志只在 Debug 配置编入（ZSteamTool 的 CMakeLists 里是
/// <c>$&lt;$&lt;CONFIG:Debug&gt;:OPENSTEAMTOOL_LOGGING_ENABLED&gt;</c>），Release 内核连日志文件都不产生，
/// 监听会静默失效；Steam 这份与内核编译配置无关，正常启动就有。</para>
///
/// <para><b>触发行的性质</b>：<c>CDepotDownloadMgr::BYldRequestDepotManifest(App: …, Depot: …, Manifest: …)</c>
/// 在客户端**取不到请求码**时才写（实测 124 条全部带 <c>Failed to get manifest request code</c>，
/// 没有成功形态），所以触发即"确实需要帮"，不存在白干；一行里 App / Depot / Manifest 齐全，
/// 行尾还带原因（<c>Access Denied</c> / <c>No connection…</c>）。</para>
///
/// <para>这一层的搬运没有任何自动机制：内核源码不碰 depotcache（全内存 hook），Steam 客户端也只读根，
/// 根又会在卸载/回滚时被清掉——所以"持久层 → 根"必须由这里负责。
/// 只在设置页「清单按需投喂」打开时才运行，关闭时不做任何事（含不写持久层）。</para>
///
/// <para><b>失败会重试</b>：处理失败的那一份只移出在途集合、不记入已处理集合，Steam 下一次写出失败行时重新尝试；
/// 不做退避也不做定时重试，节奏交给 Steam 自己。</para>
/// </summary>
public class ManifestLogWatcher : IDisposable
{
    private const int PollMs = 500;
    /// <summary>投喂串行：MHub 会按请求速率回 429（2026-09-30 实测 2 并发即触发），一次只发一个</summary>
    private const int MaxConcurrentFetches = 1;
    /// <summary>相邻两次请求的最小间隔：1 秒 1 份</summary>
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1);
    /// <summary>单份清单下载的硬上限（秒）。HttpClient.Timeout 配 ResponseHeadersRead 只管到响应头，
    /// 读响应体那步没人管——一旦传输中途卡住就是无限期静默（2026-10-01 实测挂了 5 分钟没有任何日志）。
    /// 清单最大不到 1 MB，30 秒足够。</summary>
    private const int DownloadHardTimeoutSeconds = 30;

    /// <summary>已处理集合的上限；满了清空重来，避免长会话无界增长</summary>
    private const int MaxSeen = 10000;

    // [2026-09-30 10:58:30] CDepotDownloadMgr::BYldRequestDepotManifest(App: 1958220, Depot: 1958221, Manifest: 7941803587227338199, branch: ): Failed to get manifest request code, 'Access Denied'
    // 行尾的原因做成可选：将来 Steam 措辞变了也不至于整条触发失效。
    private static readonly Regex RequestLineRegex = new(
        @"BYldRequestDepotManifest\(App:\s*(?<app>\d+),\s*Depot:\s*(?<depot>\d+),\s*Manifest:\s*(?<manifest>\d+)[^)]*\)(?::\s*Failed to get manifest request code,\s*'(?<reason>[^']*)')?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SteamService _steam;
    private readonly ConfigService _config;
    private readonly ManifestDownloadService _downloads;
    private readonly ManifestFileService _manifestFile;
    /// <summary>用默认行为的 HttpClient（吃系统代理，与程序其它网络请求一致）</summary>
    private readonly HttpClient _client;

    /// <summary>已经处理成功的 (depot,manifest)：不再重复处理</summary>
    private readonly HashSet<(string DepotId, string Gid)> _seen = new();
    /// <summary>正在处理中的 (depot,manifest)：防止同一份被并发触发两次。
    /// 处理**失败**的会从这里移除、且不进 <see cref="_seen"/> —— 于是 Steam 下一次失败时会重新尝试。</summary>
    private readonly HashSet<(string DepotId, string Gid)> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(MaxConcurrentFetches);

    /// <summary>同一 App 的在途批次：在途归零即落定，用来告诉用户"这批齐了，可以重试下载"</summary>
    private readonly Dictionary<string, AppBatch> _batches = new();
    private readonly object _batchLock = new();

    /// <summary>上一次请求发出的时刻（用于维持 1 秒最小间隔）</summary>
    private DateTime _lastRequestAt = DateTime.MinValue;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private sealed class AppBatch
    {
        public int Active;
        public int Done;
        public int Failed;
    }

    public ManifestLogWatcher(SteamService steam, ConfigService config,
        ManifestDownloadService downloads, ManifestFileService manifestFile)
    {
        _steam = steam;
        _config = config;
        _downloads = downloads;
        _manifestFile = manifestFile;

        // 默认行为：一个普通 HttpClient，吃系统代理，与程序其它网络请求一致。
        // 不要再写死 UseProxy=false —— 2026-10-01 试过，等于把请求赶到那条会中途断流的路（直连走 SYD 节点）。
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(60, config.Config.DownloadTimeout))
        };
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>启动尾随（重复调用无副作用）</summary>
    public void Start()
    {
        if (IsRunning) return;

        // 没有清单源密钥时每次投喂都会 401，不如提前说清楚
        if (string.IsNullOrEmpty(MhubApiKey))
        {
            LogService.Event("清单监听：MHub 未配置 API Key，监听未启动（请在设置页「清单源」中配置）");
            return;
        }

        LogService.Event(SelfCheck());

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        LogService.Event("清单监听：已启动");
    }

    /// <summary>停止尾随（重复调用无副作用）</summary>
    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;

        _cts = null;
        _loop = null;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
        _seen.Clear();
        lock (_batchLock) { _batches.Clear(); }
        LogService.Event("清单监听：已停止");
    }

    public void Dispose() => Stop();

    /// <summary>Steam 客户端日志：<c>&lt;Steam&gt;\logs\content_log.txt</c>（正常启动就有，与内核编译配置无关）</summary>
    private string LogPath
    {
        get
        {
            var steamPath = _steam.GetSteamPath();
            return string.IsNullOrEmpty(steamPath)
                ? ""
                : Path.Combine(steamPath, "logs", "content_log.txt");
        }
    }

    private string MhubApiKey
    {
        get
        {
            var source = _config.Config.ManifestSources?.FirstOrDefault(s => s.Id == "mhub");
            return !string.IsNullOrEmpty(source?.ApiKey) ? source!.ApiKey : _config.Config.ManifestHubApiKey;
        }
    }

    /// <summary>
    /// 轮询读取新增内容。用轮询而不是 FileSystemWatcher：日志会被 Steam 轮转/重写，
    /// 监听器的重命名/删除边界比这里几行判断麻烦得多。
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        long pos = 0;
        var attachedTo = "";

        while (!ct.IsCancellationRequested)
        {
            // 每轮重新解析路径：Start 可能早于 Steam 路径检测（App 启动早期），用户也可能中途换路径
            var logPath = LogPath;
            if (logPath != attachedTo)
            {
                attachedTo = logPath;
                // 挂上时跳过一次历史：不重放"启动前已经发生过的请求"。
                // 这份日志跨会话累积，不跳的话每次开 GUI 都会把历史请求全捞一遍。
                // 文件被轮转时长度会回退，那条分支仍然从 0 读（那是新文件，没有历史）。
                pos = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
                if (!string.IsNullOrEmpty(logPath))
                    LogService.Event($"清单监听：已开始监视 {logPath}");
            }

            try
            {
                if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                {
                    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    var len = fs.Length;
                    if (len < pos) pos = 0;   // 被轮转/重写 → 偏移归零，从头读
                    if (len > pos)
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        var buf = new byte[len - pos];
                        var read = 0;
                        while (read < buf.Length)
                        {
                            var n = fs.Read(buf, 0, buf.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }

                        var text = Encoding.UTF8.GetString(buf, 0, read);
                        var lastBreak = text.LastIndexOf('\n');
                        if (lastBreak >= 0)
                        {
                            foreach (var line in text[..lastBreak].Split('\n'))
                                HandleLine(line);
                            // 只推进到最后一个换行：末尾那半行留给下一轮，避免把截断的日志当成一次完整请求
                            pos += Encoding.UTF8.GetByteCount(text[..(lastBreak + 1)]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Diag($"清单监听：读取日志失败：{ex.Message}");
            }

            try { await Task.Delay(PollMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void HandleLine(string line)
    {
        if (!TryParseRequestLine(line, out var appId, out var depotId, out var gid, out var reason)) return;

        var key = (depotId, gid);
        lock (_batchLock)
        {
            if (_seen.Count >= MaxSeen) _seen.Clear();
            if (_seen.Contains(key) || !_inFlight.Add(key)) return;
        }

        var rootDir = _steam.GetDepotCacheDir();
        var cfgDir = _steam.GetConfigDepotCacheDir();
        var name = $"{depotId}_{gid}.manifest";

        if (TrackRequest(appId))
        {
            var why = string.IsNullOrEmpty(reason) ? "" : $"（Steam 报告：{reason}）";
            LogService.Event($"清单投喂：App {appId} 已开始获取清单{why}；请在全部写入后重试游戏下载");
        }

        // 根里有就什么都不用做，但记一行——每次监听到的请求都要在日志里留下交代
        if (!string.IsNullOrEmpty(rootDir) && File.Exists(Path.Combine(rootDir, name)))
        {
            LogService.Event($"清单投喂：Depot {depotId} 的清单 {gid} 已存在于根 depotcache，无需处理");
            MarkDone(key);
            BatchProgress(appId, true);
            return;
        }

        // 根里没有、持久层有：命中缓存，就地补一份到根，不联网
        if (!string.IsNullOrEmpty(rootDir) && !string.IsNullOrEmpty(cfgDir))
        {
            var persistent = Path.Combine(cfgDir, name);
            if (File.Exists(persistent))
            {
                var copied = TryCopyToRoot(persistent, Path.Combine(rootDir, name), depotId, gid);
                if (copied) MarkDone(key); else MarkFailed(key);
                BatchProgress(appId, copied);
                return;
            }
        }

        _ = FetchAsync(appId, depotId, gid);
    }

    /// <summary>把一份已在本地的清单原子落地到根 depotcache（临时名 + Move，不留半份）</summary>
    private static bool TryCopyToRoot(string source, string destPath, string depotId, string gid)
    {
        var tmpPath = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, tmpPath, true);
            File.Move(tmpPath, destPath, true);
            LogService.Event($"清单投喂：Depot {depotId} 的清单 {gid} 缓存命中，已复制到根 depotcache");
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            LogService.Diag($"清单投喂：Depot {depotId} 的清单 {gid} 无法写入根 depotcache：{ex.Message}");
            return false;
        }
    }

    private async Task FetchAsync(string appId, string depotId, string gid)
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var ok = false;
        using var hardCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hardCts.CancelAfter(TimeSpan.FromSeconds(DownloadHardTimeoutSeconds));
        try
        {
            // 1 秒 1 份：MHub 按请求速率回 429；串行 + 最小间隔（等的是"距上次请求"的余量）
            var wait = MinRequestInterval - (DateTime.UtcNow - _lastRequestAt);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _lastRequestAt = DateTime.UtcNow;

            // 未命中：下到临时目录，再双写——持久层当缓存后备，根是客户端实际读的那一份
            LogService.Event($"清单投喂：Depot {depotId} 的清单 {gid} 缓存未命中，已开始下载…");
            var tempDir = Path.Combine(Path.GetTempPath(), "ostgui_manifest_feed");
            var file = await _downloads.DownloadOneManifestAsync(_client, depotId, gid, tempDir, hardCts.Token);
            if (file == null)
            {
                LogService.Event($"清单投喂：未获取到 Depot {depotId} 的清单 {gid}，本次跳过；Steam 下次失败时会重试");
                return;
            }

            var n = _manifestFile.CopyToDepotCache(new List<string> { file }, ct);
            ok = n > 0;
            LogService.Event(ok
                ? $"清单投喂：Depot {depotId} 的清单 {gid} 已下载，并写入持久层与根 depotcache"
                : $"清单投喂：Depot {depotId} 的清单 {gid} 无法写入 depotcache");
            try { File.Delete(file); } catch { }
        }
        catch (OperationCanceledException) when (hardCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // 硬上限触发：连接或传输卡住（不是用户停监听）
            LogService.Event($"清单投喂：Depot {depotId} 的清单 {gid} 超过 {DownloadHardTimeoutSeconds} 秒未完成，本次放弃；Steam 下次失败时会重试");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Diag($"清单投喂：Depot {depotId} 的清单 {gid} 下载异常：{ex.Message}");
        }
        finally
        {
            try { _gate.Release(); } catch { }
            if (ok) MarkDone((depotId, gid)); else MarkFailed((depotId, gid));
            BatchProgress(appId, ok);
        }
    }

    /// <summary>处理成功：进 _seen，后续同一份不再动</summary>
    private void MarkDone((string DepotId, string Gid) key)
    {
        lock (_batchLock) { _inFlight.Remove(key); _seen.Add(key); }
    }

    /// <summary>处理失败：只移出在途，不记入 _seen —— 下一次触发会重新尝试</summary>
    private void MarkFailed((string DepotId, string Gid) key)
    {
        lock (_batchLock) { _inFlight.Remove(key); }
    }

    /// <summary>新请求记入所属 App 批次；返回 true 表示该 App 新开一波</summary>
    private bool TrackRequest(string appId)
    {
        lock (_batchLock)
        {
            if (!_batches.TryGetValue(appId, out var batch))
            {
                batch = new AppBatch { Active = 1 };
                _batches[appId] = batch;
                return true;
            }
            batch.Active++;
            return false;
        }
    }

    /// <summary>单个请求落定；在途归零时给出"这批齐了"的结论</summary>
    private void BatchProgress(string appId, bool ok)
    {
        (int Done, int Failed)? finished = null;
        lock (_batchLock)
        {
            if (!_batches.TryGetValue(appId, out var batch)) return;
            batch.Active--;
            if (ok) batch.Done++;
            else batch.Failed++;
            if (batch.Active <= 0)
            {
                _batches.Remove(appId);
                if (batch.Done > 0 || batch.Failed > 0)
                    finished = (batch.Done, batch.Failed);
            }
        }

        if (finished is (int done, int failed))
        {
            LogService.Event(failed == 0
                ? $"清单投喂：App {appId} 的清单已全部写入（共 {done} 份），请重试游戏下载"
                : $"清单投喂：App {appId} 的清单获取结束（成功 {done} 份，未获取 {failed} 份）；未获取的可重新触发下载后再试");
        }
    }

    /// <summary>从一行 Steam 日志取 (App, Depot, Manifest, 原因)；不是触发行返回 false</summary>
    public static bool TryParseRequestLine(string line, out string appId, out string depotId,
        out string gid, out string reason)
    {
        appId = "";
        depotId = "";
        gid = "";
        reason = "";
        if (string.IsNullOrEmpty(line)) return false;

        var m = RequestLineRegex.Match(line);
        if (!m.Success) return false;

        appId = m.Groups["app"].Value;
        depotId = m.Groups["depot"].Value;
        gid = m.Groups["manifest"].Value;
        reason = m.Groups["reason"].Value;
        return true;
    }

    /// <summary>
    /// 解析自检（Start 时跑一次并写日志）：只认 Steam 那条触发行，内核日志格式与其它行一律不认。
    /// </summary>
    public static string SelfCheck()
    {
        var fail = new List<string>();

        void Check(string line, bool expectMatch, string app = "", string depot = "", string gid = "", string reason = "")
        {
            var matched = TryParseRequestLine(line, out var a, out var d, out var g, out var r);
            if (matched != expectMatch) fail.Add($"匹配判定不符（{(matched ? "已匹配" : "未匹配")}）：{line}");
            else if (matched && (a != app || d != depot || g != gid || r != reason))
                fail.Add($"取值不符：{a}/{d}/{g}/{r}，期望 {app}/{depot}/{gid}/{reason}");
        }

        // 真机原文（2026-09-30 的 content_log.txt）
        Check("[2026-09-30 10:58:30] CDepotDownloadMgr::BYldRequestDepotManifest(App: 1958220, Depot: 1958221, Manifest: 7941803587227338199, branch: ): Failed to get manifest request code, 'Access Denied'",
            true, "1958220", "1958221", "7941803587227338199", "Access Denied");
        Check("[2026-09-30 10:44:40] CDepotDownloadMgr::BYldRequestDepotManifest(App: 457140, Depot: 457141, Manifest: 4837644823067158760, branch: ): Failed to get manifest request code, 'No connection to content servers'",
            true, "457140", "457141", "4837644823067158760", "No connection to content servers");
        // 旧数据源（内核日志）的格式不该再认——换了源之后它必须不匹配
        Check("[2026-09-30 10:44:42.971] [debug] [tid=10860] [Hooks_NetPacket.cpp:609 HandleSend()] GetManifestRequestCode send: depot=4157741 gid=7269192979546626522 jobid=1 app_id=4157740", false);
        Check("[2026-09-30 22:35:04] stats: (SteamCache, 290) cache6-hkg1.steamcontent.com: 9695616 Bytes, 10 sec (7.44 Mbps)", false);
        Check("", false);

        return fail.Count == 0
            ? "清单监听：解析自检通过（5 例）"
            : $"清单监听：解析自检失败 {fail.Count} 例：{string.Join("；", fail)}";
    }
}
