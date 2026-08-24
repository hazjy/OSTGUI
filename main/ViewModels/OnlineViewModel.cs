using CommunityToolkit.Mvvm.ComponentModel;
using OSTGUI.Services;
using System.Collections.ObjectModel;

namespace OSTGUI.ViewModels;

/// <summary>
/// 联机页面 ViewModel - 480 联机
/// 内核模式：steam.exe -applaunch {appId} -onlinefix（OST 内核改写身份）
/// 兼容模式：环境变量直启游戏 exe，全一致 480 世界（邀请校验天然通过）
/// </summary>
public partial class OnlineViewModel : ObservableObject
{
    private readonly OnlineFixService _onlineFixService;
    private readonly SteamGameInfoService _gameInfoService;
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
    [ObservableProperty] private bool _useCompatMode;
    [ObservableProperty] private string _installDir = "";
    [ObservableProperty] private string? _selectedExe;

    public ObservableCollection<string> ExeCandidates { get; } = new();

    public string StatusText => IsRunning ? "联机游戏中" : "未运行";
    public bool CanStart => !IsRunning && !IsBusy;

    public OnlineViewModel(
        OnlineFixService onlineFixService,
        SteamGameInfoService gameInfoService,
        GameNameCacheService nameCache)
    {
        _onlineFixService = onlineFixService;
        _gameInfoService = gameInfoService;
        _nameCache = nameCache;
    }

    partial void OnUseCompatModeChanged(bool value)
    {
        if (value) _ = LoadCompatInfoAsync();
    }

    partial void OnOnlineAppIdChanged(string value)
    {
        if (UseCompatMode) _ = LoadCompatInfoAsync();
    }

    /// <summary>
    /// 兼容模式：解析游戏安装目录与候选 exe
    /// </summary>
    public async Task LoadCompatInfoAsync()
    {
        var appId = OnlineAppId.Trim();
        ExeCandidates.Clear();
        SelectedExe = null;

        if (string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit))
        {
            InstallDir = "";
            return;
        }

        var (dir, exes) = await Task.Run(() => _onlineFixService.ResolveGameInstall(appId));
        InstallDir = dir ?? "未在 Steam 库中找到该游戏，请确认已安装";
        foreach (var exe in exes)
            ExeCandidates.Add(exe);
        SelectedExe = ExeCandidates.FirstOrDefault();
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
            var info = await _gameInfoService.GetGameDetailsFromSteamAsync(appId);
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
    /// 启动 480 联机（按当前模式分流）
    /// </summary>
    public async Task<(bool success, string message)> StartAsync()
    {
        var appId = OnlineAppId.Trim();
        if (string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit))
            return (false, "请先输入正确的 AppID");

        if (IsRunning)
            return (false, "已有联机游戏在运行，请先停止");

        if (UseCompatMode && string.IsNullOrEmpty(SelectedExe))
            return (false, "兼容模式需要选择游戏程序；列表为空请确认游戏已安装或手动刷新");

        IsBusy = true;
        try
        {
            var (ok, msg) = UseCompatMode
                ? await _onlineFixService.StartCompatAsync(appId, SelectedExe!)
                : await _onlineFixService.StartAsync(appId);
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
