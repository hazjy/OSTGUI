using CommunityToolkit.Mvvm.ComponentModel;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// 联机页面 ViewModel - 480 联机（OST -onlinefix）
/// </summary>
public partial class OnlineViewModel : ObservableObject
{
    private readonly OnlineFixService _onlineFixService;
    private readonly GameInfoService _gameInfoService;
    private readonly GameNameCacheService _nameCache;

    [ObservableProperty] private string _onlineAppId = "";
    [ObservableProperty] private string _gameName = "";
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
        GameInfoService gameInfoService,
        GameNameCacheService nameCache)
    {
        _onlineFixService = onlineFixService;
        _gameInfoService = gameInfoService;
        _nameCache = nameCache;
    }

    /// <summary>
    /// 根据 AppID 查询游戏名（先查本地缓存）
    /// </summary>
    public async Task LoadGameNameAsync()
    {
        var appId = OnlineAppId.Trim();
        if (string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit))
        {
            GameName = "";
            return;
        }

        if (_nameCache.TryGet(appId, out var cached))
        {
            GameName = cached;
            return;
        }

        try
        {
            var info = await _gameInfoService.GetGameDetailsAsync(appId);
            if (info != null && !string.IsNullOrEmpty(info.Name))
            {
                GameName = info.Name;
                _nameCache.Set(appId, info.Name);
            }
            else
            {
                GameName = "";
            }
        }
        catch
        {
            GameName = "";
        }
    }

    /// <summary>
    /// 启动 480 联机
    /// </summary>
    public async Task<(bool success, string message)> StartAsync()
    {
        var appId = OnlineAppId.Trim();
        if (string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit))
            return (false, "请先输入正确的 AppID");

        if (IsRunning)
            return (false, "已有联机游戏在运行，请先停止");

        IsBusy = true;
        try
        {
            var (ok, msg) = await _onlineFixService.StartAsync(appId);
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
