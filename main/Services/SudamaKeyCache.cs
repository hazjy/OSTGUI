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
        LogService.Event(message);
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
    /// 取"按键查询器"：**流式扫一遍缓存文件，只留下想要的 id**（缓存优先，缺缓存才下载）——入库链路专用。
    ///
    /// 为什么流式（17.5MB / 22 万条的缓存）：
    ///   ① 整份物化成 Dictionary：~40-55MB（先读成字符串还要再 +33MB）→ 单次入库瞬时 ~110MB；
    ///   ② 就算只做 JsonDocument（DOM）也要**整份驻留**：原始字节 17.5MB + 元数据表 ~5MB ≈ 23MB。
    /// 而入库实际只要 appId + 各 depot + 各 DLC 这**几百个** id → 流式扫一遍只留命中项，
    /// 峰值 ≈ 64KB 扫描缓冲（外加命中的几百条字符串）。
    ///
    /// 形状兼容：老的 <c>{"Data":{…}}</c>（本程序写的）与原始明文 <c>{…}</c> 都能读 ——
    /// 扫描只认"深度 ≤ 2 的键值对"，不关心套在哪一层。
    /// </summary>
    private async Task<SudamaLookup> LoadLookupAsync(
        string cacheFileName, string url, string label, IReadOnlyCollection<string> wantedIds, CancellationToken ct)
    {
        var cachePath = CacheFilePath(cacheFileName);

        if (File.Exists(cachePath))
        {
            var cached = await TryScanLookupAsync(cachePath, wantedIds, ct).ConfigureAwait(false);
            if (cached != null) return cached;
        }

        // 缓存缺失 / 读不动 → 下载（这条路径本来就要整份数据写盘，写完后只留想要的那几条）
        Log($"正在下载 {label}...");
        var data = await DownloadJsonAsync(url, label, ct).ConfigureAwait(false);
        if (data != null)
        {
            try { await WriteCacheAsync(cacheFileName, data).ConfigureAwait(false); } catch { }
            return FromDictionary(data, wantedIds);
        }

        // 下载失败：再试一次现有缓存（过期兜底，等价于原来的 TryLoadStaleCache）
        return await TryScanLookupAsync(cachePath, wantedIds, ct).ConfigureAwait(false) ?? SudamaLookup.Empty;
    }

    /// <summary>流式扫描缓存文件；读不动 / 格式不对返回 null（由调用方决定下载或降级）</summary>
    private static async Task<SudamaLookup?> TryScanLookupAsync(string cachePath, IReadOnlyCollection<string> wantedIds, CancellationToken ct)
    {
        try
        {
            var found = await ScanWantedAsync(cachePath, wantedIds, ct).ConfigureAwait(false);
            return new SudamaLookup(found);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户取消透传；读盘 / 格式出错仍走"改用下载"分支
        }
        catch
        {
            return null;
        }
    }

    /// <summary>下载路径本来就产出整份字典（要写盘），从里面挑出想要的几条即可</summary>
    private static SudamaLookup FromDictionary(Dictionary<string, string> data, IReadOnlyCollection<string> wantedIds)
    {
        var found = new Dictionary<string, string>(wantedIds.Count, StringComparer.Ordinal);
        foreach (var id in wantedIds)
        {
            if (data.TryGetValue(id, out var v) && !string.IsNullOrEmpty(v)) found[id] = v;
        }
        return new SudamaLookup(found);
    }

    private const int ScanBufferSize = 64 * 1024;

    /// <summary>
    /// 流式扫 <c>{"id":"value",…}</c>（兼容外面套一层 <c>{"Data":…}</c>），只收集 <paramref name="wantedIds"/> 命中的键值对。
    /// 64KB 分块喂 <see cref="Utf8JsonReader"/>，跨块的部分靠 <see cref="Utf8JsonReader.CurrentState"/> 续读。
    ///
    /// ponytail: 每个属性名都会 `GetString()` 一次（22 万次短字符串，只有 gen0 垃圾）——峰值不受影响；
    /// 真嫌 GC 吵，再改成"按 UTF-8 字节哈希比对"（用 <c>reader.ValueSpan</c> 零分配）。
    /// </summary>
    private static async Task<Dictionary<string, string>> ScanWantedAsync(string path, IReadOnlyCollection<string> wantedIds, CancellationToken ct)
    {
        var want = wantedIds as HashSet<string> ?? new HashSet<string>(wantedIds, StringComparer.Ordinal);
        var found = new Dictionary<string, string>(want.Count, StringComparer.Ordinal);
        if (want.Count == 0) return found;

        await using var fs = File.OpenRead(path);

        // 粗校验：完整 JSON 对象的最后一个非空白字符必须是 '}'。
        // 没有这一步，**截断的缓存**会静默只返回"扫描到的那部分键"→ 入库静默少密钥（最难查的一类问题）；
        // 宁可判为坏文件，让调用方降级（重新下载 / 用旧缓存 / 空）。
        if (!await EndsWithClosingBraceAsync(fs, ct).ConfigureAwait(false))
            throw new JsonException("缓存文件不是完整的 JSON 对象（尾部不是 '}'）");

        fs.Position = 0;
        var buffer = new byte[ScanBufferSize];
        var state = new JsonReaderState();
        string? pendingKey = null;
        int pendingKeyDepth = -1;
        int keep = 0;   // 缓冲里"未消费"的字节数

        while (true)
        {
            var read = await fs.ReadAsync(buffer.AsMemory(keep), ct).ConfigureAwait(false);
            var isFinal = read == 0;
            var available = keep + read;
            if (available == 0) break;

            var reader = new Utf8JsonReader(buffer.AsSpan(0, available), isFinal, state);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    // 只认浅层（根对象 / Data 对象下）的键；再深的就是嵌套内容，不是我们要的形态
                    pendingKey = reader.CurrentDepth <= 2 ? reader.GetString() : null;
                    pendingKeyDepth = reader.CurrentDepth;
                }
                else if (pendingKey != null && reader.TokenType == JsonTokenType.String && reader.CurrentDepth == pendingKeyDepth)
                {
                    if (want.Contains(pendingKey)) found[pendingKey] = reader.GetString() ?? "";
                    pendingKey = null;
                }
                else
                {
                    pendingKey = null;   // 值不是字符串（嵌套对象 / 数组 / 数字）→ 不是我们要的键值对
                }
            }

            state = reader.CurrentState;
            var consumed = (int)reader.BytesConsumed;
            keep = available - consumed;
            if (keep > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, keep);

            if (isFinal) break;
            if (keep == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);   // 单个 token 比缓冲还长：扩
        }

        return found;
    }

    /// <summary>文件最后一个非空白字节是不是 '}'（判断"看起来完整的 JSON 对象"，防止截断缓存被静默采信）</summary>
    private static async Task<bool> EndsWithClosingBraceAsync(FileStream fs, CancellationToken ct)
    {
        if (fs.Length == 0) return false;
        var take = (int)Math.Min(4096, fs.Length);
        fs.Seek(-take, SeekOrigin.End);
        var tail = new byte[take];
        var read = await fs.ReadAsync(tail.AsMemory(0, take), ct).ConfigureAwait(false);
        for (var i = read - 1; i >= 0; i--)
        {
            var b = tail[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return b == (byte)'}';
        }
        return false;
    }

    /// <summary>depot key 缓存（sudama_cache.json）：只取 <paramref name="wantedIds"/> 里命中的</summary>
    public Task<SudamaLookup> LoadDepotKeysAsync(IReadOnlyCollection<string> wantedIds, CancellationToken ct = default) =>
        LoadLookupAsync("sudama_cache.json", SudamaApiUrl, "Sudama 密钥", wantedIds, ct);

    /// <summary>App 访问令牌缓存（token_cache.json）：只取 <paramref name="wantedIds"/> 里命中的</summary>
    public Task<SudamaLookup> LoadAccessTokensAsync(IReadOnlyCollection<string> wantedIds, CancellationToken ct = default) =>
        LoadLookupAsync("token_cache.json", SudamaTokensUrl, "App 访问令牌", wantedIds, ct);

    /// <summary>
    /// 只读的按键查询器：内部只是"流式扫描命中的那几百条"的小字典，不持文档也不持文件。
    /// <see cref="Empty"/> 表示"没有数据"，点查恒 false。
    /// </summary>
    public sealed class SudamaLookup
    {
        private readonly Dictionary<string, string> _found;

        internal SudamaLookup(Dictionary<string, string> found) => _found = found;

        public static SudamaLookup Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

        public bool TryGet(string id, out string value)
        {
            if (_found.TryGetValue(id, out var v)) { value = v; return v.Length > 0; }
            value = "";
            return false;
        }
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

        // 刷新路径必然整份物化（17.5MB 的 MemoryStream + 字典 + 再序列化），干完把空洞还回去
        OstMemory.CompactAfterLargeBuffers();

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
