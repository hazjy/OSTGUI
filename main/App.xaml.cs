using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OSTGUI.ViewModels;
using OSTGUI.Services;
using NoSteamLauncher.Services;
using System.Net.Http;
using System.Text.Json;

namespace OSTGUI;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;
    public new static App Current => (App)Application.Current;
    public static Window? MainWindow => ((App)Current)._window;

    private Window? _window;

    public App()
    {
        this.InitializeComponent();

        // 全局异常日志（写入应用日志文件）
        UnhandledException += (s, e) =>
        {
            Log($"UnhandledException: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log($"AppDomain UnhandledException: {e.ExceptionObject}");
        };

        // 初始化日志文件（本地数据目录）
        LogService.Initialize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OSTGUI", "logs", "ostgui.log"));
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched start");

        // 提取模式：作为子进程运行时只执行提取并退出，不创建窗口
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length >= 3 &&
            cmdArgs[1].Equals("--extract-ticket", StringComparison.OrdinalIgnoreCase))
        {
            RunExtractTicketMode(cmdArgs);
            return;
        }

        // 成就读写子进程：同样不建窗口，干完即退（实现见 Services/SteamStatsChild.cs）
        if (cmdArgs.Length >= 5 && cmdArgs[1].StartsWith("--stats", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(SteamStatsChild.Run(cmdArgs));
            return;
        }

        // 先完整读取配置文件，再创建窗口，
        // 避免窗口先以默认状态显示、随后又被配置恢复导致闪烁
        var services = new ServiceCollection();
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        services.AddSingleton(httpClient);
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<ConfigService>();
        services.AddSingleton<SteamService>();
        services.AddSingleton<SteamDllService>();
        services.AddSingleton<GameNameCacheService>();
        services.AddSingleton<SteamSearchProvider>();
        services.AddSingleton<GameSearchService>();
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<LuaConfigService>();
        services.AddSingleton<SudamaKeyCache>();
        services.AddSingleton<CoverImageService>();   // 入库管理卡片封面（磁盘缓存，见 CoverImageService）
        services.AddSingleton<SteamGameInfoService>();
        services.AddSingleton<LuaBuilder>();
        services.AddSingleton<ManifestFileService>();
        services.AddSingleton<ManifestDownloadService>();
        services.AddSingleton<TicketService>();
        services.AddSingleton<OstFileService>();
        services.AddSingleton<OnlineFixService>();
        services.AddSingleton<AchievementStore>();
        services.AddSingleton<SteamStatsService>();
        services.AddSingleton<NoSteamLauncherService>();
        services.AddSingleton<SteamlessService>();
        services.AddSingleton<GBEDeploymentService>();
        services.AddSingleton<NoSteamLaunchOrchestrator>();
        services.AddSingleton<UbisoftDeploymentService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<DenuvoViewModel>();
        services.AddTransient<NoSteamViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AchievementViewModel>();
        Services = services.BuildServiceProvider();

        // 上次的文件法联机会话（AppID Changer）若没还原干净，这里补一刀
        Services.GetRequiredService<OnlineFixService>().RestoreAppIdFileLeftover();

        var configService = Services.GetRequiredService<ConfigService>();
        await configService.LoadAsync();
        Log("Config loaded");
        LogService.SetMaxLines(configService.Config.LogMaxLines);

        _window = new MainWindow();
        Log("MainWindow created");
        _window.Activate();

        // 必须在 Activate() 之后：恢复"最大化"会显示/激活窗口，早于订阅窗口的 Activated 事件
        // 会让初始化永久丢失（症状：内容区空白、Steam 路径/DLL 为空）
        if (_window is MainWindow mainWindow)
        {
            mainWindow.ApplyStartupMaximizeIfNeeded();
            mainWindow.EnsureInitialized();
        }

        Log("MainWindow activated");
    }

    /// <summary>
    /// 子进程提取模式：--extract-ticket &lt;appid&gt; "&lt;输出文件&gt;"
    /// 提取完成后立即退出，避免主进程被 Steam 识别为游戏进程
    /// </summary>
    private static void RunExtractTicketMode(string[] cmdArgs)
    {
        try
        {
            var appId = cmdArgs[2];
            var outFile = cmdArgs.Length >= 4 ? cmdArgs[3] : "";
            var extractor = new SteamTicketExtractor();
            var result = extractor.Extract(appId);

            if (!string.IsNullOrEmpty(outFile))
                File.WriteAllText(outFile, JsonSerializer.Serialize(result));

            Log($"extract mode done: success={result.Success} {result.Message}");
        }
        catch (Exception ex)
        {
            Log($"extract mode error: {ex}");
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static void Log(string msg)
        => LogService.AddAppLog(msg);
}
