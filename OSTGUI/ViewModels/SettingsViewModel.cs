using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using OSTGUI.Models;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// 设置页 ViewModel
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly SteamService _steamService;
    private readonly SteamDllService _steamDllService;
    private bool _isLoading;

    // === 基本设置 ===
    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private bool _showSystemNotifications = true;
    [ObservableProperty] private bool _showVersionChangeNotifications = true;
    [ObservableProperty] private string _defaultSource = "auto";
    [ObservableProperty] private string _unlockerType = "ost";

    // === 入库设置 ===
    [ObservableProperty] private bool _defaultAddAllDlc = true;
    [ObservableProperty] private bool _defaultPatchDepotKey = true;
    [ObservableProperty] private bool _defaultPatchManifest = true;
    [ObservableProperty] private int _dlcTimeout = 60;
    [ObservableProperty] private int _downloadTimeout = 120;
    [ObservableProperty] private bool _stFixedVersionDefault = true;
    [ObservableProperty] private string _stFixedManifestMode = "ask";

    // === 外观设置 ===
    [ObservableProperty] private string _themeMode = "auto";
    [ObservableProperty] private string _themeColor = "#0078d4";
    [ObservableProperty] private string _windowEffect = "mica";
    [ObservableProperty] private string _language = "zh_CN";

    // === 清单源设置 ===
    public ObservableCollection<ManifestSource> Sources { get; } = new();
    // 设置页只显示已接入的有效源，Sources 保留全部用于持久化
    public ObservableCollection<ManifestSource> VisibleSources { get; } = new();

    public bool IsLightTheme
    {
        get => ThemeMode == "light";
        set { if (value) SetTheme("light"); }
    }

    public bool IsDarkTheme
    {
        get => ThemeMode == "dark";
        set { if (value) SetTheme("dark"); }
    }

    public bool IsAutoTheme
    {
        get => ThemeMode == "auto";
        set { if (value) SetTheme("auto"); }
    }

    public event EventHandler? ThemeChanged;

    private void SetTheme(string mode)
    {
        if (ThemeMode == mode) return;
        ThemeMode = mode;
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsAutoTheme));
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyTheme(FrameworkElement root)
    {
        root.RequestedTheme = ThemeMode switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default
        };
    }

    [ObservableProperty] private bool _debugMode;
    [ObservableProperty] private bool _saveLogFiles = true;
    [ObservableProperty] private bool _checkUpdateOnStart = true;

    // === OST DLL 状态 ===
    [ObservableProperty] private bool _isOstInjected;
    [ObservableProperty] private string _ostStatusText = "检查中...";
    [ObservableProperty] private string _ostStatusType = "Info";
    [ObservableProperty] private string _ostSourceDir = "";
    [ObservableProperty] private bool _isOstOperating;
    [ObservableProperty] private bool _isSteamRunning;

    // === 状态 ===
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _statusType = "Info";

    public SettingsViewModel(ConfigService configService, SteamService steamService, SteamDllService steamDllService)
    {
        _configService = configService;
        _steamService = steamService;
        _steamDllService = steamDllService;

        // 设置变化即自动保存（实时生效）；加载期间由 _isLoading 抑制
        PropertyChanged += (s, e) => SaveAllToConfig();
    }

    /// <summary>
    /// 从配置加载设置
    /// </summary>
    public void LoadFromConfig()
    {
        _isLoading = true;
        try
        {
            var c = _configService.Config;
            SteamPath = c.SteamPath;
            DefaultSource = c.DefaultManifestSource;
            UnlockerType = c.UnlockerType;
            DefaultAddAllDlc = c.DefaultAddAllDlc;
            DefaultPatchDepotKey = c.DefaultPatchDepotKey;
            DefaultPatchManifest = c.DefaultPatchManifest;
            DlcTimeout = c.DlcTimeout;
            DownloadTimeout = c.DownloadTimeout;
            StFixedVersionDefault = c.StFixedVersionDefault;
            StFixedManifestMode = c.StFixedManifestMode;
            ThemeMode = c.ThemeMode;
            ThemeColor = c.ThemeColor;
            WindowEffect = c.WindowEffect;
            Language = c.Language;
            ShowSystemNotifications = c.ShowSystemNotifications;
            ShowVersionChangeNotifications = c.ShowVersionChangeNotifications;
            DebugMode = c.DebugMode;
            SaveLogFiles = c.SaveLogFiles;
            CheckUpdateOnStart = c.CheckUpdateOnStart;

            LoadSourcesFromConfig(c);

            RefreshOstStatus();
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// 加载清单源配置；旧版全局字段（GitHub Token / MHub Key）一次性迁移到对应源
    /// </summary>
    private void LoadSourcesFromConfig(AppConfig c)
    {
        var sources = c.ManifestSources;
        if (sources != null)
        {
            // 清理已移除的内置源与自定义源（自定义源功能已下线）
            if (sources.RemoveAll(s => s.Id == "opensteamtool" || s.IsCustom) > 0)
                _ = _configService.SaveAsync();
        }

        if (sources == null || sources.Count == 0)
        {
            sources = ManifestSource.GetPresetSources();

            // 迁移旧全局 GitHub Token 到 GitHub 源
            if (!string.IsNullOrEmpty(c.GithubToken))
            {
                var gh = sources.FirstOrDefault(s => s.Id == "github_auiowu");
                if (gh != null && string.IsNullOrEmpty(gh.ApiKey))
                    gh.ApiKey = c.GithubToken;
            }

            // 迁移旧全局 ManifestHub Key 到 MHub 源
            if (!string.IsNullOrEmpty(c.ManifestHubApiKey))
            {
                var mh = sources.FirstOrDefault(s => s.Id == "mhub");
                if (mh != null && string.IsNullOrEmpty(mh.ApiKey))
                    mh.ApiKey = c.ManifestHubApiKey;
            }

            // 启用状态
            foreach (var s in sources)
            {
                if (c.ManifestSourceEnabled.TryGetValue(s.Id, out var enabled))
                    s.IsEnabled = enabled;
            }

            c.ManifestSources = sources;
            _ = _configService.SaveAsync();
        }
        else
        {
            // 配置已存在：合并预置源，防止旧配置缺少新增的内置源
            var merged = false;
            foreach (var preset in ManifestSource.GetPresetSources())
            {
                if (!sources.Any(s => s.Id == preset.Id))
                {
                    sources.Add(preset);
                    merged = true;
                }
            }
            if (merged)
                _ = _configService.SaveAsync();
        }

        Sources.Clear();
        VisibleSources.Clear();
        foreach (var s in sources)
        {
            Sources.Add(s);
            if (ManifestSource.IsImplementedSource(s.Id))
                VisibleSources.Add(s);
        }
    }

    /// <summary>
    /// 自动检测 Steam 路径
    /// </summary>
    [RelayCommand]
    private void DetectSteamPath()
    {
        var path = _steamService.DetectSteamPath();
        if (!string.IsNullOrEmpty(path))
        {
            SteamPath = path;
            SetStatus($"已检测到 Steam 路径: {path}", "Success");
        }
        else
        {
            SetStatus("未能自动检测到 Steam，请手动指定路径", "Warning");
        }
    }

    /// <summary>
    /// 手动选择 Steam 目录
    /// </summary>
    [RelayCommand]
    private async Task BrowseSteamPathAsync()
    {
        // WinUI3 FolderPicker 需要在 UI 线程上调用
        // 这里由 View code-behind 处理，此处提供占位
        await Task.CompletedTask;
    }

    /// <summary>
    /// 保存所有设置
    /// </summary>
    public void SaveAllToConfig()
    {
        // 加载中或配置未就绪时不写盘
        if (_isLoading || !_configService.IsLoaded)
            return;

        try
        {
            _configService.UpdateAndSaveAsync(c =>
            {
                c.SteamPath = SteamPath;
                c.DefaultManifestSource = DefaultSource;
                c.UnlockerType = UnlockerType;
                c.DefaultAddAllDlc = DefaultAddAllDlc;
                c.DefaultPatchDepotKey = DefaultPatchDepotKey;
                c.DefaultPatchManifest = DefaultPatchManifest;
                c.DlcTimeout = DlcTimeout;
                c.DownloadTimeout = DownloadTimeout;
                c.StFixedVersionDefault = StFixedVersionDefault;
                c.StFixedManifestMode = StFixedManifestMode;
                c.ThemeMode = ThemeMode;
                c.ThemeColor = ThemeColor;
                c.WindowEffect = WindowEffect;
                c.Language = Language;
                c.ShowSystemNotifications = ShowSystemNotifications;
                c.ShowVersionChangeNotifications = ShowVersionChangeNotifications;
                c.DebugMode = DebugMode;
                c.SaveLogFiles = SaveLogFiles;
                c.CheckUpdateOnStart = CheckUpdateOnStart;

                // 保存完整源配置（内置 + 自定义，含 URL 模板与每源 Key）
                c.ManifestSources = Sources.ToList();
                foreach (var source in Sources)
                    c.ManifestSourceEnabled[source.Id] = source.IsEnabled;
            }).GetAwaiter().GetResult();
        }
        catch { }
    }

    /// <summary>
    /// 保存设置（兼容旧入口，行为与自动保存一致）
    /// </summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        SaveAllToConfig();
        try
        {
            // 更新 Steam 路径
            if (!string.IsNullOrEmpty(SteamPath))
                _steamService.SetSteamPath(SteamPath);
            else
                _steamService.DetectSteamPath();

            RefreshOstStatus();
            SetStatus("设置已保存", "Success");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败: {ex.Message}", "Error");
        }
    }

    /// <summary>
    /// 重置为默认设置
    /// </summary>
    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        await _configService.ResetAsync();
        LoadFromConfig();
        SetStatus("设置已重置为默认值", "Success");
    }

    // === OST DLL 操作 ===

    /// <summary>
    /// 刷新 OST DLL 状态
    /// </summary>
    [RelayCommand]
    private void RefreshOstStatus()
    {
        IsSteamRunning = _steamService.IsSteamRunning();
        IsOstInjected = _steamDllService.IsOSTDllInjected();
        OstStatusText = IsOstInjected ? "已注入" : "未注入";
        OstStatusType = IsOstInjected ? "Success" : "Warning";
    }

    /// <summary>
    /// 手动选择 OST DLL 源目录
    /// </summary>
    [RelayCommand]
    private async Task BrowseOstSourceAsync()
    {
        await Task.CompletedTask; // View code-behind 处理
    }

    /// <summary>
    /// 注入 OST DLL
    /// </summary>
    [RelayCommand]
    private async Task InjectOstDllAsync()
    {
        IsOstOperating = true;
        SetStatus("正在注入 OST DLL...", "Info");

        try
        {
            if (string.IsNullOrEmpty(OstSourceDir))
            {
                SetStatus("请先选择 OST DLL 所在的源目录", "Warning");
                return;
            }

            if (IsSteamRunning)
            {
                SetStatus("Steam 正在运行，请先关闭 Steam 再注入 DLL", "Warning");
                return;
            }

            var (success, message) = await _steamDllService.InjectOstDllAsync(OstSourceDir);
            RefreshOstStatus();
            SetStatus(message, success ? "Success" : "Error");
        }
        catch (Exception ex)
        {
            SetStatus($"注入失败: {ex.Message}", "Error");
        }
        finally
        {
            IsOstOperating = false;
        }
    }

    /// <summary>
    /// 卸载 OST DLL
    /// </summary>
    [RelayCommand]
    private async Task UnloadOstDllAsync()
    {
        IsOstOperating = true;
        SetStatus("正在卸载 OST DLL...", "Info");

        try
        {
            if (IsSteamRunning)
            {
                SetStatus("Steam 正在运行，请先关闭 Steam 再卸载 DLL", "Warning");
                return;
            }

            var (success, message) = await _steamDllService.UnloadOstDllAsync();
            RefreshOstStatus();
            SetStatus(message, success ? "Success" : "Error");
        }
        catch (Exception ex)
        {
            SetStatus($"卸载失败: {ex.Message}", "Error");
        }
        finally
        {
            IsOstOperating = false;
        }
    }

    /// <summary>
    /// 重启 Steam
    /// </summary>
    [RelayCommand]
    private async Task RestartSteamAsync()
    {
        SetStatus("正在重启 Steam...", "Info");
        var (success, message) = await _steamService.RestartSteamAsync();
        SetStatus(message, success ? "Success" : "Error");
    }

    private void SetStatus(string message, string type)
    {
        StatusMessage = message;
        StatusType = type;
    }
}
