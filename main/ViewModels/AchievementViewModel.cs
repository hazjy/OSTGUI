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

    private List<LibraryItem> _allGames = new();
    private List<LibraryItem> _ownedGames = new();
    private Dictionary<string, bool> _baseline = new();
    private string _steamId = "";
    private bool _suppress;
    private bool _ownedLoaded;

    [ObservableProperty] private ObservableCollection<LibraryItem> _games = new();
    [ObservableProperty] private LibraryItem? _selectedGame;
    [ObservableProperty] private ObservableCollection<AchievementRow> _rows = new();
    [ObservableProperty] private string _filter = "";
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
        AchievementStore store, SteamStatsService stats)
    {
        _steam = steam;
        _scanner = scanner;
        _names = names;
        _store = store;
        _stats = stats;
    }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public string DirtyText => HasChanges ? "有未保存的改动" : "";

    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnHasChangesChanged(bool value) => OnPropertyChanged(nameof(DirtyText));
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnShowLuaChanged(bool value) => ApplyFilter();
    partial void OnShowOwnedChanged(bool value) => ApplyFilter();
    partial void OnSelectedGameChanged(LibraryItem? value) => _ = LoadGameAsync(value);

    public async Task InitializeAsync()
    {
        SteamRunning = _steam.IsSteamRunning();
        var items = await _scanner.ScanLibraryAsync();
        _allGames = items.Where(i => i.AppId != "N/A").ToList();
        foreach (var item in _allGames)
        {
            item.SourceTag = "lua";
            if (_names.TryGet(item.AppId, out var name) && !string.IsNullOrWhiteSpace(name))
                item.GameName = name;
            else if (SteamStatsSchema.ReadGameName(_steam.GetSteamPath() ?? "", item.AppId) is { } schemaName)
                item.GameName = schemaName;   // schema 里自带 gamename，省得显示成 "AppID xxx"
        }
        ApplyFilter();

        // 正版列表要连一次 Steam，放后台拿（拿到再刷新列表）
        if (!_ownedLoaded) _ = LoadOwnedAsync();

        // 每次进页面都重读当前游戏（原来的「重试」按钮就是这个）：启动过一次游戏后再回来就能拿到 schema
        await LoadGameAsync(SelectedGame);
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

        var lua = _allGames.Select(g => g.AppId).ToHashSet(StringComparer.Ordinal);

        // 收集候选（要解析 schema 取名字 + 读 appmanifest）→ 放后台线程，别卡 UI
        var candidates = await Task.Run(() =>
        {
            var map = new Dictionary<string, LibraryItem>(StringComparer.Ordinal);

            // ① 本地已有成就定义的游戏（名字就在 schema 的 gamename 里，不用联网）
            try
            {
                foreach (var file in Directory.GetFiles(
                             Path.Combine(steamPath, "appcache", "stats"), "UserGameStatsSchema_*.bin"))
                {
                    var id = Path.GetFileNameWithoutExtension(file)["UserGameStatsSchema_".Length..];
                    if (id.Length == 0 || lua.Contains(id)) continue;
                    map[id] = new LibraryItem
                    {
                        AppId = id,
                        GameName = SteamStatsSchema.ReadGameName(steamPath, id) ?? $"AppID {id}",
                        SourceTag = "正版",
                    };
                }
            }
            catch (Exception ex) { LogService.AddAppLog($"正版候选①失败: {ex.Message}"); }

            // ② 已安装的游戏（appmanifest_<appid>.acf，顺带取游戏名）
            try
            {
                foreach (var root in SteamLibraryRoots(steamPath))
                foreach (var file in Directory.GetFiles(Path.Combine(root, "steamapps"), "appmanifest_*.acf"))
                {
                    var text = File.ReadAllText(file);
                    var idMatch = Regex.Match(text, "\"appid\"\\s*\"(\\d+)\"");
                    if (!idMatch.Success) continue;
                    var id = idMatch.Groups[1].Value;
                    if (lua.Contains(id)) continue;
                    var nameMatch = Regex.Match(text, "\"name\"\\s*\"([^\"]*)\"");
                    map[id] = new LibraryItem
                    {
                        AppId = id,
                        GameName = nameMatch.Success && nameMatch.Groups[1].Value.Length > 0
                            ? nameMatch.Groups[1].Value
                            : SteamStatsSchema.ReadGameName(steamPath, id) ?? $"AppID {id}",
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
