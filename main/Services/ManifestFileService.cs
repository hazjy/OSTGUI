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
        LogService.Event(message);
        System.Diagnostics.Debug.WriteLine($"[ManifestFile] {message}");
    }

    /// <summary>
    /// 复制 manifest 文件到 depot 缓存目录（config/depotcache 与 depotcache 双份）。
    ///
    /// 两条纪律（2026-09-22 加，配合"取消入库"）：
    /// ① 每份清单**之间**查一次 <paramref name="ct"/> —— 取消时立即停，不必等整轮拷完；
    /// ② 每份清单走"临时文件 + <c>File.Move</c>"**原子落地** —— 原实现直接 `File.Copy` 到目标名，
    ///    任何中断（用户取消 / 进程被杀 / 磁盘满）都会在 depotcache 留下**半份 manifest**，
    ///    而半份清单会让内核读到错的 file list，比"没拷"危险得多。
    /// </summary>
    public int CopyToDepotCache(List<string> manifestFiles, CancellationToken ct = default)
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
            ct.ThrowIfCancellationRequested();   // 取消点：逐份之间（正在拷的那一份会拷贝完，毫秒级）

            var fileName = Path.GetFileName(manifestFile);
            var copiedAny = false;
            foreach (var depotcache in depotcachePaths)
            {
                if (string.IsNullOrEmpty(depotcache)) continue;
                var destPath = Path.Combine(depotcache, fileName);
                var tmpPath = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(manifestFile, tmpPath, true);
                    File.Move(tmpPath, destPath, true);   // 原子替换：要么是旧份，要么是完整新份
                    copiedAny = true;
                }
                catch (Exception ex)
                {
                    // [本地修补 2026-09-09] 逐份拷贝独立容错：一个目录失败（如 Steam 占用
                    // depotcache 根）不再中断整轮，避免"config 有、根没有"的静默半成品。
                    failed.Add($"{fileName} -> {depotcache} ({ex.Message})");
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
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
