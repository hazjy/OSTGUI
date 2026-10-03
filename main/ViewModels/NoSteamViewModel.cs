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

[WinRT.GeneratedBindableCustomProperty]
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

    // 命令手写在 VM 上：Native AOT 下 CsWinRT 绑定提供器看不到源生成成员，XAML {Binding} 会失效
    public IAsyncRelayCommand BrowseGameExeCommand { get; }
    public IAsyncRelayCommand OpenDeploymentOptionsCommand { get; }
    public IAsyncRelayCommand OpenAdvancedConfigCommand { get; }
    public IAsyncRelayCommand DeployCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }
    public IRelayCommand ClearLogCommand { get; }

    private string _gameExePath = "";

    public string GameExePath
    {
        get => _gameExePath;
        set => SetProperty(ref _gameExePath, value);
    }

    private string _appId = "";

    public string AppId
    {
        get => _appId;
        set => SetProperty(ref _appId, value);
    }

    private bool _backupOriginalExe = true;

    public bool BackupOriginalExe
    {
        get => _backupOriginalExe;
        set
        {
            if (SetProperty(ref _backupOriginalExe, value))
            {
                OnBackupOriginalExeChanged(value);
            }
        }
    }

    private bool _skipSteamless;

    public bool SkipSteamless
    {
        get => _skipSteamless;
        set
        {
            if (SetProperty(ref _skipSteamless, value))
            {
                OnSkipSteamlessChanged(value);
            }
        }
    }

    private bool _skipGBE;

    public bool SkipGBE
    {
        get => _skipGBE;
        set
        {
            if (SetProperty(ref _skipGBE, value))
            {
                OnSkipGBEChanged(value);
            }
        }
    }

    private bool _dryRun;

    public bool DryRun
    {
        get => _dryRun;
        set
        {
            if (SetProperty(ref _dryRun, value))
            {
                OnDryRunChanged(value);
            }
        }
    }
    private int _steamlessTimeoutMinutes = 5;

    public int SteamlessTimeoutMinutes
    {
        get => _steamlessTimeoutMinutes;
        set
        {
            if (SetProperty(ref _steamlessTimeoutMinutes, value))
            {
                OnSteamlessTimeoutMinutesChanged(value);
            }
        }
    }

    private string _progressLog = "";

    public string ProgressLog
    {
        get => _progressLog;
        set => SetProperty(ref _progressLog, value);
    }

    /// <summary>
    /// 把全局日志里带 <c>[NoSteam]</c> 的行镜像到本页日志栏。
    ///
    /// 为什么需要：launcher 的 <c>progress</c> 与 <c>ILogger</c> 都写进 <see cref="LogService"/>，
    /// 而本页原来只看得到自己写的那几行（<c>[INFO]</c> / <c>[ERROR]</c>…）——"日志栏看不到部署细节"。
    /// 镜像之后：Steamless 命令行、GBE 部署、备份还原、Plugins 暂存/还原等每一行都出现在这里
    /// （launcher 的 <c>_logger</c> 由 <see cref="ProgressLogger{T}"/> 并进同一条 progress 通道再写进 LogService）。
    ///
    /// 订阅成对挂在 Loaded / Unloaded（先 <c>-=</c> 再 <c>+=</c>）：事件源是静态集合，构造函数里订阅
    /// 会让页面被永久钉住（同 <c>doc/开发踩坑-UI.md</c> 里日志栏那条）。
    /// </summary>
    public void AttachLogMirror()
    {
        LogService.Logs.CollectionChanged -= OnGlobalLogChanged;
        LogService.Logs.CollectionChanged += OnGlobalLogChanged;
    }

    public void DetachLogMirror() => LogService.Logs.CollectionChanged -= OnGlobalLogChanged;

    private const string NoSteamLogPrefix = "[NoSteam]";

    private void OnGlobalLogChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;

        foreach (var item in e.NewItems)
        {
            if (item is not string line) continue;

            // 全局行形如 "[2026-10-04 06:07:03.123] [p21348] [D] [NoSteam] 正文"，这里只取正文
            var idx = line.IndexOf(NoSteamLogPrefix, StringComparison.Ordinal);
            if (idx < 0) continue;

            ProgressLog += line[(idx + NoSteamLogPrefix.Length)..].TrimStart() + "\n";
        }
    }

    private bool _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    // Advanced GBE config properties
    private string _advancedAccountName = "";

    public string AdvancedAccountName
    {
        get => _advancedAccountName;
        set => SetProperty(ref _advancedAccountName, value);
    }

    private string _advancedSteamId = "";

    public string AdvancedSteamId
    {
        get => _advancedSteamId;
        set => SetProperty(ref _advancedSteamId, value);
    }

    private string _advancedLanguage = "schinese";

    public string AdvancedLanguage
    {
        get => _advancedLanguage;
        set => SetProperty(ref _advancedLanguage, value);
    }

    private bool _advancedUnlockAllDlc = true;

    public bool AdvancedUnlockAllDlc
    {
        get => _advancedUnlockAllDlc;
        set => SetProperty(ref _advancedUnlockAllDlc, value);
    }

    private string _advancedDlcList = "";

    public string AdvancedDlcList
    {
        get => _advancedDlcList;
        set => SetProperty(ref _advancedDlcList, value);
    }

    private bool _advancedOfflineMode;

    public bool AdvancedOfflineMode
    {
        get => _advancedOfflineMode;
        set => SetProperty(ref _advancedOfflineMode, value);
    }

    private bool _advancedDisableNetworking;

    public bool AdvancedDisableNetworking
    {
        get => _advancedDisableNetworking;
        set => SetProperty(ref _advancedDisableNetworking, value);
    }

    private bool _advancedBypassSteamApiCheck;

    public bool AdvancedBypassSteamApiCheck
    {
        get => _advancedBypassSteamApiCheck;
        set => SetProperty(ref _advancedBypassSteamApiCheck, value);
    }

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

        BrowseGameExeCommand = new AsyncRelayCommand(BrowseGameExeAsync);
        OpenDeploymentOptionsCommand = new AsyncRelayCommand(OpenDeploymentOptionsAsync);
        OpenAdvancedConfigCommand = new AsyncRelayCommand(OpenAdvancedConfigAsync);
        DeployCommand = new AsyncRelayCommand(DeployAsync, CanExecuteDeploy);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync);
        ClearLogCommand = new RelayCommand(ClearLog);

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

            AdvancedAccountName = c.AdvancedAccountName;
            AdvancedSteamId = c.AdvancedSteamId;
            AdvancedLanguage = c.AdvancedLanguage;
            AdvancedUnlockAllDlc = c.AdvancedUnlockAllDlc;
            AdvancedDlcList = c.AdvancedDlcList;
            AdvancedOfflineMode = c.AdvancedOfflineMode;
            AdvancedDisableNetworking = c.AdvancedDisableNetworking;
            AdvancedBypassSteamApiCheck = c.AdvancedSteamApiCheckBypass;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load NoSteam options from config");
        }
    }

    public void SaveOptionsToConfig()
    {
        _configService.Update(c =>
        {
            c.DefaultBackupOriginalExe = BackupOriginalExe;
            c.SkipSteamlessDefault = SkipSteamless;
            c.SkipGBEDefault = SkipGBE;
            c.DryRunDefault = DryRun;
            c.SteamlessTimeoutMinutesDefault = SteamlessTimeoutMinutes;
        });
    }

    public void SaveAdvancedConfigToConfig()
    {
        _configService.Update(c =>
        {
            c.AdvancedAccountName = AdvancedAccountName;
            c.AdvancedSteamId = AdvancedSteamId;
            c.AdvancedLanguage = AdvancedLanguage;
            c.AdvancedUnlockAllDlc = AdvancedUnlockAllDlc;
            c.AdvancedDlcList = AdvancedDlcList;
            c.AdvancedOfflineMode = AdvancedOfflineMode;
            c.AdvancedDisableNetworking = AdvancedDisableNetworking;
            c.AdvancedSteamApiCheckBypass = AdvancedBypassSteamApiCheck;
        });
    }

    private void OnBackupOriginalExeChanged(bool value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    private void OnSkipSteamlessChanged(bool value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    private void OnSkipGBEChanged(bool value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    private void OnDryRunChanged(bool value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

    private void OnSteamlessTimeoutMinutesChanged(int value)
    {
        if (_configService.IsLoaded)
            SaveOptionsToConfig();
    }

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

            var cbDryRun = new CheckBox { Content = "仅试运行（不修改文件）", IsChecked = DryRun };
            ToolTipService.SetToolTip(cbDryRun, "模拟部署流程，不实际写入文件，用于预览与调试");

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

            Helpers.PopupTheme.Apply(dialog);
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
        var tbDlcList = new TextBox { PlaceholderText = "DLC 白名单，每行格式：AppID=名称", Text = AdvancedDlcList, MinWidth = 600, MinHeight = 100, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Code, Consolas, monospace") };
        ScrollViewer.SetVerticalScrollBarVisibility(tbDlcList, ScrollBarVisibility.Auto);
        ToolTipService.SetToolTip(tbDlcList, "白名单模式时生效，写入游戏目录 steam_settings/configs.app.ini 的 [app::dlcs] 段，每行格式：AppID=名称");

        // 离线模式
        var cbOffline = new CheckBox { Content = "离线模式", IsChecked = AdvancedOfflineMode };
        ToolTipService.SetToolTip(cbOffline, "configs.main.ini 的 offline=1，游戏将在离线模式下运行");

        // 禁用网络
        var cbDisableNet = new CheckBox { Content = "完全禁用网络", IsChecked = AdvancedDisableNetworking };
        ToolTipService.SetToolTip(cbDisableNet, "configs.main.ini 的 disable_networking=1，联机游戏请慎用");

        // SteamAPICheckBypass（反模拟器检测）
        var cbBypass = new CheckBox { Content = "部署 SteamAPICheckBypass", IsChecked = AdvancedBypassSteamApiCheck };
        ToolTipService.SetToolTip(cbBypass,
            "部署 winmm.dll 劫持与文件重定向规则，向游戏的自身完整性/反作弊检查隐藏模拟器与备份痕迹（对齐 SAC）。" +
            "个别自带 winmm 依赖或反作弊的游戏可能冲突，默认关闭");

        var networkPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        networkPanel.Children.Add(cbOffline);
        networkPanel.Children.Add(cbDisableNet);
        networkPanel.Children.Add(cbBypass);

        var panel = new StackPanel { Spacing = 12, MinWidth = 700 };
        panel.Children.Add(new TextBlock { Text = "账号与身份（选择账户后自动填充）", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 4) });
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
            Title = "高级配置（GBE）",
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = window.Content.XamlRoot,
            Content = scrollViewer,
            MinWidth = 750
        };

        Helpers.PopupTheme.Apply(dialog);
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
            AdvancedBypassSteamApiCheck = cbBypass.IsChecked ?? false;

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

        Helpers.PopupTheme.Apply(tipDialog);
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
            ProgressLog += "[ERROR] 请输入有效的 Steam AppID（数字）\n";
            return;
        }

        IsRunning = true;
        ProgressLog = "";
        ProgressLog += "[INFO] 开始部署…\n";

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

                // 高级配置映射
                ForceAccountName = !string.IsNullOrWhiteSpace(AdvancedAccountName) ? AdvancedAccountName.Trim() : null,
                ForceSteamId = !string.IsNullOrWhiteSpace(AdvancedSteamId) ? AdvancedSteamId.Trim() : null,
                ForceLanguage = !string.IsNullOrWhiteSpace(AdvancedLanguage) ? AdvancedLanguage.Trim() : null,
                DlcContent = BuildDlcContent(),
                OfflineMode = AdvancedOfflineMode,
                DisableNetworking = AdvancedDisableNetworking,
                UnlockAllDlc = AdvancedUnlockAllDlc,
                EnableSteamAPICheckBypass = AdvancedBypassSteamApiCheck
            };

            // 验证配置
            try
            {
                options.Validate();
            }
            catch (Exception ex)
            {
                ProgressLog += $"[ERROR] 配置验证失败：{ex.Message}\n";
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
                    ? $"试运行完成，将部署 {result.GBEDeploy.DeployedFiles.Length} 个文件，耗时 {result.TotalDuration.TotalSeconds:F1}s"
                    : $"部署成功，已部署 {result.GBEDeploy.DeployedFiles.Length} 个文件，耗时 {result.TotalDuration.TotalSeconds:F1}s";
                ProgressLog += $"[SUCCESS] {msg}\n";
            }
            else
            {
                ProgressLog += $"[ERROR] 部署失败：{result.ErrorMessage}\n";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deploy failed");
            ProgressLog += $"[ERROR] 异常：{ex.Message}\n";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanExecuteDeploy() => !IsRunning;

    /// <summary>一键还原：撤掉部署进游戏目录的模拟器产物（EXE/DLL 备份、steam_settings、Bypass）</summary>
    private async Task RestoreAsync()
    {
        if (IsRunning)
        {
            ProgressLog += "[WARN] 正在部署中，请稍后再试\n";
            return;
        }

        if (string.IsNullOrWhiteSpace(GameExePath) || !File.Exists(GameExePath))
        {
            ProgressLog += "[ERROR] 请先选择有效的游戏 EXE 文件\n";
            return;
        }

        IsRunning = true;
        ProgressLog += $"[INFO] 开始还原 {GameExePath}\n";

        try
        {
            var result = await Task.Run(() => _noSteamService.Restore(GameExePath, _gbeLogger));

            foreach (var action in result.Actions)
                ProgressLog += $"[RESTORE] {action}\n";
            foreach (var failure in result.Failures)
                ProgressLog += $"[WARN] {failure}\n";

            ProgressLog += result.Success
                ? "[SUCCESS] 还原完成，游戏目录已回到部署前状态\n"
                : "[WARN] 还原未完成：请关闭游戏后重试（文件被占用时无法替换）\n";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore failed");
            ProgressLog += $"[ERROR] 异常：{ex.Message}\n";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void ClearLog()
    {
        ProgressLog = "";
    }
}
