using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NoSteamLauncher.Models;
using NoSteamLauncher.Services;
using OSTGUI.Services;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OSTGUI.ViewModels;

public partial class NoSteamViewModel : ObservableObject
{
    private readonly NoSteamLauncherService _noSteamService;
    private readonly SteamService _steamService;
    private readonly SteamDllService _steamDllService;
    private readonly ILogger<NoSteamViewModel> _logger;
    private readonly ILogger<NoSteamLaunchOrchestrator> _orchestratorLogger;
    private readonly ILogger<SteamlessService> _steamlessLogger;
    private readonly ILogger<GBEDeploymentService> _gbeLogger;
    private readonly ConfigService _configService;

    [ObservableProperty] private string _gameExePath = "";
    [ObservableProperty] private string _appId = "";
    [ObservableProperty] private bool _isUEGame;
    [ObservableProperty] private string _UEEnginePath = "";
    [ObservableProperty] private bool _backupOriginalExe = true;
    [ObservableProperty] private bool _generateInterfaces = true;
    [ObservableProperty] private bool _skipSteamless;
    [ObservableProperty] private bool _skipGBE;
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private int _steamlessTimeoutMinutes = 5;
    [ObservableProperty] private int _verifyLaunchTimeoutSeconds = 5;
    [ObservableProperty] private string _launchArgs = "";
    [ObservableProperty] private string _workingDirectory = "";
    [ObservableProperty] private string _progressLog = "";
    [ObservableProperty] private bool _isRunning;

    // Advanced GBE config properties
    [ObservableProperty] private string _advancedAccountName = "";
    [ObservableProperty] private string _advancedSteamId = "";
    [ObservableProperty] private string _advancedLanguage = "schinese";
    [ObservableProperty] private bool _advancedUnlockAllDlc = true;
    [ObservableProperty] private string _advancedDlcList = "";
    [ObservableProperty] private bool _advancedOfflineMode;
    [ObservableProperty] private bool _advancedDisableNetworking;
    [ObservableProperty] private string _advancedControllerType = "XBOX360";
    [ObservableProperty] private bool _advancedSteamDeck;
    [ObservableProperty] private string _advancedCustomBroadcast = "";

    public NoSteamViewModel(
        NoSteamLauncherService noSteamService,
        SteamService steamService,
        SteamDllService steamDllService,
        ILogger<NoSteamViewModel> logger,
        ILogger<NoSteamLaunchOrchestrator> orchestratorLogger,
        ILogger<SteamlessService> steamlessLogger,
        ILogger<GBEDeploymentService> gbeLogger,
        ConfigService configService)
    {
        _noSteamService = noSteamService;
        _steamService = steamService;
        _steamDllService = steamDllService;
        _logger = logger;
        _orchestratorLogger = orchestratorLogger;
        _steamlessLogger = steamlessLogger;
        _gbeLogger = gbeLogger;
        _configService = configService;

        LoadOptionsFromConfig();
    }

    public void LoadOptionsFromConfig()
    {
        try
        {
            var c = _configService.Config;
            BackupOriginalExe = c.DefaultBackupOriginalExe;
            GenerateInterfaces = c.GenerateInterfacesDefault;
            SkipSteamless = c.SkipSteamlessDefault;
            SkipGBE = c.SkipGBEDefault;
            DryRun = c.DryRunDefault;
            SteamlessTimeoutMinutes = c.SteamlessTimeoutMinutesDefault;
            VerifyLaunchTimeoutSeconds = c.VerifyLaunchTimeoutSecondsDefault;
            LaunchArgs = c.LaunchArgsDefault;
            WorkingDirectory = c.WorkingDirectoryDefault;

            AdvancedAccountName = c.AdvancedAccountName;
            AdvancedSteamId = c.AdvancedSteamId;
            AdvancedLanguage = c.AdvancedLanguage;
            AdvancedUnlockAllDlc = c.AdvancedUnlockAllDlc;
            AdvancedDlcList = c.AdvancedDlcList;
            AdvancedOfflineMode = c.AdvancedOfflineMode;
            AdvancedDisableNetworking = c.AdvancedDisableNetworking;
            AdvancedControllerType = c.AdvancedControllerType;
            AdvancedSteamDeck = c.AdvancedSteamDeck;
            AdvancedCustomBroadcast = c.AdvancedCustomBroadcast;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load NoSteam options from config");
        }
    }

    public void SaveOptionsToConfig()
    {
        _configService.UpdateAndSaveAsync(c =>
        {
            c.DefaultBackupOriginalExe = BackupOriginalExe;
            c.GenerateInterfacesDefault = GenerateInterfaces;
            c.SkipSteamlessDefault = SkipSteamless;
            c.SkipGBEDefault = SkipGBE;
            c.DryRunDefault = DryRun;
            c.SteamlessTimeoutMinutesDefault = SteamlessTimeoutMinutes;
            c.VerifyLaunchTimeoutSecondsDefault = VerifyLaunchTimeoutSeconds;
            c.LaunchArgsDefault = LaunchArgs;
            c.WorkingDirectoryDefault = WorkingDirectory;
        }).GetAwaiter().GetResult();
    }

    public void SaveAdvancedConfigToConfig()
    {
        _configService.UpdateAndSaveAsync(c =>
        {
            c.AdvancedAccountName = AdvancedAccountName;
            c.AdvancedSteamId = AdvancedSteamId;
            c.AdvancedLanguage = AdvancedLanguage;
            c.AdvancedUnlockAllDlc = AdvancedUnlockAllDlc;
            c.AdvancedDlcList = AdvancedDlcList;
            c.AdvancedOfflineMode = AdvancedOfflineMode;
            c.AdvancedDisableNetworking = AdvancedDisableNetworking;
            c.AdvancedControllerType = AdvancedControllerType;
            c.AdvancedSteamDeck = AdvancedSteamDeck;
            c.AdvancedCustomBroadcast = AdvancedCustomBroadcast;
        }).GetAwaiter().GetResult();
    }

    partial void OnBackupOriginalExeChanged(bool value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    partial void OnGenerateInterfacesChanged(bool value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    partial void OnSkipSteamlessChanged(bool value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    partial void OnSkipGBEChanged(bool value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    partial void OnDryRunChanged(bool value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    partial void OnSteamlessTimeoutMinutesChanged(int value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    partial void OnVerifyLaunchTimeoutSecondsChanged(int value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    partial void OnLaunchArgsChanged(string value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    partial void OnWorkingDirectoryChanged(string value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    [RelayCommand]
    private async Task OpenDeploymentOptionsAsync()
    {
        if (App.MainWindow is Window window)
        {
            var cbBackup = new CheckBox { Content = "备份原 EXE", IsChecked = BackupOriginalExe };
            ToolTipService.SetToolTip(cbBackup, "部署前将原 EXE 备份为 .bak");
            
            var cbInterfaces = new CheckBox { Content = "生成 steam_interfaces.txt", IsChecked = GenerateInterfaces };
            ToolTipService.SetToolTip(cbInterfaces, "使用 generate_interfaces 工具生成接口文件");
            
            var cbSkipSteamless = new CheckBox { Content = "跳过 Steamless 脱壳", IsChecked = SkipSteamless };
            ToolTipService.SetToolTip(cbSkipSteamless, "跳过 SteamStub 脱壳步骤（适用于无 Stub 的游戏）");
            
            var cbSkipGBE = new CheckBox { Content = "跳过 GBE 部署", IsChecked = SkipGBE };
            ToolTipService.SetToolTip(cbSkipGBE, "仅运行 Steamless，不部署 Goldberg 模拟器");
            
            var cbDryRun = new CheckBox { Content = "仅干跑 (不修改文件)", IsChecked = DryRun };
            ToolTipService.SetToolTip(cbDryRun, "模拟部署流程，不实际写入文件，用于预览/调试");
            
            var panel = new StackPanel { Spacing = 16, MinWidth = 360 };
            panel.Children.Add(cbBackup);
            panel.Children.Add(cbInterfaces);
            panel.Children.Add(cbSkipSteamless);
            panel.Children.Add(cbSkipGBE);
            panel.Children.Add(cbDryRun);

            var dialog = new ContentDialog
            {
                Title = "部署选项设置",
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = window.Content.XamlRoot,
                Content = panel
            };
            
            var result = await dialog.ShowAsync();
            
            if (result == ContentDialogResult.Primary)
            {
                BackupOriginalExe = cbBackup.IsChecked ?? false;
                GenerateInterfaces = cbInterfaces.IsChecked ?? false;
                SkipSteamless = cbSkipSteamless.IsChecked ?? false;
                SkipGBE = cbSkipGBE.IsChecked ?? false;
                DryRun = cbDryRun.IsChecked ?? false;
            }
        }
    }

    [RelayCommand]
    private async Task OpenAdvancedConfigAsync()
    {
        if (App.MainWindow is not Window window) return;

        // 账号名称
        var tbAccountName = new TextBox { PlaceholderText = "账号名称 (显示在好友列表/成就)", Text = AdvancedAccountName, MinWidth = 600 };
        ToolTipService.SetToolTip(tbAccountName, "对应 configs.user.ini 的 account_name");

        // SteamID64
        var tbSteamId = new TextBox { PlaceholderText = "SteamID64 (17位数字，留空自动生成)", Text = AdvancedSteamId, MinWidth = 600 };
        tbSteamId.InputScope = new InputScope { Names = { new InputScopeName { NameValue = InputScopeNameValue.Number } } };
        ToolTipService.SetToolTip(tbSteamId, "对应 configs.user.ini 的 account_steamid，用于存档隔离和联机识别");

        // 语言
        var cbLanguage = new ComboBox { PlaceholderText = "语言", SelectedItem = AdvancedLanguage, MinWidth = 600 };
        cbLanguage.Items.Add("schinese"); cbLanguage.Items.Add("english"); cbLanguage.Items.Add("japanese"); cbLanguage.Items.Add("korean"); cbLanguage.Items.Add("french"); cbLanguage.Items.Add("german"); cbLanguage.Items.Add("spanish"); cbLanguage.Items.Add("russian"); cbLanguage.Items.Add("portuguese"); cbLanguage.Items.Add("polish"); cbLanguage.Items.Add("italian"); cbLanguage.Items.Add("turkish"); cbLanguage.Items.Add("tchinese");
        ToolTipService.SetToolTip(cbLanguage, "对应 configs.user.ini 的 language，需在 supported_languages.txt 中存在");

        // DLC 模式
        var rbUnlockAll = new RadioButton { Content = "全解锁所有 DLC (unlock_all=1)", IsChecked = AdvancedUnlockAllDlc, GroupName = "DlcMode", Margin = new Thickness(0, 4, 0, 0) };
        var rbWhitelist = new RadioButton { Content = "仅解锁 DLC.txt 白名单 (unlock_all=0)", IsChecked = !AdvancedUnlockAllDlc, GroupName = "DlcMode", Margin = new Thickness(0, 4, 0, 0) };
        ToolTipService.SetToolTip(rbUnlockAll, "开启后自动解锁游戏所有 DLC，无需手动维护列表");
        ToolTipService.SetToolTip(rbWhitelist, "仅解锁 DLC.txt 中列出的 DLC，更安全但需手动维护");

        // DLC 白名单编辑
        var tbDlcList = new TextBox { PlaceholderText = "DLC 白名单，每行格式: AppID=名称", Text = AdvancedDlcList, MinWidth = 600, MinHeight = 100, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace") };
        ScrollViewer.SetVerticalScrollBarVisibility(tbDlcList, ScrollBarVisibility.Auto);
        ToolTipService.SetToolTip(tbDlcList, "unlock_all=0 时生效，对应 steam_settings/DLC.txt");

        // 离线模式
        var cbOffline = new CheckBox { Content = "离线模式 (模拟 Steam 离线状态)", IsChecked = AdvancedOfflineMode, Margin = new Thickness(0, 4, 0, 0) };
        ToolTipService.SetToolTip(cbOffline, "创建 offline.txt，游戏将在离线模式下运行，不尝试连接 Steam 网络");

        // 禁用网络
        var cbDisableNet = new CheckBox { Content = "完全禁用网络 (破坏大厅/联机功能)", IsChecked = AdvancedDisableNetworking, Margin = new Thickness(0, 4, 0, 0) };
        ToolTipService.SetToolTip(cbDisableNet, "创建 disable_networking.txt，彻底断开 Steam 网络连接，联机游戏慎用");

        // 控制器类型
        var cbControllerType = new ComboBox { PlaceholderText = "控制器类型", SelectedItem = AdvancedControllerType, MinWidth = 600, Margin = new Thickness(0, 4, 0, 0) };
        cbControllerType.Items.Add("XBOX360"); cbControllerType.Items.Add("XBOXONE"); cbControllerType.Items.Add("PS4"); cbControllerType.Items.Add("PS5"); cbControllerType.Items.Add("SWITCH");
        ToolTipService.SetToolTip(cbControllerType, "对应 configs.app.ini [app::controller] type，游戏只识别特定手柄时设置");

        // Steam Deck 伪装
        var cbSteamDeck = new CheckBox { Content = "伪装为 Steam Deck", IsChecked = AdvancedSteamDeck, Margin = new Thickness(0, 4, 0, 0) };
        ToolTipService.SetToolTip(cbSteamDeck, "对应 configs.main.ini steam_deck=1，触发 Deck 专用 UI/配置");

        // 自定义广播
        var tbCustomBroadcast = new TextBox { PlaceholderText = "自定义广播地址 (每行一个 IP/域名)", Text = AdvancedCustomBroadcast, MinWidth = 600, MinHeight = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        ToolTipService.SetToolTip(tbCustomBroadcast, "对应 custom_broadcasts.txt，局域网联机指定广播目标");

        var panel = new StackPanel { Spacing = 12, MinWidth = 700 };
        panel.Children.Add(new TextBlock { Text = "账号与身份", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(tbAccountName);
        panel.Children.Add(tbSteamId);
        panel.Children.Add(cbLanguage);
        panel.Children.Add(new TextBlock { Text = "DLC 管理", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(rbUnlockAll);
        panel.Children.Add(rbWhitelist);
        panel.Children.Add(tbDlcList);
        panel.Children.Add(new TextBlock { Text = "网络与模式", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(cbOffline);
        panel.Children.Add(cbDisableNet);
        panel.Children.Add(cbSteamDeck);
        panel.Children.Add(new TextBlock { Text = "输入设备", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(cbControllerType);
        panel.Children.Add(new TextBlock { Text = "局域网联机", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(tbCustomBroadcast);

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            MaxHeight = 600,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinWidth = 700
        };

        var dialog = new ContentDialog
        {
            Title = "高级配置 (GBE steam_settings)",
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = window.Content.XamlRoot,
            Content = scrollViewer,
            MinWidth = 750
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            AdvancedAccountName = tbAccountName.Text?.Trim() ?? "";
            AdvancedSteamId = tbSteamId.Text?.Trim() ?? "";
            AdvancedLanguage = cbLanguage.SelectedItem?.ToString() ?? "schinese";
            AdvancedUnlockAllDlc = rbUnlockAll.IsChecked == true;
            AdvancedDlcList = tbDlcList.Text?.Trim() ?? "";
            AdvancedOfflineMode = cbOffline.IsChecked ?? false;
            AdvancedDisableNetworking = cbDisableNet.IsChecked ?? false;
            AdvancedControllerType = cbControllerType.SelectedItem?.ToString() ?? "XBOX360";
            AdvancedSteamDeck = cbSteamDeck.IsChecked ?? false;
            AdvancedCustomBroadcast = tbCustomBroadcast.Text?.Trim() ?? "";

            SaveAdvancedConfigToConfig();
        }
    }

    private string? BuildDlcContent()
    {
        if (AdvancedUnlockAllDlc)
        {
            // 全解锁模式：写入 unlock_all = 1
            return "unlock_all = 1\n";
        }

        // 白名单模式：使用用户输入的 DLC 列表
        if (string.IsNullOrWhiteSpace(AdvancedDlcList))
            return null;

        return AdvancedDlcList.Trim();
    }

    [RelayCommand]
    private async Task BrowseGameExeAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            FileTypeFilter = { ".exe" }
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            GameExePath = file.Path;
            TryAutoFillAppId(file.Path);
            _logger.LogInformation("Selected game EXE: {Path}", file.Path);
        }
    }

    private void TryAutoFillAppId(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (dir == null) return;

            var appIdFile = Path.Combine(dir, "steam_appid.txt");
            if (File.Exists(appIdFile))
            {
                var content = File.ReadAllText(appIdFile).Trim();
                if (int.TryParse(content, out _))
                {
                    AppId = content;
                    return;
                }
            }

            var acfFiles = Directory.GetFiles(dir, "appmanifest_*.acf");
            if (acfFiles.Length > 0)
            {
                var content = File.ReadAllText(acfFiles[0]);
                var match = System.Text.RegularExpressions.Regex.Match(content, @"""appid""\s+""(\d+)""");
                if (match.Success)
                {
                    AppId = match.Groups[1].Value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto-fill AppID failed");
        }
    }

    [RelayCommand]
    private async Task BrowseUEEnginePathAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            UEEnginePath = folder.Path;
            _logger.LogInformation("Selected UE Engine path: {Path}", folder.Path);
        }
    }

    [RelayCommand]
    private async Task BrowseWorkingDirectoryAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            WorkingDirectory = folder.Path;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteDeploy))]
    private async Task DeployAsync()
    {
        if (IsRunning) return;

        if (string.IsNullOrWhiteSpace(GameExePath) || !File.Exists(GameExePath))
        {
            ProgressLog += "[ERROR] 请选择有效的游戏 EXE 文件\n";
            return;
        }

        if (string.IsNullOrWhiteSpace(AppId) || !int.TryParse(AppId, out _))
        {
            ProgressLog += "[ERROR] 请输入有效的 Steam AppID (数字)\n";
            return;
        }

        if (IsUEGame && string.IsNullOrWhiteSpace(UEEnginePath))
        {
            ProgressLog += "[ERROR] UE 游戏必须指定 Engine 路径\n";
            return;
        }

        if (IsUEGame && !Directory.Exists(UEEnginePath))
        {
            ProgressLog += "[ERROR] UE Engine 路径不存在\n";
            return;
        }

        IsRunning = true;
        ProgressLog = "";
        ProgressLog += "[INFO] 开始部署...\n";

        try
        {
            var options = new LaunchOptions
            {
                GameExePath = GameExePath,
                AppId = AppId,
                IsUEGame = IsUEGame,
                UEEnginePath = IsUEGame ? UEEnginePath : null,
                BackupOriginalExe = BackupOriginalExe,
                GenerateInterfaces = GenerateInterfaces,
                SkipSteamless = SkipSteamless,
                SkipGBE = SkipGBE,
                DryRun = DryRun,
                SteamlessTimeout = TimeSpan.FromMinutes(Math.Max(1, SteamlessTimeoutMinutes)),
                VerifyLaunchTimeout = TimeSpan.FromSeconds(Math.Max(1, VerifyLaunchTimeoutSeconds)),
                LaunchArgs = string.IsNullOrWhiteSpace(LaunchArgs) ? null : LaunchArgs,
                WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory,

                // 高级配置映射
                ForceAccountName = !string.IsNullOrWhiteSpace(AdvancedAccountName) ? AdvancedAccountName.Trim() : null,
                ForceSteamId = !string.IsNullOrWhiteSpace(AdvancedSteamId) ? AdvancedSteamId.Trim() : null,
                ForceLanguage = !string.IsNullOrWhiteSpace(AdvancedLanguage) ? AdvancedLanguage.Trim() : null,
                DlcContent = BuildDlcContent(),
                OfflineMode = AdvancedOfflineMode,
                DisableNetworking = AdvancedDisableNetworking,
                ControllerType = !string.IsNullOrWhiteSpace(AdvancedControllerType) ? AdvancedControllerType.Trim() : null,
                SpoofSteamDeck = AdvancedSteamDeck,
                CustomBroadcasts = !string.IsNullOrWhiteSpace(AdvancedCustomBroadcast) ? AdvancedCustomBroadcast.Trim() : null,
                UnlockAllDlc = AdvancedUnlockAllDlc
            };

            var result = await _noSteamService.ExecuteAsync(
                options,
                _orchestratorLogger,
                _steamlessLogger,
                _gbeLogger);

            if (result.Success)
            {
                var msg = DryRun 
                    ? $"✅ 干跑完成！将部署 {result.GBEDeploy.DeployedFiles.Length} 个文件，耗时 {result.TotalDuration.TotalSeconds:F1}s"
                    : $"✅ 部署成功！部署了 {result.GBEDeploy.DeployedFiles.Length} 个文件，耗时 {result.TotalDuration.TotalSeconds:F1}s";
                if (result.GameProcessId.HasValue)
                    msg += $"\n游戏进程 PID: {result.GameProcessId}";
                ProgressLog += $"[SUCCESS] {msg}\n";
            }
            else
            {
                ProgressLog += $"[ERROR] 部署失败: {result.ErrorMessage}\n";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deploy failed");
            ProgressLog += $"[ERROR] 异常: {ex.Message}\n";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanExecuteDeploy() => !IsRunning;

    [RelayCommand]
    private void ClearLog()
    {
        ProgressLog = "";
    }
}