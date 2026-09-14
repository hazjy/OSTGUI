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
    private readonly SudamaKeyCache _sudamaCache;
    private bool _isLoading;

    // === 基本设置 ===
    [ObservableProperty] private string _steamPath = "";
    /// <summary>GUI 写入 lua 的目录；改它会同步进内核配置，内核与 GUI 始终共用一个目录</summary>
    [ObservableProperty] private string _luaPath = "";
    [ObservableProperty] private bool _showSystemNotifications = true;
    [ObservableProperty] private bool _showVersionChangeNotifications = true;
    [ObservableProperty] private string _defaultSource = "auto";

    // === 入库设置 ===
    [ObservableProperty] private bool _defaultAddAllDlc = true;
    [ObservableProperty] private bool _stFixedVersionDefault = true;

    // === 外观设置 ===
    [ObservableProperty] private string _themeMode = "auto";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyNavigationPaneWidth))]
    private string _navigationPaneWidthInput = "200";

    /// <summary>输入为 150–600 内整数且与当前已存值不同才可应用（按钮激活条件）</summary>
    public bool CanApplyNavigationPaneWidth =>
        int.TryParse(NavigationPaneWidthInput.Trim(), out var v)
        && v is >= 150 and <= 600
        && v != (int)_configService.Config.NavigationPaneWidth;

    // === D 加密模式 ===
    // 唯一数据源是内核的 <Steam>\opensteamtool.toml（[denuvo] mode），不写进应用配置；
    // 内核热重载该文件，所以这里改完立即生效，无需重启 Steam。
    private string _denuvoMode = "normal";

    public bool IsDenuvoNormalMode
    {
        get => _denuvoMode == "normal";
        set { if (value) SetDenuvoMode("normal"); }
    }

    public bool IsDenuvoCompatMode
    {
        get => _denuvoMode == "compat";
        set { if (value) SetDenuvoMode("compat"); }
    }

    /// <summary>内核配置文件路径（界面展示，便于用户直接手改）</summary>
    [ObservableProperty] private string _denuvoConfigPath = "";

    /// <summary>切换 D 加密模式：写内核配置文件；失败则回读文件真实值，避免界面与文件不一致</summary>
    private void SetDenuvoMode(string mode)
    {
        if (_denuvoMode == mode) return;

        var (ok, message) = _steamDllService.SetDenuvoMode(mode);
        if (!ok)
        {
            SetStatus(message, "Error");
            ToastService.ShowError("D 加密模式切换失败", message);
            RefreshDenuvoModeFromKernel();
            return;
        }

        _denuvoMode = mode;
        OnPropertyChanged(nameof(IsDenuvoNormalMode));
        OnPropertyChanged(nameof(IsDenuvoCompatMode));
        LogService.AddLog(message);
        SetStatus(message, "Success");
        ToastService.ShowSuccess("D 加密模式已切换", message);
    }

    /// <summary>从内核配置文件读回当前模式（进入设置页时调用，可覆盖外部手改）</summary>
    public void RefreshDenuvoModeFromKernel()
    {
        _denuvoMode = _steamDllService.GetDenuvoMode();
        DenuvoConfigPath = _steamDllService.GetConfigPath() ?? "（未设置 Steam 路径，无法定位 opensteamtool.toml）";
        OnPropertyChanged(nameof(IsDenuvoNormalMode));
        OnPropertyChanged(nameof(IsDenuvoCompatMode));
    }

    // === 日志显示 ===
    [ObservableProperty] private string _logsText = "";

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

    [ObservableProperty] private double _logMaxLines = 1000;

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
    [ObservableProperty] private bool _isRefreshingSudama;

    public SettingsViewModel(ConfigService configService, SteamService steamService,
        SteamDllService steamDllService, SudamaKeyCache sudamaCache)
    {
        _configService = configService;
        _steamService = steamService;
        _steamDllService = steamDllService;
        _sudamaCache = sudamaCache;

        // 设置变化即自动保存（实时生效）；加载期间由 _isLoading 抑制
        PropertyChanged += (s, e) => SaveAllToConfig();
    }

    [RelayCommand]
    private void ApplyNavigationPaneWidth()
    {
        if (!CanApplyNavigationPaneWidth) return;
        if (int.TryParse(NavigationPaneWidthInput.Trim(), out var v) && v is >= 150 and <= 600)
            _configService.UpdateAndSaveAsync(c => c.NavigationPaneWidth = v).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 手动刷新 Sudama 缓存（密钥 + 令牌）
    /// </summary>
    [RelayCommand]
    private async Task RefreshSudamaCache()
    {
        if (IsRefreshingSudama) return;
        IsRefreshingSudama = true;
        try
        {
            var (ok, message) = await _sudamaCache.RefreshAsync();
            LogService.AddLog(message);
            SetStatus(message, ok ? "Success" : "Error");
            if (ok)
                Services.ToastService.ShowSuccess("Sudama 缓存已更新", message);
            else
                Services.ToastService.ShowError("Sudama 缓存刷新失败", message);
        }
        catch (Exception ex)
        {
            var msg = $"Sudama 缓存刷新异常: {ex.Message}";
            LogService.AddLog(msg);
            SetStatus(msg, "Error");
            Services.ToastService.ShowError("Sudama 缓存刷新失败", msg);
        }
        finally
        {
            IsRefreshingSudama = false;
            RefreshSudamaCacheAge();
        }
    }

    /// <summary>
    /// 手动导入本地下载的 Sudama 缓存文件（浏览器直连下载通常远快于应用内下载）
    /// </summary>
    public async Task ImportSudamaCacheAsync(IEnumerable<string> filePaths)
    {
        try
        {
            var (ok, message) = await _sudamaCache.ImportFilesAsync(filePaths);
            LogService.AddLog(message);
            SetStatus(message, ok ? "Success" : "Error");
            if (ok)
                Services.ToastService.ShowSuccess("Sudama 缓存导入完成", message);
            else
                Services.ToastService.ShowError("Sudama 缓存导入失败", message);
        }
        catch (Exception ex)
        {
            var msg = $"Sudama 缓存导入异常: {ex.Message}";
            LogService.AddLog(msg);
            SetStatus(msg, "Error");
            Services.ToastService.ShowError("Sudama 缓存导入失败", msg);
        }
        RefreshSudamaCacheAge();
    }

    /// <summary>
    /// 记录某需 token 源的 API Key 上次设置时间，并触发该行重绑定显示（仅变化才重建）
    /// </summary>
    public void MarkManifestKeyUpdated(ManifestSource source)
    {
        if (!source.RequiresToken) return;
        var text = "上次更新：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        if (source.ApiKeyUpdatedAtText == text) return;
        source.ApiKeyUpdatedAtText = text;
        var i = VisibleSources.IndexOf(source);
        if (i >= 0) VisibleSources[i] = VisibleSources[i];
    }

    /// <summary>
    /// 依据缓存文件修改时间刷新"上次刷新/导入时间"文案到 Sudama 源行（不引入额外存储状态）
    /// </summary>
    public void RefreshSudamaCacheAge()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSTGUI");
        var times = new[] { "sudama_cache.json", "token_cache.json" }
            .Select(f => Path.Combine(dir, f))
            .Where(File.Exists)
            .Select(File.GetLastWriteTimeUtc)
            .ToList();

        var text = times.Count == 0
            ? "上次更新：暂无缓存"
            : $"上次更新：{times.Max().ToLocalTime():yyyy-MM-dd HH:mm}";

        // 写进 Sudama 源行自身，集合元素替换触发该行重绑定
        for (var i = 0; i < VisibleSources.Count; i++)
        {
            if (VisibleSources[i].Id == "sudama")
            {
                VisibleSources[i].SudamaCacheAgeText = text;
                VisibleSources[i] = VisibleSources[i];
            }
        }
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
            RefreshPaths();
            DefaultSource = c.DefaultManifestSource;
            DefaultAddAllDlc = c.DefaultAddAllDlc;
            StFixedVersionDefault = c.StFixedVersionDefault;
            NavigationPaneWidthInput = ((int)c.NavigationPaneWidth).ToString();
            ThemeMode = c.ThemeMode;
            ShowSystemNotifications = c.ShowSystemNotifications;
            ShowVersionChangeNotifications = c.ShowVersionChangeNotifications;
            LogMaxLines = c.LogMaxLines;

            LoadSourcesFromConfig(c);

            RefreshOstStatus();
            RefreshDenuvoModeFromKernel();
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
                var existing = sources.FirstOrDefault(s => s.Id == preset.Id);
                if (existing == null)
                {
                    sources.Add(preset);
                    merged = true;
                }
                else if (ManifestSource.IsImplementedSource(preset.Id) &&
                         (existing.Name != preset.Name || existing.Description != preset.Description))
                {
                    // 内置源的显示名/说明以代码为准（旧配置里存的是历史文案，如"Sudama 库"）
                    existing.Name = preset.Name;
                    existing.Description = preset.Description;
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
    /// 回填两个路径输入框：Steam 路径取已配置值（没有则检测），Lua 路径取内核实际扫描的目录
    /// （内核开放了「改目录」的配置项，所以由内核配置说了算；它没有配置时才用默认目录）。
    /// </summary>
    public void RefreshPaths()
    {
        SteamPath = SteamPathFromConfig();
        _steamService.SetSteamPath(SteamPath);

        LuaPath = ResolveLuaPath();
        _steamService.SetLuaPath(LuaPath);
    }

    /// <summary>Steam 路径：本次已填的 → 已保存的 → 注册表检测结果；都没有返回空串</summary>
    private string SteamPathFromConfig()
    {
        var candidates = new[] { _configService.Config.SteamPath, _steamService.GetSteamPath() };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
               ?? _steamService.DetectSteamPath()
               ?? string.Empty;
    }

    /// <summary>
    /// 生效的 Lua 目录：内核配置（opensteamtool.toml 的 [lua] paths）优先，其次是已保存的设置，
    /// 都没有就是默认目录 &lt;Steam&gt;\config\lua；路径以 Steam 目录为基准解析。
    /// </summary>
    private string ResolveLuaPath()
    {
        var steam = _steamService.GetSteamPath() ?? string.Empty;
        var fromKernel = _steamDllService.GetLuaPath();
        var raw = !string.IsNullOrWhiteSpace(fromKernel)
            ? fromKernel
            : (string.IsNullOrWhiteSpace(LuaPath) ? _configService.Config.LuaPath : LuaPath);
        return ResolveAgainst(raw, steam);
    }

    /// <summary>
    /// 内核与 GUI 共用同一 Lua 目录：把当前生效目录写进内核配置（等于默认目录时改写为注释行）。
    /// </summary>
    private void SyncLuaPathToKernel()
    {
        var (ok, message) = _steamDllService.SetLuaPath(LuaPath, GetDefaultLuaDir());
        SetStatus(ok ? message : $"Lua 路径写入内核配置失败：{message}", ok ? "Info" : "Warning");
    }

    /// <summary>默认 Lua 目录：&lt;Steam&gt;\config\lua（没有 Steam 路径时为空串）</summary>
    private string GetDefaultLuaDir()
    {
        var steam = _steamService.GetSteamPath();
        return string.IsNullOrEmpty(steam) ? string.Empty : Path.Combine(steam, "config", "lua");
    }

    /// <summary>当前生效的 Lua 目录（已存储值的相对路径按 Steam 目录补齐）</summary>
    private string GetEffectiveLuaDir() => ResolveAgainst(LuaPath, _steamService.GetSteamPath() ?? string.Empty);

    /// <summary>相对路径按 Steam 根目录解析（与内核 weakly_canonical 的行为一致），绝对路径原样返回</summary>
    private static string ResolveAgainst(string? path, string steamPath)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Path.IsPathFullyQualified(path) || string.IsNullOrEmpty(steamPath)
            ? path
            : Path.GetFullPath(Path.Combine(steamPath, path));
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
                c.LuaPath = LuaPath;
                c.DefaultManifestSource = DefaultSource;
                c.DefaultAddAllDlc = DefaultAddAllDlc;
                c.StFixedVersionDefault = StFixedVersionDefault;
                c.ThemeMode = ThemeMode;
                c.ShowSystemNotifications = ShowSystemNotifications;
                c.ShowVersionChangeNotifications = ShowVersionChangeNotifications;
                c.LogMaxLines = (int)LogMaxLines;
                LogService.SetMaxLines((int)LogMaxLines);

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
    /// Steam 路径失焦处理（检测的是输入框里的新内容）：清理输入（去空白 / 成对引号）；
    /// 为空则自动检测并回填；非空但目录下没有 steam.exe 时回滚为当前生效值。
    /// Lua 目录若仍停在原默认位置，则跟着 Steam 目录一起搬。
    /// </summary>
    public void ApplySteamPathOnBlur()
    {
        var oldDefault = GetDefaultLuaDir();
        var oldLua = GetEffectiveLuaDir();
        var input = NormalizePathInput(SteamPath);

        if (input.Length == 0)
        {
            var detected = _steamService.DetectSteamPath();
            if (string.IsNullOrEmpty(detected))
            {
                SteamPath = _steamService.GetSteamPath() ?? string.Empty;
                SetStatus("未能自动检测到 Steam，请手动指定路径", "Warning");
                SaveAllToConfig();
                return;
            }
            input = detected;
        }
        else if (!File.Exists(Path.Combine(input, "steam.exe")))
        {
            SteamPath = _steamService.GetSteamPath() ?? string.Empty;
            SetStatus("该目录下未找到 steam.exe，已回滚为当前生效路径", "Warning");
            SaveAllToConfig();
            return;
        }

        SteamPath = input;
        _steamService.SetSteamPath(input);
        MigrateLuaPathIfDefault(oldLua, oldDefault);
        SetStatus($"Steam 路径：{input}", "Success");
        SaveAllToConfig();
    }

    /// <summary>
    /// Lua 路径失焦处理（检测的是输入框里的新内容）：清理输入并确保目录可用，然后写入内核配置
    /// （内核与 GUI 从此共用这一个目录）；创建失败则回滚，留空即默认目录。
    /// </summary>
    public void ApplyLuaPathOnBlur()
    {
        var input = NormalizePathInput(LuaPath);
        if (input.Length == 0)
        {
            LuaPath = GetDefaultLuaDir();
        }
        else
        {
            try
            {
                Directory.CreateDirectory(input);
                LuaPath = input;
            }
            catch (Exception ex)
            {
                LuaPath = GetEffectiveLuaDir();
                SetStatus($"Lua 路径不可用（{ex.Message}），已回滚为 {LuaPath}", "Warning");
                SaveAllToConfig();
                return;
            }
        }

        _steamService.SetLuaPath(LuaPath);
        SyncLuaPathToKernel();
        SaveAllToConfig();
    }

    /// <summary>
    /// Lua 目录原本由默认位置推导而来（跟着 Steam 目录走），Steam 目录变了就一起搬；
    /// 显式设过别的目录则保持不动。
    /// </summary>
    private void MigrateLuaPathIfDefault(string oldLua, string oldDefault)
    {
        if (oldLua.Length == 0 || oldDefault.Length == 0
            || !string.Equals(oldLua, oldDefault, StringComparison.OrdinalIgnoreCase)) return;

        LuaPath = GetDefaultLuaDir();
        _steamService.SetLuaPath(LuaPath);
        SyncLuaPathToKernel();
    }

    /// <summary>去掉首尾空白与成对引号（从资源管理器"复制路径"粘贴回来常带引号）</summary>
    private static string NormalizePathInput(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            s = s[1..^1].Trim();
        return s;
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
