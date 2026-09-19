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
    private readonly ConfigService _configService;

    [ObservableProperty] private string _onlineAppId = "";
    [ObservableProperty] private string _gameName = "";

    /// <summary>「其他」下拉的选中项（0 = DLL 注入，1 = AppID Changer）；改动即落盘，重开记住上次选择</summary>
    [ObservableProperty] private int _otherModeIndex;

    partial void OnOtherModeIndexChanged(int value)
        => _ = _configService.UpdateAndSaveAsync(c => c.OnlineOtherMode = value);

    // 联机会话身份：默认 Spacewar(480)，自定义时用 SessionAppId
    // 两个联机视图共用这份状态；各视图内的单选靠各自 GroupName 分组（两个视图的名字必须不同）
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

    // 「其他」页 —— DLL 注入：游戏 AppID（查询定位游戏程序）与定位结果
    [ObservableProperty] private string _dllGameAppId = "";
    [ObservableProperty] private string _dllGameExePath = "";

    /// <summary>协议 AppID：默认 480，或自定义（两个联机视图共用同一份状态）</summary>
    private (bool ok, string value) ResolveSessionAppId()
    {
        if (!IsCustomSession) return (true, "480");

        var input = SessionAppId.Trim();
        // 内核只认 uint32 内的十进制 AppID，非法值会静默回落 480，这里先挡住
        return uint.TryParse(input, out var parsed) && parsed != 0 ? (true, input) : (false, "");
    }

    /// <summary>按「游戏 AppID」定位已安装游戏的主程序；定位不到返回 null</summary>
    public string? ResolveDllGameExe()
    {
        if (DllGameAppId.Trim().Length == 0) return null;

        var exe = _onlineFixService.ResolveGameExe(DllGameAppId.Trim());
        if (exe is null) return null;

        DllGameExePath = exe;
        return exe;
    }

    /// <summary>DLL 注入启动（宿主 480：OnlineHost.exe 以会话身份拉起游戏）</summary>
    public (bool success, string message) StartDllInject()
    {
        if (IsRunning)
            return (false, "已有联机游戏在运行，请先停止");

        if (string.IsNullOrWhiteSpace(DllGameExePath) || !File.Exists(DllGameExePath))
            return (false, "请先点「查询」定位到游戏程序");

        var (sessionOk, sessionAppId) = ResolveSessionAppId();
        if (!sessionOk)
            return (false, "请输入正确的协议 AppID（十进制，非 0）");

        var (ok, msg) = _onlineFixService.StartViaHost(DllGameExePath, sessionAppId);
        RefreshRunningState();
        return (ok, msg);
    }

    /// <summary>AppID Changer 启动（文件法：宿主写游戏 exe 同目录的 steam_appid.txt 后拉起游戏，退出即还原）</summary>
    public (bool success, string message) StartChanger()
    {
        if (IsRunning)
            return (false, "已有联机游戏在运行，请先停止");

        if (string.IsNullOrWhiteSpace(DllGameExePath) || !File.Exists(DllGameExePath))
            return (false, "请先点「查询」定位到游戏程序");

        var (sessionOk, sessionAppId) = ResolveSessionAppId();
        if (!sessionOk)
            return (false, "请输入正确的协议 AppID（十进制，非 0）");

        var (ok, msg) = _onlineFixService.StartViaHost(DllGameExePath, sessionAppId, viaAppIdFile: true);
        RefreshRunningState();
        return (ok, msg);
    }

    /// <summary>停止 DLL 注入联机游戏（宿主 + 它拉起的游戏）</summary>
    public (bool success, string message) StopDllInject()
    {
        var (ok, msg) = _onlineFixService.StopViaHost();
        RefreshRunningState();
        return (ok, msg);
    }

    public OnlineViewModel(
        OnlineFixService onlineFixService,
        GameSearchService searchService,
        SteamGameInfoService gameInfoService,
        ConfigService configService)
    {
        _onlineFixService = onlineFixService;
        _searchService = searchService;
        _gameInfoService = gameInfoService;
        _configService = configService;

        // 直接赋字段：走属性会触发一次无意义落盘
        _otherModeIndex = configService.Config.OnlineOtherMode;
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

        var (sessionOk, sessionAppId) = ResolveSessionAppId();
        if (!sessionOk)
            return (false, "请输入正确的会话身份 AppID（十进制，非 0）");

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
    /// 刷新运行状态（含两种启动方式：内核 -onlinefix 与 DLL 注入宿主）
    /// </summary>
    public void RefreshRunningState()
        => IsRunning = _onlineFixService.IsRunning() || _onlineFixService.IsHostRunning();
}
