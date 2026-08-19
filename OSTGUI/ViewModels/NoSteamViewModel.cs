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
    [ObservableProperty] private bool _backupOriginalExe = true;
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
        }).GetAwaiter().GetResult();
    }

    partial void OnBackupOriginalExeChanged(bool value)
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

            var cbSkipSteamless = new CheckBox { Content = "跳过 Steamless 脱壳", IsChecked = SkipSteamless };
            ToolTipService.SetToolTip(cbSkipSteamless, "跳过 SteamStub 脱壳步骤（适用于无 Stub 的游戏）");

            var cbSkipGBE = new CheckBox { Content = "跳过 GBE 部署", IsChecked = SkipGBE };
            ToolTipService.SetToolTip(cbSkipGBE, "仅运行 Steamless，不部署 Goldberg 模拟器");

            var cbDryRun = new CheckBox { Content = "仅干跑 (不修改文件)", IsChecked = DryRun };
            ToolTipService.SetToolTip(cbDryRun, "模拟部署流程，不实际写入文件，用于预览/调试");

            var panel = new StackPanel { Spacing = 16, MinWidth = 360 };
            panel.Children.Add(cbBackup);
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

        // 账户选择下拉菜单
        var accounts = _steamService.GetSteamAccounts();
        var cbAccount = new ComboBox { PlaceholderText = "选择账户", MinWidth = 600 };
        cbAccount.Items.Add("不指定账户");
        foreach (var acc in accounts)
        {
            cbAccount.Items.Add($"{acc.AccountName} ({acc.PersonaName})");
        }
        // 设置当前选中的账户
        if (!string.IsNullOrWhiteSpace(AdvancedAccountName))
        {
            var match = accounts.FirstOrDefault(a => a.AccountName == AdvancedAccountName);
            if (match.AccountName != null)
            {
                cbAccount.SelectedItem = $"{match.AccountName} ({match.PersonaName})";
            }
            else
            {
                cbAccount.SelectedItem = "不指定账户";
            }
        }
        else
        {
            cbAccount.SelectedItem = "不指定账户";
        }
        ToolTipService.SetToolTip(cbAccount, "选择本机 Steam 账户，将自动填充账号名称和 SteamID");

        // 语言
        var cbLanguage = new ComboBox { PlaceholderText = "语言", MinWidth = 600 };
        cbLanguage.Items.Add("schinese"); cbLanguage.Items.Add("english"); cbLanguage.Items.Add("japanese"); cbLanguage.Items.Add("korean"); cbLanguage.Items.Add("french"); cbLanguage.Items.Add("german"); cbLanguage.Items.Add("spanish"); cbLanguage.Items.Add("russian"); cbLanguage.Items.Add("portuguese"); cbLanguage.Items.Add("polish"); cbLanguage.Items.Add("italian"); cbLanguage.Items.Add("turkish"); cbLanguage.Items.Add("tchinese");
        cbLanguage.SelectedItem = AdvancedLanguage;
        ToolTipService.SetToolTip(cbLanguage, "对应 configs.user.ini 的 language");

        // DLC 模式
        var rbUnlockAll = new RadioButton { Content = "全解锁所有 DLC", IsChecked = AdvancedUnlockAllDlc, GroupName = "DlcMode" };
        var rbWhitelist = new RadioButton { Content = "仅解锁 DLC.txt 白名单", IsChecked = !AdvancedUnlockAllDlc, GroupName = "DlcMode" };
        ToolTipService.SetToolTip(rbUnlockAll, "开启后自动解锁游戏所有 DLC，无需手动维护列表");
        ToolTipService.SetToolTip(rbWhitelist, "仅解锁 DLC.txt 中列出的 DLC，更安全但需手动维护");
        var dlcPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        dlcPanel.Children.Add(rbUnlockAll);
        dlcPanel.Children.Add(rbWhitelist);

        // DLC 白名单编辑
        var tbDlcList = new TextBox { PlaceholderText = "DLC 白名单，每行格式: AppID=名称", Text = AdvancedDlcList, MinWidth = 600, MinHeight = 100, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace") };
        ScrollViewer.SetVerticalScrollBarVisibility(tbDlcList, ScrollBarVisibility.Auto);
        ToolTipService.SetToolTip(tbDlcList, "白名单模式时生效，对应 steam_settings/DLC.txt");

        // 离线模式
        var cbOffline = new CheckBox { Content = "离线模式", IsChecked = AdvancedOfflineMode };
        ToolTipService.SetToolTip(cbOffline, "configs.main.ini 的 offline=1，游戏将在离线模式下运行");

        // 禁用网络
        var cbDisableNet = new CheckBox { Content = "完全禁用网络", IsChecked = AdvancedDisableNetworking };
        ToolTipService.SetToolTip(cbDisableNet, "configs.main.ini 的 disable_networking=1，联机游戏慎用");

        var networkPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        networkPanel.Children.Add(cbOffline);
        networkPanel.Children.Add(cbDisableNet);

        var panel = new StackPanel { Spacing = 12, MinWidth = 700 };
        panel.Children.Add(new TextBlock { Text = "账号与身份 (选择账户后自动填充)", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(cbAccount);
        panel.Children.Add(new TextBlock { Text = "语言", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(cbLanguage);
        panel.Children.Add(new TextBlock { Text = "DLC 管理", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(dlcPanel);
        panel.Children.Add(tbDlcList);
        panel.Children.Add(new TextBlock { Text = "网络模式", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(networkPanel);

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
            Title = "高级配置 (GBE)",
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
            // 处理账户选择
            var selectedAccount = cbAccount.SelectedItem?.ToString();
            if (selectedAccount == "不指定账户")
            {
                AdvancedAccountName = "";
                AdvancedSteamId = "";
            }
            else if (selectedAccount != null && selectedAccount.Contains("("))
            {
                // 从 "AccountName (PersonaName)" 格式提取
                var accountName = selectedAccount.Split(' ')[0];
                var matched = accounts.FirstOrDefault(a => a.AccountName == accountName);
                if (matched.AccountName != null)
                {
                    AdvancedAccountName = matched.AccountName;
                    // 从 loginusers.vdf 中查找对应的 SteamID
                    AdvancedSteamId = FindSteamIdByAccountName(matched.AccountName, accounts);
                }
            }

            AdvancedLanguage = cbLanguage.SelectedItem?.ToString() ?? "schinese";
            AdvancedUnlockAllDlc = rbUnlockAll.IsChecked == true;
            AdvancedDlcList = tbDlcList.Text?.Trim() ?? "";
            AdvancedOfflineMode = cbOffline.IsChecked ?? false;
            AdvancedDisableNetworking = cbDisableNet.IsChecked ?? false;

            SaveAdvancedConfigToConfig();
        }
    }

    private string FindSteamIdByAccountName(string accountName, List<(string AccountName, string PersonaName, bool RememberPassword)> accounts)
    {
        // 从 loginusers.vdf 中读取 SteamID
        try
        {
            var steamPath = _steamService.GetSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return "";

            var vdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(vdfPath)) return "";

            var content = File.ReadAllText(vdfPath);
            // 简单的 VDF 解析：查找 "AccountName" "xxx" 后的 SteamID
            var lines = content.Split('\n');
            string currentSteamId = "";
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("\"") && line.EndsWith("\"") && line.Count(c => c == '"') == 2)
                {
                    // 这可能是 SteamID 行
                    var id = line.Trim('"');
                    if (id.Length > 15 && id.StartsWith("7656119"))
                    {
                        currentSteamId = id;
                    }
                }
                if (line.Contains($"\"AccountName\"") && line.Contains($"\"{accountName}\""))
                {
                    return currentSteamId;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FindSteamIdByAccountName failed");
        }
        return "";
    }

    private string? BuildDlcContent()
    {
        if (AdvancedUnlockAllDlc)
            return null;

        if (string.IsNullOrWhiteSpace(AdvancedDlcList))
            return null;

        return AdvancedDlcList.Trim();
    }

    [RelayCommand]
    private async Task BrowseGameExeAsync()
    {
        if (App.MainWindow is not Window window) return;

        // 选择前提示
        var tipDialog = new ContentDialog
        {
            Title = "选择游戏",
            PrimaryButtonText = "选择",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = window.Content.XamlRoot,
            Content = "请选择游戏的主程序 EXE 文件\n\n请确保选择的是正确的游戏启动程序"
        };

        var tipResult = await tipDialog.ShowAsync();
        if (tipResult != ContentDialogResult.Primary) return;

        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            FileTypeFilter = { ".exe" }
        };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
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

        IsRunning = true;
        ProgressLog = "";
        ProgressLog += "[INFO] 开始部署...\n";

        try
        {
            var options = new LaunchOptions
            {
                GameExePath = GameExePath,
                AppId = AppId,
                BackupOriginalExe = BackupOriginalExe,
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
                UnlockAllDlc = AdvancedUnlockAllDlc
            };

            // 验证配置
            try
            {
                options.Validate();
            }
            catch (Exception ex)
            {
                ProgressLog += $"[ERROR] 配置验证失败: {ex.Message}\n";
                IsRunning = false;
                return;
            }

            var result = await _noSteamService.ExecuteAsync(
                options,
                _orchestratorLogger,
                _steamlessLogger,
                _gbeLogger);

            if (result.Success)
            {
                var msg = DryRun
                    ? $"干跑完成！将部署 {result.GBEDeploy.DeployedFiles.Length} 个文件，耗时 {result.TotalDuration.TotalSeconds:F1}s"
                    : $"部署成功！部署了 {result.GBEDeploy.DeployedFiles.Length} 个文件，耗时 {result.TotalDuration.TotalSeconds:F1}s";
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
