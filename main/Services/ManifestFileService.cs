using System.IO.Compression;

namespace OSTGUI.Services;

/// <summary>
/// 清单文件服务 - 复制到 depotcache、从文件名解析 depot 信息
/// </summary>
public class ManifestFileService
{
    private readonly SteamService _steamService;

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
