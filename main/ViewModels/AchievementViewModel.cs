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

    [ObservableProperty] private bool _achieved;
    [ObservableProperty] private long _unlockTime;

    partial void OnAchievedChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(UnlockText));
    }

    partial void OnUnlockTimeChanged(long value) => OnPropertyChanged(nameof(UnlockText));
}

/// <summary>
/// 成就编辑：左边挑入库游戏，右边勾成就。勾选只改**本地留底**
/// （%LOCALAPPDATA%\OSTGUI\achievements\&lt;appid&gt;.json），点「保存到 Steam」才写回客户端。
/// 假入库游戏的 Steam 端状态本来就会被服务端/内核清空，留底才是唯一可靠副本。
/// </summary>
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

    [ObservableProperty] private ObservableCollection<LibraryItem> _games = new();
    [ObservableProperty] private LibraryItem? _selectedGame;
    [ObservableProperty] private ObservableCollection<AchievementRow> _rows = new();
    /// <summary>列表实际显示的成就（= Rows 经过 RowFilter 过滤；保存仍按 Rows 走）</summary>
    [ObservableProperty] private ObservableCollection<AchievementRow> _visibleRows = new();
    [ObservableProperty] private string _filter = "";
    /// <summary>成就列表的过滤词</summary>
    [ObservableProperty] private string _rowFilter = "";
    /// <summary>显示入库（lua）的游戏</summary>
    [ObservableProperty] private bool _showLua = true;
    /// <summary>显示客户端认为正版拥有的游戏</summary>
    [ObservableProperty] private bool _showOwned = true;
    [ObservableProperty] private string _gameTitle = "未选择游戏";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _steamRunning;
    [ObservableProperty] private bool _hasChanges;
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private InfoBarSeverity _noticeSeverity = InfoBarSeverity.Informational;

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
    }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public string DirtyText => HasChanges ? "有未保存的改动" : "";

    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnHasChangesChanged(bool value) => OnPropertyChanged(nameof(DirtyText));
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnRowFilterChanged(string value) => ApplyRowFilter();

    /// <summary>Rows 被整体替换（或增删）时重新过滤——只挂这一处，省得每个赋值点都记得调</summary>
    partial void OnRowsChanged(ObservableCollection<AchievementRow> value)
    {
        value.CollectionChanged += (_, _) => ApplyRowFilter();
        ApplyRowFilter();
    }
    partial void OnShowLuaChanged(bool value)
    {
        ApplyFilter();
        _ = SaveTogglesAsync();
    }

    partial void OnShowOwnedChanged(bool value)
    {
        ApplyFilter();
        _ = SaveTogglesAsync();
    }

    /// <summary>重扫左侧列表（入库 + 正版）并重读当前游戏</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _ownedLoaded = false;
            _luaIds.Clear();
            await InitializeAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>记住两个来源勾选，下次进页面沿用</summary>
    private async Task SaveTogglesAsync()
    {
        try
        {
            await _config.UpdateAndSaveAsync(c =>
            {
                c.AchievementShowLua = ShowLua;
                c.AchievementShowOwned = ShowOwned;
            });
        }
        catch (Exception ex) { LogService.AddAppLog($"成就页记住勾选失败: {ex.Message}"); }
    }
    partial void OnSelectedGameChanged(LibraryItem? value) => _ = LoadGameAsync(value);

    public async Task InitializeAsync()
    {
        SteamRunning = _steam.IsSteamRunning();
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
        catch (Exception ex) { LogService.AddAppLog($"成就页补名失败: {ex.Message}"); }

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
            catch (Exception ex) { LogService.AddAppLog($"正版候选②失败: {ex.Message}"); }

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
        LogService.AddAppLog($"正版游戏：候选={candidates.Count} 拥有={_ownedGames.Count}");
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

    [RelayCommand]
    private void UnlockAll()
    {
        _suppress = true;
        foreach (var row in Rows) row.Achieved = true;
        _suppress = false;
        AfterBulkChange();
    }

    [RelayCommand]
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

    [RelayCommand]
    private async Task SaveToSteamAsync()
    {
        if (SelectedGame == null || IsBusy) return;
        if (!SteamRunning)
        {
            ToastService.ShowWarning("成就", "Steam 没在运行");
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

    [RelayCommand]
    private async Task ReadFromSteamAsync()
    {
        if (SelectedGame == null || IsBusy) return;
        if (!SteamRunning)
        {
            ToastService.ShowWarning("成就", "Steam 没在运行");
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
