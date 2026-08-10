using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 入库协调服务（门面）- 组合清单下载、游戏信息、Lua 生成与修复服务
/// </summary>
public class ManifestService
{
    private readonly ConfigService _configService;
    private readonly ManifestDownloadService _downloadService;
    private readonly SteamGameInfoService _gameInfoService;
    private readonly ManifestRepairService _repairService;

    public ManifestService(
        ConfigService configService,
        ManifestDownloadService downloadService,
        SteamGameInfoService gameInfoService,
        ManifestRepairService repairService)
    {
        _configService = configService;
        _downloadService = downloadService;
        _gameInfoService = gameInfoService;
        _repairService = repairService;
    }

    /// <summary>
    /// 从 GitHub 下载清单并生成 Lua 配置
    /// </summary>
    public Task<(bool success, string message)> DownloadFromGithubAsync(
        string appId, bool fixedVersion, bool addAllDlc, bool patchDepotKey, IProgress<string>? progress = null)
        => _downloadService.DownloadFromGithubAsync(appId, fixedVersion, addAllDlc, patchDepotKey, progress);

    /// <summary>
    /// 从 ManifestHub 下载清单并生成 Lua 配置
    /// </summary>
    public Task<(bool success, string message)> DownloadFromManifestHubAsync(
        string appId, bool fixedVersion, bool addAllDlc = false, IProgress<string>? progress = null)
        => _downloadService.DownloadFromManifestHubAsync(appId, fixedVersion, addAllDlc, progress);

    /// <summary>
    /// 从 Sudama 获取密钥并兜底下载清单、生成 Lua
    /// </summary>
    public Task<(bool success, string message)> DownloadFromSudamaAsync(
        string appId, bool fixedVersion, bool addAllDlc = false, IProgress<string>? progress = null)
        => _downloadService.DownloadFromSudamaAsync(appId, fixedVersion, addAllDlc, progress);

    /// <summary>
    /// 获取游戏详情（含 depot 和 manifest gid）
    /// </summary>
    public Task<GameInfo?> GetGameDetailsFromSteamAsync(string appId)
        => _gameInfoService.GetGameDetailsFromSteamAsync(appId);

    /// <summary>
    /// 获取可用的清单源列表
    /// </summary>
    public List<ManifestSource> GetAvailableSources()
    {
        var sources = ManifestSource.GetPresetSources();
        var config = _configService.Config;

        foreach (var source in sources)
        {
            if (config.ManifestSourceEnabled.TryGetValue(source.Id, out var enabled))
                source.IsEnabled = enabled;
        }

        return sources;
    }

    /// <summary>
    /// 自动修复清单（拉取缺失清单）
    /// </summary>
    public Task<(bool success, string message)> RepairManifestAsync(string appId)
        => _repairService.RepairManifestAsync(appId);

    /// <summary>
    /// 修复 Lua 错误（重新生成 Lua 配置）
    /// </summary>
    public Task<(bool success, string message)> RepairLuaAsync(string appId, bool fixedVersion)
        => _repairService.RepairLuaAsync(appId, fixedVersion);

    /// <summary>
    /// 补齐版本配置（检测并修复固定版本方面的错误）
    /// </summary>
    public Task<(bool success, string message)> RepairVersionConfigAsync(string appId)
        => _repairService.RepairVersionConfigAsync(appId);
}
