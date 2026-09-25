using System.IO.Compression;
using System.Security.Cryptography;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 修改器文件的本地管理：下载（进度/取消/原子落盘）、zip 解压、列目录、删除、打开所在目录。
/// 存放目录：<c>%LOCALAPPDATA%\OSTGUI\trainers</c>（与 bindings.json 同目录）。
///
/// FLiNG 的附件是 zip 且**标题里没有扩展名**（如 <c>Elden.Ring.v1.02-v1.16.1.Plus.35.Trainer-FLiNG</c>），
/// 所以按内容嗅探（开头 "PK"）判断是否要解压，而不是看扩展名。
/// </summary>
public class TrainerDownloadService
{
    public static string TrainerDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSTGUI", "trainers");

    private const int TimeoutSeconds = 600;

    /// <summary>已下载的修改器 = 目录下的 exe（含解压出来的子目录，最多看两层）</summary>
    public List<TrainerInfo> ListLocal()
    {
        var result = new List<TrainerInfo>();
        try
        {
            if (!Directory.Exists(TrainerDir)) return result;

            foreach (var file in EnumerateExes(TrainerDir))
            {
                result.Add(new TrainerInfo
                {
                    GameName = Path.GetFileNameWithoutExtension(file),
                    LocalPath = file,
                    UpdateDate = File.GetLastWriteTime(file).ToString("yyyy.MM.dd"),
                });
            }
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 列目录失败: {ex.Message}");
        }
        return result.OrderBy(r => r.GameName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 下载到 <see cref="TrainerDir"/>，若是 zip 就地解压；返回可执行的 exe 路径（失败/取消返回 null）。
    /// 先写 <c>.part</c>、成功再落最终名：取消或断网都不会留下半截文件。
    /// </summary>
    public async Task<string?> DownloadAsync(string url, string fileName, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(TrainerDir);
        var bare = Path.GetFileNameWithoutExtension(fileName);      // 附件标题没有扩展名，先按无扩展名处理
        var temp = Path.Combine(TrainerDir, bare + ".part");

        try
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) })
            using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;

                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
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
            var final = Path.Combine(TrainerDir, bare + (IsZip(temp) ? ".zip" : ".exe"));
            File.Move(temp, final, overwrite: true);
            LogService.AddAppLog($"trainer 下载完成 {Path.GetFileName(final)}（{new FileInfo(final).Length / 1024} KB，SHA256 {Sha256(final)[..16]}…）");

            if (!IsZip(final)) return final;

            var exe = Extract(final);
            return exe;
        }
        catch (OperationCanceledException)
        {
            LogService.AddAppLog($"trainer 下载已取消 {bare}");
            return null;
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 下载失败 {bare}: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>解压到与压缩包同名的目录，返回里面第一个 exe（没有 exe 就返回 null）</summary>
    private static string? Extract(string zipPath)
    {
        var dir = Path.Combine(TrainerDir, Path.GetFileNameWithoutExtension(zipPath));
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

    /// <summary>目录下的 exe（最多两层，跳过 .part/.tmp）</summary>
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
