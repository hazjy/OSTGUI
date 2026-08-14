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

    public ManifestService(
        ConfigService configService,
        ManifestDownloadService downloadService,
        SteamGameInfoService gameInfoService)
    {
        _configService = configService;
        _downloadService = downloadService;
        _gameInfoService = gameInfoService;
    }

    /// <summary>
    /// 从 GitHub 下载清单并生成 Lua 配置
    /// </summary>
    public Task<(bool success, string message, List<string> missingKeys)> DownloadFromGithubAsync(
        string appId, bool fixedVersion, bool addAllDlc, IProgress<string>? progress)
        => _downloadService.DownloadFromGithubAsync(appId, fixedVersion, addAllDlc, progress);

    /// <summary>
    /// 从 ManifestHub 下载清单并生成 Lua 配置
    /// </summary>
    public Task<(bool success, string message, List<string> missingKeys)> DownloadFromManifestHubAsync(
        string appId, bool fixedVersion, bool addAllDlc, IProgress<string>? progress)
        => _downloadService.DownloadFromManifestHubAsync(appId, fixedVersion, addAllDlc, progress);

    /// <summary>
    /// 从 Sudama 获取密钥并生成 Lua（不下载清单文件，仅作密钥源兜底）
    /// </summary>
    public Task<(bool success, string message, List<string> missingKeys)> DownloadFromSudamaAsync(
        string appId, bool fixedVersion, bool addAllDlc, IProgress<string>? progress)
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
}
