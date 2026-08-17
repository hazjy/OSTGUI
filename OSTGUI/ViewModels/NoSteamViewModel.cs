using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NoSteamLauncher.Models;
using NoSteamLauncher.Services;
using OSTGUI.Services;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;

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

    [ObservableProperty] private string _statusMessage = "就绪";
    [ObservableProperty] private string _statusType = "Info";
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
            var panel = new StackPanel { Spacing = 16, MinWidth = 360 };
            var cb1 = new CheckBox { Content = "备份原 EXE", IsChecked = BackupOriginalExe };
            ToolTipService.SetToolTip(cb1, "部署前将原 EXE 备份为 .bak");
            panel.Children.Add(cb1);
            var cb2 = new CheckBox { Content = "生成 steam_interfaces.txt", IsChecked = GenerateInterfaces };
            ToolTipService.SetToolTip(cb2, "使用 generate_interfaces 工具生成接口文件");
            panel.Children.Add(cb2);
            var cb3 = new CheckBox { Content = "跳过 Steamless 脱壳", IsChecked = SkipSteamless };
            ToolTipService.SetToolTip(cb3, "跳过 SteamStub 脱壳步骤（适用于无 Stub 的游戏）");
            panel.Children.Add(cb3);
            var cb4 = new CheckBox { Content = "跳过 GBE 部署", IsChecked = SkipGBE };
            ToolTipService.SetToolTip(cb4, "仅运行 Steamless，不部署 Goldberg 模拟器");
            panel.Children.Add(cb4);
            var cb5 = new CheckBox { Content = "仅干跑 (不修改文件)", IsChecked = DryRun };
            ToolTipService.SetToolTip(cb5, "模拟部署流程，不实际写入文件，用于预览/调试");
            panel.Children.Add(cb5);

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
        }
    }

    public void RefreshStatus()
    {
        var isSteamRunning = _steamService.IsSteamRunning();
        var isInjected = _steamDllService.IsOSTDllInjected();

        StatusMessage = isSteamRunning ? "Steam 正在运行" : "Steam 未运行";
        StatusType = isSteamRunning ? "Warning" : "Success";
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
            ShowError("请选择有效的游戏 EXE 文件");
            return;
        }

        if (string.IsNullOrWhiteSpace(AppId) || !int.TryParse(AppId, out _))
        {
            ShowError("请输入有效的 Steam AppID (数字)");
            return;
        }

        if (IsUEGame && string.IsNullOrWhiteSpace(UEEnginePath))
        {
            ShowError("UE 游戏必须指定 Engine 路径");
            return;
        }

        if (IsUEGame && !Directory.Exists(UEEnginePath))
        {
            ShowError("UE Engine 路径不存在");
            return;
        }

        IsRunning = true;
        ProgressLog = "";
        ShowInfo("开始部署...");

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
                WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory
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
                ShowSuccess(msg);
            }
            else
            {
                ShowError($"❌ 部署失败: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deploy failed");
            ShowError($"❌ 异常: {ex.Message}");
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

    private void ShowInfo(string msg)
    {
        StatusMessage = msg;
        StatusType = "Info";
        ProgressLog += $"[INFO] {msg}\n";
    }

    private void ShowSuccess(string msg)
    {
        StatusMessage = msg;
        StatusType = "Success";
        ProgressLog += $"[SUCCESS] {msg}\n";
    }

    private void ShowError(string msg)
    {
        StatusMessage = msg;
        StatusType = "Error";
        ProgressLog += $"[ERROR] {msg}\n";
    }
}