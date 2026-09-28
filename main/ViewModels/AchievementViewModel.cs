using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Models;
using OSTGUI.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace OSTGUI.ViewModels;

/// <summary>成就列表的一行：定义（来自 schema）+ 当前状态（来自留底或 Steam）</summary>
[WinRT.GeneratedBindableCustomProperty]
public partial class AchievementRow : ObservableObject
{
    public AchievementRow(AchievementDef def, bool achieved, long unlockTime)
    {
        Def = def;
        _achieved = achieved;
        _unlockTime = unlockTime;
    }

    public AchievementDef Def { get; }
    public string Name => Def.Name;
    public string Title => Def.Hidden && !Achieved ? "隐藏成就" : Def.DisplayName;
    public string Subtitle => Def.Hidden && !Achieved ? "" : Def.Description;
    public string UnlockText => Achieved && UnlockTime > 0
        ? DateTimeOffset.FromUnixTimeSeconds(UnlockTime).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : "";

    private bool _achieved;

    public bool Achieved
    {
        get => _achieved;
        set
        {
            if (SetProperty(ref _achieved, value))
            {
                OnAchievedChanged(value);
            }
        }
    }

    private long _unlockTime;

    public long UnlockTime
    {
        get => _unlockTime;
        set
        {
            if (SetProperty(ref _unlockTime, value))
            {
                OnUnlockTimeChanged(value);
            }
        }
    }

    private void OnAchievedChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(UnlockText));
    }

    private void OnUnlockTimeChanged(long value) => OnPropertyChanged(nameof(UnlockText));
}

/// <summary>
/// 成就编辑：左边挑入库游戏，右边勾成就。勾选只改**本地留底**
/// （%LOCALAPPDATA%\OSTGUI\achievements\&lt;appid&gt;.json），点「保存到 Steam」才写回客户端。
/// 假入库游戏的 Steam 端状态本来就会被服务端/内核清空，留底才是唯一可靠副本。
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public partial class AchievementViewModel : ObservableObject
{
    private readonly SteamService _steam;
    private readonly LibraryScanner _scanner;
    private readonly GameNameCacheService _names;
    private readonly AchievementStore _store;
    private readonly SteamStatsService _stats;
    private readonly GameSearchService _search;
    private readonly ConfigService _config;

    private List<LibraryItem> _allGames = new();
    private List<LibraryItem> _ownedGames = new();
    private Dictionary<string, bool> _baseline = new();
    private string _steamId = "";
    private bool _suppress;
    private bool _ownedLoaded;

    /// <summary>算"游戏"的 appinfo 类型（Fluent-Steam-Lua 同款白名单），其余是 SDK/工具/Redistributable 之类</summary>
    private static readonly HashSet<string> GameTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Game", "Demo", "Mod"
    };

    /// <summary>lua 入库涉及的所有 appid（文件名 + 各 addappid），用来把正版候选排除掉</summary>
    private readonly HashSet<string> _luaIds = new(StringComparer.Ordinal);

    // 命令手写在 VM 上：Native AOT 下 CsWinRT 绑定提供器看不到源生成成员，XAML {Binding} 会失效
    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand UnlockAllCommand { get; }
    public IRelayCommand LockAllCommand { get; }
    public IAsyncRelayCommand SaveToSteamCommand { get; }
    public IAsyncRelayCommand ReadFromSteamCommand { get; }

    private ObservableCollection<LibraryItem> _games = new();

    // 声明类型用 IList<T>：WinRT 内建映射（IVector<T>），Native AOT 下无需在
    // WinRTGlobalVtableLookup 里登记闭合泛型；运行时对象仍是 ObservableCollection（见字段），
    // 集合变更通知行为不变。
    public IList<LibraryItem> Games
    {
        get => _games;
        set => SetProperty(ref _games, value as ObservableCollection<LibraryItem> ?? new(value));
    }

    private LibraryItem? _selectedGame;

    public LibraryItem? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetProperty(ref _selectedGame, value))
            {
                OnSelectedGameChanged(value);
            }
        }
    }

    private ObservableCollection<AchievementRow> _rows = new();

    public ObservableCollection<AchievementRow> Rows
    {
        get => _rows;
        set
        {
            if (SetProperty(ref _rows, value))
            {
                OnRowsChanged(value);
            }
        }
    }

    /// <summary>列表实际显示的成就（= Rows 经过 RowFilter 过滤；保存仍按 Rows 走）</summary>
    private ObservableCollection<AchievementRow> _visibleRows = new();

    public IList<AchievementRow> VisibleRows
    {
        get => _visibleRows;
        set => SetProperty(ref _visibleRows, value as ObservableCollection<AchievementRow> ?? new(value));
    }

    private string _filter = "";

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                OnFilterChanged(value);
            }
        }
    }

    /// <summary>成就列表的过滤词</summary>
    private string _rowFilter = "";

    public string RowFilter
    {
        get => _rowFilter;
        set
        {
            if (SetProperty(ref _rowFilter, value))
            {
                OnRowFilterChanged(value);
            }
        }
    }

    /// <summary>显示入库（lua）的游戏</summary>
    private bool _showLua = true;

    public bool ShowLua
    {
        get => _showLua;
        set
        {
            if (SetProperty(ref _showLua, value))
            {
                OnShowLuaChanged(value);
            }
        }
    }

    /// <summary>显示客户端认为正版拥有的游戏</summary>
    private bool _showOwned = true;

    public bool ShowOwned
    {
        get => _showOwned;
        set
        {
            if (SetProperty(ref _showOwned, value))
            {
                OnShowOwnedChanged(value);
            }
        }
    }

    private string _gameTitle = "未选择游戏";

    public string GameTitle
    {
        get => _gameTitle;
        set => SetProperty(ref _gameTitle, value);
    }

    private string _progressText = "";

    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    private double _progressValue;

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    private bool _steamRunning;

    public bool SteamRunning
    {
        get => _steamRunning;
        set => SetProperty(ref _steamRunning, value);
    }

    private bool _hasChanges;

    public bool HasChanges
    {
        get => _hasChanges;
        set
        {
            if (SetProperty(ref _hasChanges, value))
            {
                OnHasChangesChanged(value);
            }
        }
    }

    private string _notice = "";

    public string Notice
    {
        get => _notice;
        set
        {
            if (SetProperty(ref _notice, value))
            {
                OnNoticeChanged(value);
            }
        }
    }

    private InfoBarSeverity _noticeSeverity = InfoBarSeverity.Informational;

    public InfoBarSeverity NoticeSeverity
    {
        get => _noticeSeverity;
        set => SetProperty(ref _noticeSeverity, value);
    }

    public AchievementViewModel(
        SteamService steam, LibraryScanner scanner, GameNameCacheService names,
        AchievementStore store, SteamStatsService stats, GameSearchService search, ConfigService config)
    {
        _steam = steam;
        _scanner = scanner;
        _names = names;
        _store = store;
        _stats = stats;
        _search = search;
        _config = config;

        // 勾选状态沿用上次的选择（直接写字段：构造期不触发过滤与保存）
        _showLua = config.Config.AchievementShowLua;
        _showOwned = config.Config.AchievementShowOwned;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        UnlockAllCommand = new RelayCommand(UnlockAll);
        LockAllCommand = new RelayCommand(LockAll);
        SaveToSteamCommand = new AsyncRelayCommand(SaveToSteamAsync);
        ReadFromSteamCommand = new AsyncRelayCommand(ReadFromSteamAsync);
    }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public string DirtyText => HasChanges ? "有未保存的改动" : "";

    private void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));
    private void OnHasChangesChanged(bool value) => OnPropertyChanged(nameof(DirtyText));
    private void OnFilterChanged(string value) => ApplyFilter();
    private void OnRowFilterChanged(string value) => ApplyRowFilter();

    /// <summary>Rows 被整体替换（或增删）时重新过滤——只挂这一处，省得每个赋值点都记得调</summary>
    private void OnRowsChanged(ObservableCollection<AchievementRow> value)
    {
        value.CollectionChanged += (_, _) => ApplyRowFilter();
        ApplyRowFilter();
    }
    private void OnShowLuaChanged(bool value)
    {
        ApplyFilter();
        RememberToggles();
    }

    private void OnShowOwnedChanged(bool value)
    {
        ApplyFilter();
        RememberToggles();
    }

    /// <summary>重扫左侧列表（入库 + 正版）并重读当前游戏</summary>
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _ownedLoaded = false;
            _luaIds.Clear();
            _forceRescan = true;                    // 强制重扫：跳过缓存并重写它
            AchievementListCache.Delete();
            await InitializeAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 只改内存里的配置：退出时由 MainWindow 统一 SaveAsync 落盘（与窗口尺寸、导航栏状态等的机制一致），
    /// 避免每点一次勾选就写一次配置文件
    /// </summary>
    private void RememberToggles()
    {
        _config.Config.AchievementShowLua = ShowLua;
        _config.Config.AchievementShowOwned = ShowOwned;
    }
    private void OnSelectedGameChanged(LibraryItem? value) => _ = LoadGameAsync(value);

    public async Task InitializeAsync()
    {
        SteamRunning = _steam.IsSteamRunning();

        // 缓存命中就直接铺列表（重开秒开，也不起 stats-owned 子进程）；「刷新」按钮会置 _forceRescan 跳过它
        if (!_forceRescan && TryApplyCache())
        {
            ApplyFilter();
            await LoadGameAsync(SelectedGame);
            return;
        }
        _forceRescan = false;

        var items = await _scanner.ScanLibraryAsync();

        _allGames = new List<LibraryItem>();
        foreach (var item in items.Where(i => i.AppId != "N/A"))
        {
            _luaIds.Add(item.AppId);                                   // lua 内容里的第一个 addappid
            foreach (var id in item.InstalledAppIds) _luaIds.Add(id);   // 文件里的其它 addappid（depot/DLC）

            // lua 文件名才是游戏 appid：内容里第一个 addappid 常常是 depot
            // （如 3751950.lua 里先出现 1716751，会把游戏记错 id、还会被当成正版）
            var fileId = Path.GetFileNameWithoutExtension(item.FileName);
            if (fileId.Length > 0 && fileId.All(char.IsDigit))
            {
                _luaIds.Add(fileId);
                item.AppId = fileId;
            }

            item.SourceTag = "lua";
            if (_names.TryGet(item.AppId, out var cached) && !string.IsNullOrWhiteSpace(cached))
                item.GameName = cached;
            _allGames.Add(item);
        }
        ApplyFilter();

        _ = FillNamesAsync();   // 补名（本地 appinfo.vdf 优先）；补到会自动刷新列表

        // 正版列表要连一次 Steam，放后台拿（拿到再刷新列表）
        if (!_ownedLoaded) _ = LoadOwnedAsync();

        // 每次进页面都重读当前游戏（原来的「重试」按钮就是这个）：启动过一次游戏后再回来就能拿到 schema
        await LoadGameAsync(SelectedGame);
    }

    /// <summary>「刷新」按下的这一轮不读缓存（强制重扫并重写缓存）</summary>
    private bool _forceRescan;

    /// <summary>缓存命中就把列表直接铺出来；返回 false = 需要真扫</summary>
    private bool TryApplyCache()
    {
        var snapshot = AchievementListCache.Load();
        if (snapshot == null)
        {
            LogService.Diag("成就列表缓存：没有（首次运行或已损坏）");
            return false;
        }

        var luaDir = _steam.GetEffectiveLuaDir() ?? "";
        if (!AchievementListCache.IsFresh(snapshot, luaDir, _steam.GetSteamPath() ?? ""))
        {
            LogService.Diag("成就列表缓存：已失效（lua 目录或 appinfo.vdf 有变化）");
            return false;
        }

        _allGames = snapshot.LuaGames.Select(ToLibItem).ToList();
        _ownedGames = snapshot.OwnedGames.Select(ToLibItem).ToList();
        _luaIds.Clear();
        foreach (var g in snapshot.LuaGames) _luaIds.Add(g.AppId);

        // 拥有关系来自缓存（快照里没有正版列表时不算——那种情况多半是当时 Steam 没开，得重查）
        _ownedLoaded = snapshot.OwnedGames.Count > 0;
        LogService.Diag($"成就列表缓存：命中（lua {_allGames.Count} / 正版 {_ownedGames.Count}）");
        return true;
    }

    /// <summary>扫描完把结果写回缓存（含两个指纹，供下次判失效）</summary>
    private void SaveListCache()
    {
        var (luaTicks, luaCount) = AchievementListCache.LuaStamp(_steam.GetEffectiveLuaDir() ?? "");
        var (appSize, appTicks) = AchievementListCache.AppInfoStamp(_steam.GetSteamPath() ?? "");

        AchievementListCache.Save(new AchievementListCache.Snapshot
        {
            SavedAt = DateTime.Now,
            LuaDirTicks = luaTicks,
            LuaFileCount = luaCount,
            AppInfoSize = appSize,
            AppInfoTicks = appTicks,
            LuaGames = _allGames.Select(ToCacheItem).ToList(),
            OwnedGames = _ownedGames.Select(ToCacheItem).ToList(),
        });
    }

    private static LibraryItem ToLibItem(AchievementListCache.Item item) =>
        new() { AppId = item.AppId, GameName = item.GameName, SourceTag = item.SourceTag };

    private static AchievementListCache.Item ToCacheItem(LibraryItem item) =>
        new() { AppId = item.AppId, GameName = item.GameName, SourceTag = item.SourceTag };

    /// <summary>
    /// 补名：先本地 appinfo.vdf（离线权威、明文；在线那条路走 store.steampowered.com，这台机器上常不通），
    /// 还缺的再走库页同款的在线批量取名字。故意不用 schema 的 gamename：那可能是开发代号
    /// （实测 3751950 的 gamename 是 OBSIDIAN，实际是刺客信条黑旗记忆重置）。
    /// </summary>
    private async Task FillNamesAsync()
    {
        var nameless = _allGames.Concat(_ownedGames)
            .Where(g => g.GameName.StartsWith("AppID", StringComparison.Ordinal))
            .ToList();
        if (nameless.Count == 0) return;

        // ① 本地 appinfo.vdf（离线、权威）
        var steamPath = _steam.GetSteamPath() ?? "";
        var local = await Task.Run(() => AppInfoVdf.GetAll(steamPath));
        var filled = false;
        foreach (var item in nameless)
        {
            if (local.TryGetValue(item.AppId, out var info) && info.Name.Length > 0)
            {
                item.GameName = info.Name;
                filled = true;
            }
        }
        if (filled) ApplyFilter();

        // ② 还缺的走在线补名（能补多少算多少）
        var ids = _allGames.Concat(_ownedGames)
            .Where(g => g.GameName.StartsWith("AppID", StringComparison.Ordinal))
            .Select(g => g.AppId)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return;

        try { await _search.GetGameNamesBatchAsync(ids); }
        catch (Exception ex) { LogService.Diag($"成就页补名失败: {ex.Message}"); }

        var changed = false;
        foreach (var item in _allGames.Concat(_ownedGames))
        {
            if (_names.TryGet(item.AppId, out var name) && !string.IsNullOrWhiteSpace(name) && item.GameName != name)
            {
                item.GameName = name;
                changed = true;
            }
        }
        if (changed) ApplyFilter();
    }

    /// <summary>
    /// 正版（客户端认为拥有的）游戏：候选 = 本地有成就定义的 appid ∪ 已安装的 appid，
    /// 再用子进程批量问一次"哪些是拥有的"（不请求统计、不改任何东西）。
    /// </summary>
    private async Task LoadOwnedAsync()
    {
        var steamPath = _steam.GetSteamPath();
        if (string.IsNullOrEmpty(steamPath) || !SteamRunning) return;   // 不标记已加载，下次进页面再试
        _ownedLoaded = true;

        var lua = _luaIds;

        // 收集候选（要解析 schema 取名字 + 读 appmanifest）→ 放后台线程，别卡 UI
        var candidates = await Task.Run(() =>
        {
            var map = new Dictionary<string, LibraryItem>(StringComparer.Ordinal);

            var all = AppInfoVdf.GetAll(steamPath);   // appid → (类型, 名字)

            // ① 客户端认识的游戏——类型白名单同 Fluent-Steam-Lua：
            //    只有 Game/Demo/Mod 算游戏，SDK、工具、Redistributable 之类不进来
            foreach (var (id, info) in all)
            {
                if (lua.Contains(id) || !GameTypes.Contains(info.Type)) continue;
                map[id] = new LibraryItem
                {
                    AppId = id,
                    GameName = info.Name.Length > 0 ? info.Name : $"AppID {id}",
                    SourceTag = "正版",
                };
            }

            // ② 已安装、但 appinfo 不认识的（极少见）：名字取 appmanifest。
            //    appinfo 认识的一律走 ①，否则工具/运行库会从这条路漏进来（228980 就是这么进来的）
            try
            {
                foreach (var root in SteamLibraryRoots(steamPath))
                foreach (var file in Directory.GetFiles(Path.Combine(root, "steamapps"), "appmanifest_*.acf"))
                {
                    var text = File.ReadAllText(file);
                    var idMatch = Regex.Match(text, "\"appid\"\\s*\"(\\d+)\"");
                    if (!idMatch.Success) continue;
                    var id = idMatch.Groups[1].Value;
                    if (lua.Contains(id) || all.ContainsKey(id)) continue;
                    var nameMatch = Regex.Match(text, "\"name\"\\s*\"([^\"]*)\"");
                    map[id] = new LibraryItem
                    {
                        AppId = id,
                        GameName = nameMatch.Success && nameMatch.Groups[1].Value.Length > 0
                            ? nameMatch.Groups[1].Value
                            : $"AppID {id}",
                        SourceTag = "正版",
                    };
                }
            }
            catch (Exception ex) { LogService.Diag($"正版候选②失败: {ex.Message}"); }

            return map;
        });

        if (candidates.Count == 0) return;

        var owned = await _stats.OwnedAppsAsync(candidates.Keys.ToList());
        _ownedGames = candidates.Where(kv => owned.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        foreach (var g in _ownedGames)
        {
            if (_names.TryGet(g.AppId, out var name) && !string.IsNullOrWhiteSpace(name))
                g.GameName = name;
        }
        LogService.Event($"正版游戏：候选={candidates.Count} 拥有={_ownedGames.Count}");
        SaveListCache();   // 只有走到这里才写缓存（含正版列表；Steam 没开时不写，免得缓存一份空的正版列表）
        ApplyFilter();
        _ = FillNamesAsync();
    }

    /// <summary>Steam 库根目录（含 libraryfolders.vdf 里记录的其他盘）</summary>
    private static List<string> SteamLibraryRoots(string steamPath)
    {
        var roots = new List<string> { steamPath };
        try
        {
            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!roots.Contains(p, StringComparer.OrdinalIgnoreCase)) roots.Add(p);
                }
            }
        }
        catch { }
        return roots;
    }

    private void ApplyRowFilter()
    {
        var q = RowFilter?.Trim() ?? "";
        VisibleRows = string.IsNullOrEmpty(q)
            ? new ObservableCollection<AchievementRow>(Rows)
            : new ObservableCollection<AchievementRow>(Rows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private void ApplyFilter()
    {
        var q = Filter?.Trim() ?? "";
        var keep = SelectedGame;

        var merged = new List<LibraryItem>();
        if (ShowLua) merged.AddRange(_allGames);
        if (ShowOwned)
        {
            var have = merged.Select(g => g.AppId).ToHashSet(StringComparer.Ordinal);
            merged.AddRange(_ownedGames.Where(g => !have.Contains(g.AppId)));
        }

        var list = string.IsNullOrEmpty(q)
            ? merged
            : merged.Where(g => g.AppId.Contains(q, StringComparison.OrdinalIgnoreCase)
                                || g.GameName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Games = new ObservableCollection<LibraryItem>(list);
        if (keep != null && Games.Contains(keep)) SelectedGame = keep;
    }

    private async Task LoadGameAsync(LibraryItem? game)
    {
        _suppress = true;
        try
        {
            foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
            Rows = new ObservableCollection<AchievementRow>();
            _baseline = new Dictionary<string, bool>();
            HasChanges = false;
            _steamId = "";
            Notice = "";

            if (game == null)
            {
                GameTitle = "未选择游戏";
                SetProgress(0, 0);
                return;
            }

            GameTitle = game.GameName;

            if (!SteamStatsSchema.TryLoad(_steam.GetSteamPath() ?? "", game.AppId, out var defs, out var schemaError))
            {
                SetProgress(0, 0);
                Notice = $"未发现成就定义，错误码：{schemaError}";
                NoticeSeverity = InfoBarSeverity.Warning;
                return;
            }

            var file = _store.Load(game.AppId);
            _steamId = file?.SteamId ?? "";
            var saved = file?.Achievements.ToDictionary(a => a.Name, a => a) ?? new Dictionary<string, AchievementRecord>();

            var rows = new List<AchievementRow>(defs.Count);
            foreach (var def in defs)
            {
                saved.TryGetValue(def.Name, out var rec);
                var row = new AchievementRow(def, rec?.Achieved ?? false, rec?.UnlockTime ?? 0);
                row.PropertyChanged += OnRowChanged;
                rows.Add(row);
            }
            Rows = new ObservableCollection<AchievementRow>(rows);
            _baseline = rows.ToDictionary(r => r.Name, r => r.Achieved);
            SetProgress(rows.Count(r => r.Achieved), rows.Count);
        }
        finally
        {
            _suppress = false;
        }
        await Task.CompletedTask;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppress || e.PropertyName != nameof(AchievementRow.Achieved)) return;

        HasChanges = Rows.Any(r => _baseline.TryGetValue(r.Name, out var b) ? b != r.Achieved : r.Achieved);
        SetProgress(Rows.Count(r => r.Achieved), Rows.Count);
        SaveStore("local");   // 勾一次写一次，文件很小
    }

    private void SetProgress(int unlocked, int total)
    {
        ProgressValue = total > 0 ? (double)unlocked / total : 0;
        ProgressText = total > 0 ? $"已解锁 {unlocked}/{total}" : "";
    }

    private void SaveStore(string source)
    {
        if (SelectedGame == null || Rows.Count == 0) return;
        _store.Save(new AchievementFile
        {
            AppId = SelectedGame.AppId,
            SteamId = _steamId,
            Source = source,
            Achievements = Rows.Select(r => new AchievementRecord
            {
                Name = r.Name,
                Achieved = r.Achieved,
                UnlockTime = r.UnlockTime,
            }).ToList(),
        });
    }

    /// <summary>把子进程回读的状态套回列表（并重置"未保存"基线）</summary>
    private void MergeFromResult(StatsChildResult result)
    {
        _suppress = true;
        try
        {
            if (!string.IsNullOrEmpty(result.SteamId)) _steamId = result.SteamId;
            var byName = result.Achievements.ToDictionary(a => a.Name, a => a);
            foreach (var row in Rows)
            {
                if (!byName.TryGetValue(row.Name, out var rec)) continue;
                row.Achieved = rec.Achieved;
                row.UnlockTime = rec.UnlockTime;
            }
            _baseline = Rows.ToDictionary(r => r.Name, r => r.Achieved);
            HasChanges = false;
            SetProgress(Rows.Count(r => r.Achieved), Rows.Count);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void UnlockAll()
    {
        _suppress = true;
        foreach (var row in Rows) row.Achieved = true;
        _suppress = false;
        AfterBulkChange();
    }

    private void LockAll()
    {
        _suppress = true;
        foreach (var row in Rows) row.Achieved = false;
        _suppress = false;
        AfterBulkChange();
    }

    private void AfterBulkChange()
    {
        HasChanges = Rows.Any(r => _baseline.TryGetValue(r.Name, out var b) ? b != r.Achieved : r.Achieved);
        SetProgress(Rows.Count(r => r.Achieved), Rows.Count);
        SaveStore("local");
    }

    private async Task SaveToSteamAsync()
    {
        if (SelectedGame == null || IsBusy) return;
        if (!SteamRunning)
        {
            (App.MainWindow as MainWindow)?.Notify("成就", "Steam 未运行", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
            return;
        }

        // 不做"有没有改动"的判断：每次都把当前状态整体写一遍（幂等，也省得改动判断本身出岔子）
        var changes = Rows
            .Select(r => new AchievementRecord { Name = r.Name, Achieved = r.Achieved })
            .ToList();

        IsBusy = true;
        try
        {
            var result = await _stats.ApplyAsync(SelectedGame.AppId, changes);
            if (result is { Ok: true })
            {
                // 客户端一条状态都没读回来（比如成就根本设不上）→ 别拿它覆盖界面与留底
                if (result.ReadOk > 0)
                {
                    MergeFromResult(result);
                    SaveStore("steam");
                }

                if (result.Failed > 0)
                {
                    // 不报条数：设不上的游戏通常是"全都没设上"，报出来恒等于总数，没有信息量
                    ToastService.ShowError("成就未全部写入 Steam", result.Warning);
                }
                else if (!string.IsNullOrEmpty(result.Warning))
                {
                    ToastService.ShowWarning("成就已写入 Steam", result.Warning);
                }
                else
                {
                    ToastService.ShowSuccess("成就已写入 Steam", "");
                }
            }
            else
            {
                ToastService.ShowError("写入 Steam 失败", result?.Message ?? "子进程无结果");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReadFromSteamAsync()
    {
        if (SelectedGame == null || IsBusy) return;
        if (!SteamRunning)
        {
            (App.MainWindow as MainWindow)?.Notify("成就", "Steam 未运行", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _stats.DumpAsync(SelectedGame.AppId);
            if (result is { Ok: true })
            {
                MergeFromResult(result);
                SaveStore("steam");
                if (result.ReadOk == 0)
                {
                    // 客户端手里没有这个游戏的成就数据（不是"全部未解锁"）——别拿它覆盖留底
                    ToastService.ShowWarning("从 Steam 读取", "客户端里没有这个游戏的成就数据，已保留本地留底");
                    return;
                }
                MergeFromResult(result);
                SaveStore("steam");
            }
            else
            {
                ToastService.ShowError("从 Steam 读取失败", result?.Message ?? "子进程无结果");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
