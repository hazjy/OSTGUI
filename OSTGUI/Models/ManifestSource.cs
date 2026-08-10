namespace OSTGUI.Models;

/// <summary>
/// 清单源配置模型
/// BaseUrl 为 URL 模板，支持以下占位符：
///   {appid}     游戏 AppID
///   {key}       该源配置的 API Key
///   {token}     同上（兼容别名）
///   {depotid}   Depot ID（下载清单时替换）
///   {manifestid} Manifest GID（下载清单时替换）
/// </summary>
public class ManifestSource
{
    public const string PlaceholderAppId = "{appid}";
    public const string PlaceholderKey = "{key}";
    public const string PlaceholderToken = "{token}";
    public const string PlaceholderDepotId = "{depotid}";
    public const string PlaceholderManifestId = "{manifestid}";

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public ManifestSourceType Type { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool RequiresToken { get; set; }

    /// <summary>
    /// 该源独立的 API Key / Token（在界面中配置）
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public int Priority { get; set; } = 100;

    /// <summary>
    /// 是否为用户自定义源
    /// </summary>
    public bool IsCustom => Type == ManifestSourceType.Custom;

    /// <summary>
    /// 获取该源 API Key / Token 的页面地址（无则返回 null）
    /// </summary>
    public static string? GetTokenPageUrl(string sourceId) => sourceId switch
    {
        "mhub" => "https://manifesthub2.filegear-sg.me/",
        "github_auiowu" => "https://github.com/settings/tokens",
        _ => null
    };

    /// <summary>
    /// 该源是否已接入入库逻辑（未接入的源仅在设置页隐藏，仍保留在配置与文档中）
    /// </summary>
    public static bool IsImplementedSource(string sourceId) => sourceId switch
    {
        "mhub" or "sudama" or "github_auiowu" => true,
        _ => false
    };

    /// <summary>
    /// 替换 URL 模板中的占位符
    /// </summary>
    public string BuildUrl(string? appId = null, string? depotId = null, string? manifestId = null)
    {
        var url = BaseUrl;
        if (!string.IsNullOrEmpty(appId))
            url = url.Replace(PlaceholderAppId, appId);
        if (!string.IsNullOrEmpty(depotId))
            url = url.Replace(PlaceholderDepotId, depotId);
        if (!string.IsNullOrEmpty(manifestId))
            url = url.Replace(PlaceholderManifestId, manifestId);
        url = url.Replace(PlaceholderKey, ApiKey)
                   .Replace(PlaceholderToken, ApiKey);
        return url;
    }

    /// <summary>
    /// 预置清单源列表（兼容流畅入库）
    /// 只有适合 URL 模板的源提供默认 BaseUrl，其余由专用逻辑处理
    /// </summary>
    public static List<ManifestSource> GetPresetSources() => new()
    {
        new() { Id = "sac", Name = "SAC 分流", Description = "SAC 清单分流源", BaseUrl = "", Type = ManifestSourceType.SAC, Priority = 1 },
        new() { Id = "walftech", Name = "Walftech", Description = "Walftech 清单源", BaseUrl = "", Type = ManifestSourceType.Walftech, Priority = 2 },
        new()
        {
            Id = "mhub", Name = "MHub", Description = "MHub 清单源",
            BaseUrl = "https://api.manifesthub2.filegear-sg.me/manifest?apikey={key}&depotid={depotid}&manifestid={manifestid}",
            Type = ManifestSourceType.MHub, RequiresToken = true, Priority = 3
        },
        new() { Id = "steamautocracks_v2", Name = "SteamAutoCracks V2", Description = "仅提供密钥", BaseUrl = "", Type = ManifestSourceType.KeyOnly, Priority = 4 },
        new()
        {
            Id = "sudama", Name = "Sudama 库", Description = "仅提供密钥",
            BaseUrl = "https://api.993499094.xyz/depotkeys.json",
            Type = ManifestSourceType.KeyOnly, Priority = 5
        },
        new() { Id = "buqiuren", Name = "清单不求人", Description = "仅提供清单", BaseUrl = "", Type = ManifestSourceType.ManifestOnly, Priority = 6 },
        new() { Id = "github_auiowu", Name = "GitHub (Auiowu)", Description = "GitHub 仓库清单（专用逻辑）", BaseUrl = "", Type = ManifestSourceType.GitHub, RequiresToken = true, Priority = 7 },
        new() { Id = "auto_github", Name = "自动搜索 GitHub", Description = "自动在 GitHub 上搜索清单（专用逻辑）", BaseUrl = "", Type = ManifestSourceType.GitHubSearch, Priority = 8 },
    };
}

public enum ManifestSourceType
{
    SAC,
    Walftech,
    MHub,
    KeyOnly,
    ManifestOnly,
    GitHub,
    GitHubSearch,
    OpenSteamTool,
    Custom
}
