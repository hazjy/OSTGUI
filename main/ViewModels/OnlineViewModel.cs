using CommunityToolkit.Mvvm.ComponentModel;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// 联机页面 ViewModel - 480 联机（OST -onlinefix）
/// </summary>
public partial class OnlineViewModel : ObservableObject
{
    private readonly OnlineFixService _onlineFixService;
    private readonly GameSearchService _searchService;
    private readonly SteamGameInfoService _gameInfoService;

    [ObservableProperty] private string _onlineAppId = "";
    [ObservableProperty] private string _gameName = "";

    // 联机会话身份：默认 Spacewar(480)，自定义时用 SessionAppId（内核 -onlinefix=<appid>）
    [ObservableProperty] private bool _isDefaultSession = true;
    [ObservableProperty] private bool _isCustomSession;
    [ObservableProperty] private string _sessionAppId = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isBusy;

    public string StatusText => IsRunning ? "联机游戏中" : "未运行";
    public bool CanStart => !IsRunning && !IsBusy;

    public OnlineViewModel(
        OnlineFixService onlineFixService,
        GameSearchService searchService,
        SteamGameInfoService gameInfoService)
    {
        _onlineFixService = onlineFixService;
        _searchService = searchService;
        _gameInfoService = gameInfoService;
    }

    /// <summary>
    /// 查询游戏名：与搜索/入库同源（GameSearchService：缓存优先 → Steam 官方 Store API → 写缓存）。
    /// steamcmd（GetGameNameOnlineAsync）仅作兜底；官方接口即搜索页取名成功之路。
    /// </summary>
    public async Task LoadGameNameAsync()
    {
        var appId = OnlineAppId.Trim();
        if (string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit))
        {
            GameName = "";
            return;
        }

        var name = await _searchService.GetGameNameAsync(appId);
        if (string.IsNullOrEmpty(name))
        {
            // 官方接口未果时补一层 steamcmd 兜底
            name = await _gameInfoService.GetGameNameOnlineAsync(appId) ?? "";
        }
        GameName = name;
    }

    /// <summary>
    /// 启动联机（默认 480 身份，或自定义会话身份）
    /// </summary>
    public async Task<(bool success, string message)> StartAsync()
    {
        var appId = OnlineAppId.Trim();
        if (string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit))
            return (false, "请先输入正确的 AppID");

        var sessionAppId = "480";
        if (IsCustomSession)
        {
            sessionAppId = SessionAppId.Trim();
            // 内核只认 uint32 内的十进制 AppID，非法值会静默回落 480，这里先挡住
            if (!uint.TryParse(sessionAppId, out var parsed) || parsed == 0)
                return (false, "请输入正确的会话身份 AppID（十进制，非 0）");
        }

        if (IsRunning)
            return (false, "已有联机游戏在运行，请先停止");

        IsBusy = true;
        try
        {
            var (ok, msg) = await _onlineFixService.StartAsync(appId, sessionAppId);
            RefreshRunningState();
            return (ok, msg);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 停止 480 联机游戏
    /// </summary>
    public (bool success, string message) Stop()
    {
        var (ok, msg) = _onlineFixService.Stop();
        RefreshRunningState();
        return (ok, msg);
    }

    /// <summary>
    /// 刷新运行状态
    /// </summary>
    public void RefreshRunningState()
        => IsRunning = _onlineFixService.IsRunning();
}
