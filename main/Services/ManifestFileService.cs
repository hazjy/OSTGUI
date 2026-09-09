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
        var failed = new List<string>();
        foreach (var manifestFile in manifestFiles)
        {
            var fileName = Path.GetFileName(manifestFile);
            var copiedAny = false;
            foreach (var depotcache in depotcachePaths)
            {
                if (string.IsNullOrEmpty(depotcache)) continue;
                try
                {
                    File.Copy(manifestFile, Path.Combine(depotcache, fileName), true);
                    copiedAny = true;
                }
                catch (Exception ex)
                {
                    // [本地修补 2026-09-09] 逐份拷贝独立容错：一个目录失败（如 Steam 占用
                    // depotcache 根）不再中断整轮，避免"config 有、根没有"的静默半成品。
                    failed.Add($"{fileName} -> {depotcache} ({ex.Message})");
                }
            }
            if (copiedAny) count++;
        }
        if (failed.Count > 0)
            Log($"警告: 以下清单拷贝未全部成功（可重试入库补拷）: {string.Join("; ", failed)}");
        return count;
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
