namespace OSTGUI.Models;

/// <summary>
/// 应用全局配置模型
/// 后期可扩展添加新配置项
/// </summary>
public class AppConfig
{
    // === 基本设置 ===
    public string SteamPath { get; set; } = string.Empty; // 留空则自动检测
    public string GithubToken { get; set; } = string.Empty;
    public string ManifestHubApiKey { get; set; } = string.Empty;
    public bool ShowSystemNotifications { get; set; } = true;
    public bool ShowVersionChangeNotifications { get; set; } = true;
    public string DefaultManifestSource { get; set; } = "auto";
    public string UnlockerType { get; set; } = "ost"; // ost, steamtools, greenluma

    // === 入库设置 ===
    public bool DefaultAddAllDlc { get; set; } = true;
    public bool DefaultPatchDepotKey { get; set; } = true;
    public bool DefaultPatchManifest { get; set; } = true;
    public int DownloadTimeout { get; set; } = 120;
    public bool StFixedVersionDefault { get; set; } = true;
    public string StFixedManifestMode { get; set; } = "ask"; // always, never, ask

    // === 外观设置 ===
    public string ThemeMode { get; set; } = "auto"; // light, dark, auto
    public string ThemeColor { get; set; } = "#0078d4";
    public string WindowEffect { get; set; } = "mica"; // none, mica, acrylic
    public string Language { get; set; } = "zh_CN";

    // === 窗口状态记忆 ===
    public double WindowWidth { get; set; } = 1250;
    public double WindowHeight { get; set; } = 875;
    public double WindowX { get; set; } = -1; // -1 表示居中
    public double WindowY { get; set; } = -1;
    public bool IsWindowMaximized { get; set; }

    // === 界面状态记忆 ===
    public string LibraryViewMode { get; set; } = "list"; // list, grid
    public string DefaultPage { get; set; } = "home";
    public bool IsNavigationPaneOpen { get; set; } = true;
    public double NavigationPaneWidth { get; set; } = 360;

    // === 应用程序设置 ===
    public bool DebugMode { get; set; }
    public int LogMaxLines { get; set; } = 1000;
    public bool CheckUpdateOnStart { get; set; } = true;

    // === 自定义清单源 ===
    public List<string> CustomGithubRepos { get; set; } = new();
    public List<string> CustomZipUrls { get; set; } = new();
    public List<ManifestSource> CustomManifestSources { get; set; } = new();
    public Dictionary<string, bool> ManifestSourceEnabled { get; set; } = new();

    // === 完整清单源配置（内置 + 自定义，通用格式） ===
    public List<ManifestSource> ManifestSources { get; set; } = new();

    // === 扩展预留 ===
    public Dictionary<string, object> Extensions { get; set; } = new();

    /// <summary>
    /// 获取默认配置
    /// </summary>
    public static AppConfig GetDefault() => new()
    {
        SteamPath = string.Empty,
        GithubToken = string.Empty,
        DefaultManifestSource = "auto",
        UnlockerType = "ost",
        DefaultAddAllDlc = true,
        DefaultPatchDepotKey = true,
        DefaultPatchManifest = true,
        DownloadTimeout = 120,
        StFixedVersionDefault = true,
        StFixedManifestMode = "ask",
        ThemeMode = "auto",
        ThemeColor = "#0078d4",
        WindowEffect = "mica",
        Language = "zh_CN",
        ManifestHubApiKey = "",
        WindowWidth = 1250,
        WindowHeight = 875,
        WindowX = -1,
        WindowY = -1,
        IsWindowMaximized = false,
        LibraryViewMode = "list",
        DefaultPage = "home",
        IsNavigationPaneOpen = true,
        NavigationPaneWidth = 360,
        DebugMode = false,
        LogMaxLines = 1000,
        CheckUpdateOnStart = true,
        ManifestSourceEnabled = ManifestSource.GetPresetSources()
            .ToDictionary(s => s.Id, s => s.IsEnabled),
        ManifestSources = ManifestSource.GetPresetSources(),
        CustomGithubRepos = new(),
        CustomZipUrls = new(),
        CustomManifestSources = new(),
        Extensions = new()
    };
}
