using System.IO.Compression;

namespace OSTGUI.Services;

/// <summary>
/// 清单文件服务 - 分支 zip 下载/解压、复制到 depotcache、从文件名解析 depot 信息
/// </summary>
public class ManifestFileService
{
    private readonly SteamService _steamService;

    private const string GithubRepo = "SteamAutoCracks/ManifestHub";

    public ManifestFileService(SteamService steamService)
    {
        _steamService = steamService;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[ManifestFile] {message}");
    }

    /// <summary>
    /// 从 GitHub 分支 zip 下载 manifest（直连失败自动走镜像），返回 manifest 文件路径列表
    /// </summary>
    public async Task<List<string>> DownloadFromGithubZipAsync(string appId, string extractPath)
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
    /// 复制 manifest 文件到 depot 缓存目录（config/depotcache 与 depotcache 双份）
    /// </summary>
    public int CopyToDepotCache(List<string> manifestFiles)
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
    public static List<(string depotId, string manifestGid, long manifestSize)> ParseDepotsFromFiles(List<string> manifestFiles)
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
    /// 删除临时目录（失败忽略，不影响入库结果）
    /// </summary>
    public static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }
    }
}
