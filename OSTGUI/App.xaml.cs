using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using OSTGUI.ViewModels;
using OSTGUI.Services;
using System.Net.Http;
using System.Text.Json;

namespace OSTGUI;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;
    public new static App Current => (App)Application.Current;
    public static Window? MainWindow => ((App)Current)._window;

    private Window? _window;
    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "crash.log");

    public App()
    {
        this.InitializeComponent();

        // 全局异常日志（写入 crash.log）
        UnhandledException += (s, e) =>
        {
            Log($"UnhandledException: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log($"AppDomain UnhandledException: {e.ExceptionObject}");
        };

        var services = new ServiceCollection();
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        services.AddSingleton(httpClient);
        services.AddSingleton<ConfigService>();
        services.AddSingleton<SteamService>();
        services.AddSingleton<SteamDllService>();
        services.AddSingleton<GameNameCacheService>();
        services.AddSingleton<SteamSearchProvider>();
        services.AddSingleton<GameSearchService>();
        services.AddSingleton<GameInfoService>();
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<LuaConfigService>();
        services.AddSingleton<SudamaKeyCache>();
        services.AddSingleton<SteamGameInfoService>();
        services.AddSingleton<LuaBuilder>();
        services.AddSingleton<ManifestFileService>();
        services.AddSingleton<ManifestDownloadService>();
        services.AddSingleton<LuaRepairService>();
        services.AddSingleton<ManifestRepairService>();
        services.AddSingleton<ManifestService>();
        services.AddSingleton<TicketService>();
        services.AddSingleton<OstFileService>();
        services.AddSingleton<SteamTicketExtractor>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<DenuvoViewModel>();
        services.AddTransient<SettingsViewModel>();
        Services = services.BuildServiceProvider();

        // 初始化 Toast 服务
        ToastService.Initialize(Services.GetRequiredService<ConfigService>());
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

        // 先完整读取配置文件，再创建窗口，
        // 避免窗口先以默认状态显示、随后又被配置恢复导致闪烁
        var configService = Services.GetRequiredService<ConfigService>();
        await configService.LoadAsync();
        Log("Config loaded");

        _window = new MainWindow();
        Log("MainWindow created");
        _window.Activate();
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
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
