using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using OSTGUI.Models;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// 主窗口 ViewModel - 管理全局状态和导航
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public ConfigService ConfigService { get; }
    public SteamService SteamService { get; }
    private readonly SteamDllService _steamDllService;
    public GameSearchService SearchService { get; }
    public LuaConfigService LuaService { get; }
    public ManifestService ManifestService { get; }
    public TicketService TicketService { get; }

    [ObservableProperty] private string _statusMessage = "就绪";
    private bool _isSteamRunning;
    public bool IsSteamRunning
    {
        get => _isSteamRunning;
        set
        {
            if (SetProperty(ref _isSteamRunning, value))
            {
                OnPropertyChanged(nameof(SteamRunningText));
                OnPropertyChanged(nameof(SteamPathDisplay)); // 也可能需要更新
            }
        }
    }

    private bool _isOstInjected;
    public bool IsOstInjected
    {
        get => _isOstInjected;
        set
        {
            if (SetProperty(ref _isOstInjected, value))
            {
                OnPropertyChanged(nameof(OstStatusText));
            }
        }
    }
    private int _totalGames;
    public int TotalGames
    {
        get => _totalGames;
        set
        {
            if (SetProperty(ref _totalGames, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }
    [ObservableProperty] private int _fixedVersionCount;
    [ObservableProperty] private int _autoVersionCount;
    [ObservableProperty] private string _steamPathDisplay = "未检测到";
    [ObservableProperty] private string _title = "OSTGUI";
    public string OstStatusText => IsOstInjected ? "已注入" : "未注入";
    public string SteamRunningText => IsSteamRunning ? "运行中" : "未运行";

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _steamStatusTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _libraryRefreshTimer;

    // 子 ViewModels
    public SearchViewModel SearchVM { get; }
    public LibraryViewModel LibraryVM { get; }
    public DenuvoViewModel DenuvoVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public OnlineViewModel OnlineVM { get; }

    public MainViewModel(
        ConfigService configService,
        SteamService steamService,
        GameSearchService searchService,
        GameInfoService gameInfoService,
        GameNameCacheService gameNameCacheService,
        OnlineFixService onlineFixService,
        LuaConfigService luaService,
        ManifestService manifestService,
        TicketService ticketService,
        OstFileService ostFileService,
        SteamGameInfoService steamGameInfoService,
        SteamTicketExtractor ticketExtractor,
        SteamDllService steamDllService,
        SudamaKeyCache sudamaCache)
    {
        ConfigService = configService;
        SteamService = steamService;
        SearchService = searchService;
        LuaService = luaService;
        ManifestService = manifestService;
        TicketService = ticketService;
        _steamDllService = steamDllService;

        // 初始化子 ViewModel
        SearchVM = new SearchViewModel(searchService, manifestService, steamService, configService);
        LibraryVM = new LibraryViewModel(luaService, searchService, gameInfoService, steamService, manifestService, configService);
        DenuvoVM = new DenuvoViewModel(
            ticketService, luaService, searchService, ostFileService,
            steamGameInfoService, steamService, ticketExtractor);
        SettingsVM = new SettingsViewModel(configService, steamService, _steamDllService, sudamaCache);
        OnlineVM = new OnlineViewModel(onlineFixService, gameInfoService, gameNameCacheService);
    }

    /// <summary>
    /// 应用初始化
    /// </summary>
    public async Task InitializeAsync(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        // 加载配置
        await ConfigService.LoadAsync();
        var config = ConfigService.Config;

        // 检测 Steam 路径
        var steamPath = !string.IsNullOrEmpty(config.SteamPath)
            ? config.SteamPath
            : SteamService.DetectSteamPath();

        if (!string.IsNullOrEmpty(steamPath))
        {
            SteamService.SetSteamPath(steamPath);
            SteamPathDisplay = steamPath;
        }
        else
        {
            SteamPathDisplay = "未检测到 Steam，请在设置中手动配置";
        }

        // 检查状态
        IsOstInjected = _steamDllService.IsOSTDllInjected();
        IsSteamRunning = SteamService.IsSteamRunning();

        StatusMessage = "就绪";

        // 启动 Steam 状态轮询
        StartSteamStatusPolling(dispatcherQueue);

        // 启动库刷新定时器
        StartLibraryRefreshTimer(dispatcherQueue);
    }

    /// <summary>
    /// 启动 Steam 状态轮询
    /// </summary>
    public void StartSteamStatusPolling(DispatcherQueue queue)
    {
        StopSteamStatusPolling();
        _steamStatusTimer = queue.CreateTimer();
        _steamStatusTimer.Interval = TimeSpan.FromSeconds(3);
        _steamStatusTimer.Tick += OnSteamStatusTimerTick;
        _steamStatusTimer.Start();
    }

    /// <summary>
    /// 停止 Steam 状态轮询
    /// </summary>
    public void StopSteamStatusPolling()
    {
        if (_steamStatusTimer != null)
        {
            _steamStatusTimer.Stop();
            _steamStatusTimer.Tick -= OnSteamStatusTimerTick;
            _steamStatusTimer = null;
        }
    }

    /// <summary>
    /// 启动库刷新定时器
    /// </summary>
    public void StartLibraryRefreshTimer(DispatcherQueue queue)
    {
        StopLibraryRefreshTimer();
        _libraryRefreshTimer = queue.CreateTimer();
        _libraryRefreshTimer.Interval = TimeSpan.FromSeconds(5);
        _libraryRefreshTimer.Tick += OnLibraryRefreshTimerTick;
        _libraryRefreshTimer.Start();
    }

    /// <summary>
    /// 停止库刷新定时器
    /// </summary>
    public void StopLibraryRefreshTimer()
    {
        if (_libraryRefreshTimer != null)
        {
            _libraryRefreshTimer.Stop();
            _libraryRefreshTimer.Tick -= OnLibraryRefreshTimerTick;
            _libraryRefreshTimer = null;
        }
    }

    private async void OnLibraryRefreshTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        await RefreshLibraryStatsAsync();
    }

    private void OnSteamStatusTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        var isRunning = SteamService.IsSteamRunning();
        if (IsSteamRunning != isRunning)
        {
            IsSteamRunning = isRunning;
        }
    }

    /// <summary>
    /// 刷新库统计
    /// </summary>
    public async Task RefreshLibraryStatsAsync()
    {
        var items = await LuaService.ScanLibraryAsync();
        var games = items.Where(i => i.AppId != "N/A").ToList();
        TotalGames = games.Count;
        FixedVersionCount = games.Count(i => i.VersionMode == "fixed");
        AutoVersionCount = games.Count(i => i.VersionMode == "auto");
        Title = $"已入库游戏：{TotalGames}";
    }

    /// <summary>
    /// 刷新 OST 注入状态
    /// </summary>
    public void RefreshOstStatus()
    {
        IsOstInjected = _steamDllService.IsOSTDllInjected();
        IsSteamRunning = SteamService.IsSteamRunning();
    }
}
