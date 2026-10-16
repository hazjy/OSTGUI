using CommunityToolkit.Mvvm.ComponentModel;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// 联机页面 ViewModel - 480 联机（OST -onlinefix）
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public partial class OnlineViewModel : ObservableObject
{
    private readonly OnlineFixService _onlineFixService;
    private readonly GameSearchService _searchService;
    private readonly SteamGameInfoService _gameInfoService;
    private readonly ConfigService _configService;
    private readonly SteamService _steamService;
    private readonly SteamDllService _steamDllService;
    private readonly LuaConfigService _luaService;

    private string _onlineAppId = "";

    public string OnlineAppId
    {
        get => _onlineAppId;
        set => SetProperty(ref _onlineAppId, value);
    }

    private string _gameName = "";

    public string GameName
    {
        get => _gameName;
        set => SetProperty(ref _gameName, value);
    }

    /// <summary>「其他」下拉的选中项（0 = 宿主会话，1 = 文件会话）；改内存，退出落盘，重开记住上次选择</summary>
    private int _otherModeIndex;

    public int OtherModeIndex
    {
        get => _otherModeIndex;
        set
        {
            if (SetProperty(ref _otherModeIndex, value))
            {
                OnOtherModeIndexChanged(value);
            }
        }
    }

    private void OnOtherModeIndexChanged(int value)
        => _configService.Update(c => c.OnlineOtherMode = value);

    // 联机会话身份：默认 Spacewar(480)，自定义时用 SessionAppId
    // 两个联机视图共用这份状态；各视图内的单选靠各自 GroupName 分组（两个视图的名字必须不同）
    // 选择状态记进 AppConfig（ConfigService.Update 只改内存、退出时统一落盘，所以随手记不心疼）。
    private bool _isDefaultSession = true;

    public bool IsDefaultSession
    {
        get => _isDefaultSession;
        set
        {
            if (!SetProperty(ref _isDefaultSession, value)) return;
            _configService.Update(c => c.SessionIsDefault = value);
            // 两个单选互斥：这里同步另一边，免得上层忘记
            if (value && _isCustomSession)
            {
                _isCustomSession = false;
                OnPropertyChanged(nameof(IsCustomSession));
            }
        }
    }

    private bool _isCustomSession;

    public bool IsCustomSession
    {
        get => _isCustomSession;
        set
        {
            if (!SetProperty(ref _isCustomSession, value)) return;
            _configService.Update(c => c.SessionIsDefault = !value);
            if (value && _isDefaultSession)
            {
                _isDefaultSession = false;
                OnPropertyChanged(nameof(IsDefaultSession));
            }
        }
    }

    private string _sessionAppId = "";

    public string SessionAppId
    {
        get => _sessionAppId;
        set
        {
            if (!SetProperty(ref _sessionAppId, value)) return;
            _configService.Update(c => c.SessionAppId = value);
        }
    }

    // ── 启动前置检查（宿主会话 / 文件会话）─────────────────────────────────
    // 三项：Steam 是否运行 · 内核是否已安装 · 游戏是否已入库。
    // 只做展示，不拦截启动。
    // 对外只暴露三个字符串（UI 上每项一块、横排一行，文案 = 名称：就绪 / 名称：错误）——
    // 不走 ItemsControl + 嵌套类型的绑定：AOT + CsWinRT 下嵌套 record 的成员绑定不可靠
    // （第一版就是这么写的，界面上直接空白），顶层属性最稳。
    private string _steamCheckText = "Steam：—";
    public string SteamCheckText
    {
        get => _steamCheckText;
        private set => SetProperty(ref _steamCheckText, value);
    }

    private string _kernelCheckText = "内核：—";
    public string KernelCheckText
    {
        get => _kernelCheckText;
        private set => SetProperty(ref _kernelCheckText, value);
    }

    private string _libraryCheckText = "入库：—";
    public string LibraryCheckText
    {
        get => _libraryCheckText;
        private set => SetProperty(ref _libraryCheckText, value);
    }

    // 入库检查带缓存：按 AppID 记住结果，让定时刷新不必反复读盘（只在首次与换 AppID 时读）。
    private string _libCheckAppId = "";
    private bool _libCheckOk;

    // 上一条已应用的结果摘要：只在变化时写日志，免得 2 秒一条刷屏。
    private string _lastCheckSummary = "";

    // 刷新可能发生在 await 之后（线程池），改属性前统一封送回 UI 线程。
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _ui =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    /// <summary>
    /// 刷新三项前置检查。调用点：进联机页、切页、点「查询」，以及页面那个 2 秒定时器。
    /// </summary>
    public async Task RefreshPrechecksAsync()
    {
        bool steamOk = false, kernelOk = false;
        try { steamOk = _steamService.IsSteamRunning(); } catch { }
        try { kernelOk = _steamDllService.IsOSTDllInjected(); } catch { }

        var appId = HostGameAppId.Trim();
        // 入库只对「查询过的那个 AppID」下判断：没查过就显示未查询，免得把半截输入的号码当真
        if (!_hostGameQueried)
        {
            _libCheckAppId = "";
            _libCheckOk = false;
        }
        else if (appId != _libCheckAppId)
        {
            _libCheckAppId = appId;
            _libCheckOk = false;
            if (appId.Length > 0)
            {
                try { _libCheckOk = await _luaService.ContainsAppIdAsync(appId); } catch { }
            }
        }

        void Apply()
        {
            SteamCheckText = steamOk ? "Steam：就绪" : "Steam：错误";
            KernelCheckText = kernelOk ? "内核：就绪" : "内核：错误";
            LibraryCheckText = !_hostGameQueried
                ? "入库：未查询"
                : _libCheckOk ? "入库：就绪" : "入库：错误";

            var summary = $"{SteamCheckText} {KernelCheckText} {LibraryCheckText}"
                          + (appId.Length > 0 ? $"（AppID {appId}）" : "（未填 AppID）");
            if (summary != _lastCheckSummary)
            {
                _lastCheckSummary = summary;
                LogService.Diag($"前置检查：{summary}");
            }
        }

        if (_ui is null || _ui.HasThreadAccess) Apply();
        else _ui.TryEnqueue(Apply);
    }

    private bool _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    public string StatusText => IsRunning ? "联机游戏中" : "未运行";
    public bool CanStart => !IsRunning && !IsBusy;

    // 「其他」页 —— 宿主会话：游戏 AppID（查询定位游戏程序）与定位结果
    // （宿主会话 = OnlineHost.exe 以会话身份拉起游戏；不经 Steam 启动，游戏侧因此拿到
    //  会话身份的 SteamAppId 环境变量。见 ZSteamTool/docs/dev/REF-内核-联机会话.md）
    private string _hostGameAppId = "";

    public string HostGameAppId
    {
        get => _hostGameAppId;
        set
        {
            if (!SetProperty(ref _hostGameAppId, value)) return;
            // 输入框内容一变，之前的「已查询」就作废 —— 入库状态只对查询过的那个 AppID 才准确
            _hostGameQueried = false;
        }
    }

    // 是否已对当前 AppID 点过「查询」并定位成功：入库检查只在查询之后才有意义
    private bool _hostGameQueried;

    private string _hostGameExePath = "";

    public string HostGameExePath
    {
        get => _hostGameExePath;
        set => SetProperty(ref _hostGameExePath, value);
    }

    /// <summary>协议 AppID：默认 480，或自定义（两个联机视图共用同一份状态）</summary>
    private (bool ok, string value) ResolveSessionAppId()
    {
        if (!IsCustomSession) return (true, "480");

        var input = SessionAppId.Trim();
        // 内核只认 uint32 内的十进制 AppID，非法值会静默回落 480，这里先挡住
        return uint.TryParse(input, out var parsed) && parsed != 0 ? (true, input) : (false, "");
    }

    /// <summary>按「游戏 AppID」定位已安装游戏的主程序；定位不到返回 null</summary>
    public string? ResolveHostGameExe()
    {
        if (HostGameAppId.Trim().Length == 0) return null;

        var exe = _onlineFixService.ResolveGameExe(HostGameAppId.Trim());
        if (exe is null)
        {
            _hostGameQueried = false;
            return null;
        }

        HostGameExePath = exe;
        _hostGameQueried = true;   // 查询成功 → 入库检查从此开始生效
        return exe;
    }

    /// <summary>宿主会话启动：由 OnlineHost.exe 以会话身份拉起游戏（不经 Steam）</summary>
    public (bool success, string message) StartHostSession()
    {
        if (IsRunning)
            return (false, "已有联机游戏在运行，请先停止");

        if (string.IsNullOrWhiteSpace(HostGameExePath) || !File.Exists(HostGameExePath))
            return (false, "请先单击「查询」定位游戏程序");

        var (sessionOk, sessionAppId) = ResolveSessionAppId();
        if (!sessionOk)
            return (false, "请输入正确的协议 AppID（十进制，非 0）");

        var (ok, msg) = _onlineFixService.StartViaHost(HostGameExePath, sessionAppId);
        RefreshRunningState();
        return (ok, msg);
    }

    /// <summary>文件会话启动（文件法：宿主写游戏 exe 同目录的 steam_appid.txt 后拉起游戏，退出即还原）</summary>
    public (bool success, string message) StartFileSession()
    {
        if (IsRunning)
            return (false, "已有联机游戏在运行，请先停止");

        if (string.IsNullOrWhiteSpace(HostGameExePath) || !File.Exists(HostGameExePath))
            return (false, "请先单击「查询」定位游戏程序");

        var (sessionOk, sessionAppId) = ResolveSessionAppId();
        if (!sessionOk)
            return (false, "请输入正确的协议 AppID（十进制，非 0）");

        var (ok, msg) = _onlineFixService.StartViaHost(HostGameExePath, sessionAppId, viaAppIdFile: true);
        RefreshRunningState();
        return (ok, msg);
    }

    /// <summary>停止宿主会话联机游戏（宿主 + 它拉起的游戏）</summary>
    public (bool success, string message) StopHostSession()
    {
        var (ok, msg) = _onlineFixService.StopViaHost();
        RefreshRunningState();
        return (ok, msg);
    }

    public OnlineViewModel(
        OnlineFixService onlineFixService,
        GameSearchService searchService,
        SteamGameInfoService gameInfoService,
        ConfigService configService,
        SteamService steamService,
        SteamDllService steamDllService,
        LuaConfigService luaService)
    {
        _onlineFixService = onlineFixService;
        _searchService = searchService;
        _gameInfoService = gameInfoService;
        _configService = configService;
        _steamService = steamService;
        _steamDllService = steamDllService;
        _luaService = luaService;

        // 直接赋字段：走属性会触发一次无意义落盘
        _otherModeIndex = configService.Config.OnlineOtherMode;
        // 会话身份：恢复上次的选择状态（老配置没有这两个键时用默认值兜底）
        _isDefaultSession = configService.Config.SessionIsDefault;
        _isCustomSession = !_isDefaultSession;
        _sessionAppId = string.IsNullOrWhiteSpace(configService.Config.SessionAppId)
            ? "480"
            : configService.Config.SessionAppId;
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
            return (false, "请先填写正确的 AppID");

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
    /// 刷新运行状态（含两种启动方式：命令行会话 `-onlinefix` 与宿主会话）
    /// </summary>
    public void RefreshRunningState()
        => IsRunning = _onlineFixService.IsRunning() || _onlineFixService.IsHostRunning();
}
