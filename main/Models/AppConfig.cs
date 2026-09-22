namespace OSTGUI.Models;

/// <summary>
/// 应用全局配置模型
/// 后期可扩展添加新配置项
/// </summary>
public class AppConfig
{
    // === 基本设置 ===
    // Steam 路径：只有用户在设置页明确指定过才写进来；留空/自动检测的结果不落盘。
    // （曾经把检测结果也写进来，steam 一挪位置就变成读到失效路径，检测形同虚设。）
    public string SteamPath { get; set; } = string.Empty;
    public string ManifestHubApiKey { get; set; } = string.Empty;
    public bool ShowSystemNotifications { get; set; } = true;
    public bool ShowVersionChangeNotifications { get; set; } = true;
    public string DefaultManifestSource { get; set; } = "auto";

    // === 入库设置 ===
    public bool DefaultAddAllDlc { get; set; } = true;
    // 隐藏调优参数：无 UI，仅手改 config.json 生效。消费点：
    //   ManifestDownloadService 清单文件下载 max(60, 此值)
    //   SudamaKeyCache 缓存下载 max(120, 此值)
    public int DownloadTimeout { get; set; } = 120;
    public bool StFixedVersionDefault { get; set; } = true;
    public bool StDownloadManifestDefault { get; set; } = true;

    // === 免Steam运行设置 ===
    public bool DefaultBackupOriginalExe { get; set; } = true;
    public bool SkipSteamlessDefault { get; set; }
    public bool SkipGBEDefault { get; set; }
    public bool DryRunDefault { get; set; }
    public int SteamlessTimeoutMinutesDefault { get; set; } = 5;

    // === 免Steam高级配置 (GBE steam_settings) ===
    public string AdvancedAccountName { get; set; } = string.Empty;
    public string AdvancedSteamId { get; set; } = string.Empty;
    public string AdvancedLanguage { get; set; } = "schinese";
    public bool AdvancedUnlockAllDlc { get; set; } = true;
    public string AdvancedDlcList { get; set; } = string.Empty;
    public bool AdvancedOfflineMode { get; set; }
    public bool AdvancedDisableNetworking { get; set; }
    public bool AdvancedSteamApiCheckBypass { get; set; }

    // === 外观设置 ===
    public string ThemeMode { get; set; } = "auto"; // light, dark, auto
    // 窗口显示效果：none（纯色）/ mica（云母）/ acrylic（亚克力）
    public string BackdropMode { get; set; } = "acrylic";

    // === 窗口状态记忆 ===
    public double WindowWidth { get; set; } = 1250;
    public double WindowHeight { get; set; } = 875;
    public double WindowX { get; set; } = -1; // -1 表示居中
    public double WindowY { get; set; } = -1;
    public bool IsWindowMaximized { get; set; }

    // === 界面状态记忆 ===
    public string DefaultPage { get; set; } = "home";
    public bool IsNavigationPaneOpen { get; set; } = true;
    public double NavigationPaneWidth { get; set; } = 200;
    // 联机页「其他」下拉的选中项：0 = DLL 注入（推荐），1 = AppID Changer（轻量）
    public int OnlineOtherMode { get; set; }
    // 入库管理视图形态：list = 列表卡片（默认），grid = 网格卡片
    public string LibraryViewMode { get; set; } = "list";

    // === 应用程序设置 ===
    public int LogMaxLines { get; set; } = 1000;

    // === 自定义清单源 ===
    public Dictionary<string, bool> ManifestSourceEnabled { get; set; } = new();

    // === 完整清单源配置（内置 + 自定义，通用格式） ===
    public List<ManifestSource> ManifestSources { get; set; } = new();

    // === 扩展预留 ===
    public Dictionary<string, object> Extensions { get; set; } = new();

    /// <summary>
    /// 获取默极配置
    /// </summary>
    public static AppConfig GetDefault() => new()
    {
        SteamPath = string.Empty,
        DefaultManifestSource = "auto",
        DefaultAddAllDlc = true,
        DownloadTimeout = 120,
        StFixedVersionDefault = true,
        StDownloadManifestDefault = true,
        ThemeMode = "auto",
        BackdropMode = "acrylic",
        ManifestHubApiKey = "",
        WindowWidth = 1250,
        WindowHeight = 875,
        WindowX = -1,
        WindowY = -1,
        IsWindowMaximized = false,
        DefaultPage = "home",
        IsNavigationPaneOpen = true,
        NavigationPaneWidth = 200,
        OnlineOtherMode = 0,
        LibraryViewMode = "list",
        LogMaxLines = 1000,
        ManifestSourceEnabled = ManifestSource.GetPresetSources()
            .ToDictionary(s => s.Id, s => s.IsEnabled),
        ManifestSources = ManifestSource.GetPresetSources(),
        Extensions = new(),
        // NoSteam options
        DefaultBackupOriginalExe = true,
        SkipSteamlessDefault = false,
        SkipGBEDefault = false,
        DryRunDefault = false,
        SteamlessTimeoutMinutesDefault = 5,
        // Advanced GBE config
        AdvancedAccountName = string.Empty,
        AdvancedSteamId = string.Empty,
        AdvancedLanguage = "schinese",
        AdvancedUnlockAllDlc = true,
        AdvancedDlcList = string.Empty,
        AdvancedOfflineMode = false,
        AdvancedDisableNetworking = false,
        AdvancedSteamApiCheckBypass = false
    };
}