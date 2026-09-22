using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;
/// <summary>
/// Sudama 密钥缓存服务 - 下载并缓存 depot key 与 App 访问令牌
/// </summary>
public class SudamaKeyCache
{
    private readonly ConfigService _configService;

    private const string SudamaApiUrl = "https://api.993499094.xyz/depotkeys.json";
    private const string SudamaTokensUrl = "https://api.993499094.xyz/appaccesstokens.json";

    public SudamaKeyCache(ConfigService configService)
    {
        _configService = configService;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[SudamaKeyCache] {message}");
    }

    /// <summary>
    /// 单次尝试超时（秒）：与设置里的下载超时联动。
    /// gzip 通道实测 MB/s 级，120 秒余量已远超全量文件所需；
    /// 之前固定 300 秒会让卡死的连接挂满 5 分钟才报错，用户只觉得"慢+失败"。
    /// </summary>
    private int DownloadTimeoutSeconds => Math.Max(120, _configService.Config.DownloadTimeout);

    /// <summary>
    /// 创建启用自动解压的下载客户端：
    /// Sudama 明文传输极慢（实测约 23KB/s），gzip 压缩后 MB/s 级。
    /// 注意不能启用 Brotli：服务器对含 br 的协商返回 brotli，而 br 通道极慢，
    /// 只协商 gzip/deflate 才能走快通道
    /// </summary>
    private static HttpClient CreateDownloadClient(int timeoutSeconds)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    /// <summary>
    /// 带重试的流式下载：边收边写入内存流（避免整包 byte[] + 大字符串双重复制），
    /// 失败间隔 1.5s 重试一次。成功返回解析后的字典，均失败返回 null。
    /// </summary>
    private async Task<Dictionary<string, string>?> DownloadJsonAsync(string url, string label, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = CreateDownloadClient(DownloadTimeoutSeconds);
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"{label}下载失败 HTTP {(int)resp.StatusCode}" + (attempt == 1 ? "，1.5s 后重试..." : ""));
                }
                else
                {
                    using var ms = new MemoryStream();
                    await resp.Content.CopyToAsync(ms, ct).ConfigureAwait(false);
                    sw.Stop();
                    ms.Position = 0;
                    var data = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(ms, cancellationToken: ct).ConfigureAwait(false);
                    if (data is not { Count: > 0 })
                    {
                        Log($"{label}返回空数据");
                        return null;
                    }
                    Log($"{label}下载完成：{data.Count} 条，{ms.Length / 1024:N0} KB，耗时 {sw.Elapsed.TotalSeconds:F1}s");
                    return data;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户取消透传；下载超时（也是 OCE）留给下面重试 + 过期缓存兜底
            }
            catch (Exception ex)
            {
                Log($"{label}下载异常({sw.Elapsed.TotalSeconds:F0}s): {ex.Message}" + (attempt == 1 ? "，1.5s 后重试..." : ""));
            }

            if (attempt == 1)
                await Task.Delay(1500, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static string CacheFilePath(string cacheFileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI", cacheFileName);

    /// <summary>
    /// 原子写缓存（临时文件 + Move）：取消/中断若落在写盘中途，也只留一个 .tmp，
    /// 不会把 16MB 的缓存截断——截断的缓存会让之后**所有**入库静默少密钥（比崩溃更难查）
    /// </summary>
    private static async Task WriteCacheAsync(string cacheFileName, Dictionary<string, string> data)
    {
        var cachePath = CacheFilePath(cacheFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var cache = new SudamaCache { Data = data };
        var json = JsonSerializer.Serialize(cache);
        var tmpPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json).ConfigureAwait(false);
        File.Move(tmpPath, cachePath, true);
    }

    /// <summary>
    /// 取"按键查询器"（缓存优先，缺缓存才下载）——入库链路专用。
    ///
    /// 为什么不用 <c>Dictionary&lt;string,string&gt;</c>：密钥缓存是 17.5MB / 22 万条，
    /// 物化成字典要 ~40-55MB 的字符串+字典，再叠加读文件时的 ~33MB 字符串 → **单次入库瞬时 ~110MB**，
    /// 而且都是 LOH 大对象（峰值过后工作集退不回去）。入库其实只要点查几十个 id，
    /// 所以这里用 <see cref="JsonDocument"/> 惰性解析：只留解析后的 UTF-8 文档（~18MB），不造 22 万条 string。
    ///
    /// 形状兼容：老的 <c>{"Data":{…}}</c>（本程序写的）与原始明文 <c>{…}</c> 都能读。
    /// ⚠️ 调用方必须 <c>using</c>（Dispose 才会释放那份文档）。
    /// </summary>
    private async Task<SudamaLookup> LoadLookupAsync(string cacheFileName, string url, string label, CancellationToken ct = default)
    {
        var cachePath = CacheFilePath(cacheFileName);

        if (File.Exists(cachePath))
        {
            var cached = await TryOpenLookupAsync(cachePath, label, ct).ConfigureAwait(false);
            if (cached != null) return cached;
        }

        // 缓存缺失/读不动 → 下载（这条路径本来就产出字典，用它建查询器，省一次解析）
        Log($"正在下载 {label}...");
        var data = await DownloadJsonAsync(url, label, ct).ConfigureAwait(false);
        if (data != null)
        {
            try { await WriteCacheAsync(cacheFileName, data).ConfigureAwait(false); } catch { }
            return FromDictionary(data);
        }

        // 下载失败：再试一次现有缓存（过期兜底，等价于原来的 TryLoadStaleCache）
        return await TryOpenLookupAsync(cachePath, label, ct).ConfigureAwait(false) ?? SudamaLookup.Empty;
    }

    /// <summary>惰性打开缓存文件；读不动返回 null（由调用方决定下载或降级）</summary>
    private static async Task<SudamaLookup?> TryOpenLookupAsync(string cachePath, string label, CancellationToken ct)
    {
        try
        {
            await using var fs = File.OpenRead(cachePath);
            var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("Data", out var inner) && inner.ValueKind == JsonValueKind.Object)
                return new SudamaLookup(doc, inner);      // {"Data":{…}}

            if (root.ValueKind == JsonValueKind.Object)
                return new SudamaLookup(doc, root);       // 明文 {…}

            doc.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户取消透传；读盘出错仍走"改用下载"分支
        }
        catch
        {
            return null;
        }
    }

    private static SudamaLookup FromDictionary(Dictionary<string, string> data)
    {
        var doc = JsonSerializer.SerializeToDocument(data);
        return new SudamaLookup(doc, doc.RootElement);
    }

    /// <summary>depot key 缓存（sudama_cache.json）</summary>
    public Task<SudamaLookup> LoadDepotKeysAsync(CancellationToken ct = default) =>
        LoadLookupAsync("sudama_cache.json", SudamaApiUrl, "Sudama 密钥", ct);

    /// <summary>App 访问令牌缓存（token_cache.json）</summary>
    public Task<SudamaLookup> LoadAccessTokensAsync(CancellationToken ct = default) =>
        LoadLookupAsync("token_cache.json", SudamaTokensUrl, "App 访问令牌", ct);

    /// <summary>
    /// 只读的按键查询器：包一层 <see cref="JsonDocument"/>，按 id 点查。
    /// 用完必须 Dispose（文档持有 ~18MB 缓冲）。<see cref="Empty"/> 表示"没有数据"，点查恒 false。
    /// </summary>
    public sealed class SudamaLookup : IDisposable
    {
        private readonly JsonDocument? _doc;
        private readonly JsonElement _map;

        internal SudamaLookup(JsonDocument? doc, JsonElement map)
        {
            _doc = doc;
            _map = map;
        }

        public static SudamaLookup Empty { get; } = new(null, default);

        public bool TryGet(string id, out string value)
        {
            value = "";
            if (string.IsNullOrEmpty(id) || _map.ValueKind != JsonValueKind.Object) return false;
            if (!_map.TryGetProperty(id, out var el) || el.ValueKind != JsonValueKind.String) return false;
            value = el.GetString() ?? "";
            return value.Length > 0;
        }

        public void Dispose() => _doc?.Dispose();
    }

    /// <summary>
    /// 手动导入本地下载的缓存文件（浏览器直连下载通常远快于应用内下载）。
    /// 文件名含 depotkey/token 即可自动识别类型；识别不出时按内容启发：
    /// 密钥值均为 64 位十六进制，令牌为长数字串。兼容包装格式 {"Data":{...}}，
    /// 也兼容明文 {"id":"value"} 字典。导入成功即写入新缓存。
    /// </summary>
    public async Task<(bool ok, string message)> ImportFilesAsync(IEnumerable<string> filePaths)
    {
        var msgs = new List<string>();
        var okCount = 0;
        var total = 0;

        foreach (var path in filePaths)
        {
            total++;
            var name = Path.GetFileName(path);
            try
            {
                if (!File.Exists(path)) { msgs.Add($"{name}：文件不存在"); continue; }
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);

                Dictionary<string, string>? data = null;
                try
                {
                    var wrapper = JsonSerializer.Deserialize<SudamaCache>(json);
                    if (wrapper?.Data is { Count: > 0 }) data = wrapper.Data;
                }
                catch { }
                data ??= JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data is not { Count: > 0 }) { msgs.Add($"{name}：未解析出有效数据"); continue; }

                var kind = DetectKind(name, data);
                if (kind == null) { msgs.Add($"{name}：无法识别是密钥还是令牌"); continue; }

                await WriteCacheAsync(
                    kind == "keys" ? "sudama_cache.json" : "token_cache.json", data).ConfigureAwait(false);
                okCount++;
                msgs.Add($"{name} → {(kind == "keys" ? "密钥" : "访问令牌")}已导入（{data.Count} 条）");
            }
            catch (Exception ex)
            {
                msgs.Add($"{name}：导入失败 {ex.Message}");
            }
        }

        return (okCount > 0 && okCount == total, string.Join("；", msgs));
    }

    private static string? DetectKind(string fileName, Dictionary<string, string> data)
    {
        var n = fileName.ToLowerInvariant();
        if (n.Contains("depotkey")) return "keys";
        if (n.Contains("token")) return "tokens";

        var sample = data.Values.Take(20).ToList();
        var hex64 = sample.Count(v => v.Length == 64 && v.All(Uri.IsHexDigit));
        if (sample.Count > 0 && hex64 == sample.Count) return "keys";
        if (sample.Count > 0 && hex64 == 0) return "tokens";
        return null;
    }

    /// <summary>
    /// 手动强制刷新缓存：密钥与令牌并行下载（互不阻塞），无论缓存新旧一律覆盖
    /// </summary>
    public async Task<(bool ok, string message)> RefreshAsync()
    {
        var keysTask = ForceRefreshAsync("sudama_cache.json", SudamaApiUrl, "Sudama 密钥");
        var tokensTask = ForceRefreshAsync("token_cache.json", SudamaTokensUrl, "App 访问令牌");
        await Task.WhenAll(keysTask, tokensTask).ConfigureAwait(false);

        var (keysOk, keysMsg) = keysTask.Result;
        var (tokensOk, tokensMsg) = tokensTask.Result;

        if (keysOk && tokensOk)
            return (true, $"Sudama 缓存已更新：{keysMsg}；{tokensMsg}");
        if (keysOk || tokensOk)
            return (false, $"Sudama 缓存刷新不完整：{keysMsg}；{tokensMsg}");
        return (false, $"Sudama 缓存刷新失败：{keysMsg}；{tokensMsg}");
    }

    /// <summary>
    /// 强制下载单个缓存文件并覆盖本地缓存；失败时尝试保留旧缓存
    /// </summary>
    private async Task<(bool ok, string message)> ForceRefreshAsync(
        string cacheFileName, string url, string label)
    {
        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OSTGUI", cacheFileName);

        Log($"正在刷新 {label}...");
        try
        {
            var data = await DownloadJsonAsync(url, label).ConfigureAwait(false);
            if (data == null)
            {
                var stale = TryLoadStaleCache(cachePath);
                return (stale.Count > 0,
                    $"{label}下载失败（已重试）" + (stale.Count > 0 ? "，已保留旧缓存" : "，且无可用旧缓存"));
            }

            try
            {
                await WriteCacheAsync(cacheFileName, data).ConfigureAwait(false);
            }
            catch { }

            return (true, $"{label}已更新（{data.Count} 条）");
        }
        catch (Exception ex)
        {
            var stale = TryLoadStaleCache(cachePath);
            return (stale.Count > 0,
                $"{label}刷新异常: {ex.Message}，已保留旧缓存");
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


}
public class SudamaCache
{
    public Dictionary<string, string> Data { get; set; } = new();
}
