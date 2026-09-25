using System.IO.Compression;
using System.Security.Cryptography;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 修改器文件的本地管理：下载（进度/取消/原子落盘）、zip 解压、列目录、删除、打开所在目录。
/// 默认目录 <c>%LOCALAPPDATA%\OSTGUI\trainers</c>，可在页面上改（<c>AppConfig.TrainerDownloadDir</c>）。
///
/// 下载必须照抄浏览器的请求（2026-09-25 实测）：
/// - 站点在 Cloudflare 后面且防盗链：**必须带浏览器 UA + 详情页 Referer**（只带 UA 会 403）；
/// - 直链会 302 两次：<c>/downloads/x</c> → <c>/download-trainer.php?path=…</c>
///   → <c>wp-content/uploads/trainer-files/…zip/名字.exe</c>，所以自己跟跳转
///   （HttpClient 的自动跳转不会替我们带上自定义 Referer）。
/// 落盘的附件**其实是 exe**（不是 zip；站点只是把单文件放在"zip/名.exe"这种路径下），
/// 所以仍按内容嗅探（开头 "PK" 才是 zip）决定要不要解压。
/// </summary>
public class TrainerDownloadService
{
    /// <summary>默认目录（也是 bindings.json / monitor.pid 的固定位置——监控进程要一个稳定路径）</summary>
    public static string DefaultDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSTGUI", "trainers");

    private const int TimeoutSeconds = 600;
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly ConfigService _config;

    public TrainerDownloadService(ConfigService config) => _config = config;

    /// <summary>当前下载目录：配置里有就用它，否则默认目录</summary>
    public string Dir
    {
        get
        {
            var custom = _config.Config.TrainerDownloadDir;
            return string.IsNullOrWhiteSpace(custom) ? DefaultDir : custom;
        }
    }

    /// <summary>
    /// 已下载修改器的**索引**：放 <c>%LOCALAPPDATA%\OSTGUI\trainers.json</c>（与 config.json 同目录，
    /// 不随下载目录变，也不放在修改器目录里）。
    /// </summary>
    private static string IndexPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSTGUI", "trainers.json");

    private sealed class IndexEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        /// <summary>详情页地址——「更新」要靠它重新取最新附件（旧记录可能为空，见 <see cref="CommitUpdate"/> 的提示）</summary>
        public string PageUrl { get; set; } = "";
        public string SourceUrl { get; set; } = "";
        public DateTime AddedAt { get; set; }
    }

    /// <summary>
    /// 已下载的修改器 = **索引里的条目**（文件还在的）。
    /// 刻意不扫目录：修改器自带一堆其他 exe（游戏原版 exe、工具链），递归扫会把它们都当成修改器。
    /// 索引里指向的文件被手动删掉时，顺手把该条目摘掉。
    /// </summary>
    public List<TrainerInfo> ListLocal()
    {
        var result = new List<TrainerInfo>();
        try
        {
            var entries = ReadIndex();
            var alive = entries.Where(e => File.Exists(e.Path)).ToList();
            if (alive.Count != entries.Count) WriteIndex(alive);

            foreach (var entry in alive)
            {
                result.Add(new TrainerInfo
                {
                    GameName = entry.Name,
                    LocalPath = entry.Path,
                    PageUrl = entry.PageUrl,     // 「更新」要用它回详情页取最新版
                    UpdateDate = File.GetLastWriteTime(entry.Path).ToString("yyyy.MM.dd"),
                });
            }
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 读取索引失败: {ex.Message}");
        }
        return result.OrderBy(r => r.GameName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<IndexEntry> ReadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new List<IndexEntry>();
            return System.Text.Json.JsonSerializer.Deserialize<List<IndexEntry>>(File.ReadAllText(IndexPath))
                   ?? new List<IndexEntry>();
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 索引解析失败（当空处理）: {ex.Message}");
            return new List<IndexEntry>();
        }
    }

    private static void WriteIndex(List<IndexEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
            var temp = IndexPath + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(entries,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 索引写入失败: {ex.Message}");
        }
    }

    /// <summary>记录一个修改器（按路径去重）</summary>
    private static void AddToIndex(string name, string path, string pageUrl, string sourceUrl)
    {
        var entries = ReadIndex();
        entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        entries.Add(new IndexEntry
        {
            Name = name,
            Path = path,
            PageUrl = pageUrl,
            SourceUrl = sourceUrl,
            AddedAt = DateTime.Now,
        });
        WriteIndex(entries);
    }

    /// <summary>
    /// 「更新」的收尾：新文件已就位后，删旧文件（含旧的解压目录）、把索引换到新路径、
    /// 并把绑定里指向旧路径的条目改指新路径。
    /// 顺序刻意是"新文件先就位 → 再动旧引用"：中途失败也不会把还能用的旧文件弄没。
    /// </summary>
    public void CommitUpdate(string oldPath, string newPath, string newName, string pageUrl, string sourceUrl)
    {
        var entries = ReadIndex();
        var old = entries.FirstOrDefault(e => string.Equals(e.Path, oldPath, StringComparison.OrdinalIgnoreCase));

        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(oldPath);

            // 旧文件是 zip 解压出来的 → 连同那个解压目录一起清掉（里面全是旧版文件）
            var oldDir = Path.GetDirectoryName(oldPath);
            if (oldDir != null && !string.Equals(oldDir, DefaultDir, StringComparison.OrdinalIgnoreCase))
                TryDeleteDirectory(oldDir);
        }

        entries.RemoveAll(e => string.Equals(e.Path, oldPath, StringComparison.OrdinalIgnoreCase));
        entries.RemoveAll(e => string.Equals(e.Path, newPath, StringComparison.OrdinalIgnoreCase));
        entries.Add(new IndexEntry
        {
            Name = newName,
            Path = newPath,
            PageUrl = string.IsNullOrEmpty(pageUrl) ? old?.PageUrl ?? "" : pageUrl,
            SourceUrl = sourceUrl,
            AddedAt = DateTime.Now,
        });
        WriteIndex(entries);
        LogService.AddAppLog($"trainer 更新完成：{Path.GetFileName(oldPath)} → {Path.GetFileName(newPath)}（绑定按名称查索引，无需改写）");
    }

    /// <summary>按名称查修改器的实际路径（绑定靠它把"名称"还原成文件；名称就是索引里的 Name）</summary>
    public static string? FindTrainerPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var entry = ReadIndex().FirstOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            return entry != null && File.Exists(entry.Path) ? entry.Path : null;
        }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { LogService.AddAppLog($"trainer 删除旧文件失败 {Path.GetFileName(path)}: {ex.Message}"); }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { LogService.AddAppLog($"trainer 删除旧目录失败 {Path.GetFileName(dir)}: {ex.Message}"); }
    }

    private static void RemoveFromIndex(string path)
    {
        var entries = ReadIndex();
        if (entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) > 0)
            WriteIndex(entries);
    }

    /// <summary>
    /// 下载到 <see cref="Dir"/>，若是 zip 就地解压；返回可执行的 exe 路径（失败/取消返回 null）。
    /// <paramref name="referer"/> 传详情页地址（防盗链要用）。先写 <c>.part</c>、成功再落最终名。
    /// </summary>
    public async Task<(string? Path, string Error)> DownloadAsync(
        string url, string fileName, string referer, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        var bare = Path.GetFileNameWithoutExtension(fileName);      // 附件标题没有扩展名，先按无扩展名处理
        var temp = Path.Combine(Dir, bare + ".part");

        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            using var resp = await FollowRedirectsAsync(client, url, referer, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0) progress?.Report(Math.Round((double)read / total * 100, 1));
                }
            }

            progress?.Report(100);
            var final = Path.Combine(Dir, bare + (IsZip(temp) ? ".zip" : ".exe"));
            File.Move(temp, final, overwrite: true);
            LogService.AddAppLog($"trainer 下载完成 {Path.GetFileName(final)}（{new FileInfo(final).Length / 1024} KB，SHA256 {Sha256(final)[..16]}…）");

            var result = IsZip(final) ? Extract(final) : final;
            if (result != null) AddToIndex(bare, result, referer, url);   // referer 就是详情页：「更新」要靠它
            return (result, "");
        }
        catch (OperationCanceledException)
        {
            LogService.AddAppLog($"trainer 下载已取消 {bare}");
            return (null, "已取消");
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 下载失败 {bare}: {ex.Message}");
            return (null, ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>自己跟 302（最多 5 跳），每一跳都带详情页 Referer——站点只认这种"像浏览器"的请求</summary>
    private static async Task<HttpResponseMessage> FollowRedirectsAsync(
        HttpClient client, string url, string referer, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; hop < 5; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (!string.IsNullOrEmpty(referer)) request.Headers.Referrer = new Uri(referer);

            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
            {
                var next = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(new Uri(current), resp.Headers.Location);
                resp.Dispose();
                current = next.ToString();
                continue;
            }
            return resp;
        }
        throw new IOException("下载地址跳转次数过多");
    }

    /// <summary>解压到与压缩包同名的目录，返回里面第一个 exe（没有 exe 就返回 null）</summary>
    private string? Extract(string zipPath)
    {
        var dir = Path.Combine(Dir, Path.GetFileNameWithoutExtension(zipPath));
        var dirFull = Path.GetFullPath(dir + Path.DirectorySeparatorChar);
        try
        {
            Directory.CreateDirectory(dir);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;   // 目录项

                // 防 zip-slip：解出来的路径必须仍在目标目录里
                var target = Path.GetFullPath(Path.Combine(dir, entry.FullName));
                if (!target.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            var exe = EnumerateExes(dir).FirstOrDefault();
            LogService.AddAppLog($"trainer 已解压到 {Path.GetFileName(dir)}（exe: {(exe == null ? "无" : Path.GetFileName(exe))}）");
            return exe;
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 解压失败 {Path.GetFileName(zipPath)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>目录下的 exe（最多两层，跳过临时文件）</summary>
    private static IEnumerable<string> EnumerateExes(string root)
    {
        var result = new List<string>();
        try
        {
            result.AddRange(Directory.GetFiles(root, "*.exe"));

            foreach (var sub in Directory.GetDirectories(root))
                result.AddRange(Directory.GetFiles(sub, "*.exe", SearchOption.AllDirectories));
        }
        catch { }
        return result.Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
        }
        catch { return false; }
    }

    public void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            RemoveFromIndex(path);
            LogService.AddAppLog($"trainer 已删除 {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 删除失败 {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>打开所在目录并选中该文件（explorer 的 /select）</summary>
    public void RevealInExplorer(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 打开目录失败: {ex.Message}");
        }
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
