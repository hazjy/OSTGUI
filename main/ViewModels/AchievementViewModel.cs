using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Models;
using OSTGUI.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;

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
    private Dictionary<string, bool> _baseline = new();
    private string _steamId = "";
    private bool _suppress;
    private bool _initialized;

    [ObservableProperty] private ObservableCollection<LibraryItem> _games = new();
    [ObservableProperty] private LibraryItem? _selectedGame;
    [ObservableProperty] private ObservableCollection<AchievementRow> _rows = new();
    [ObservableProperty] private string _filter = "";
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
    partial void OnSelectedGameChanged(LibraryItem? value) => _ = LoadGameAsync(value);

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        SteamRunning = _steam.IsSteamRunning();
        var items = await _scanner.ScanLibraryAsync();
        _allGames = items.Where(i => i.AppId != "N/A").ToList();
        foreach (var item in _allGames)
        {
            if (_names.TryGet(item.AppId, out var name) && !string.IsNullOrWhiteSpace(name))
                item.GameName = name;
        }
        ApplyFilter();
        RefreshNotice();
    }

    private void ApplyFilter()
    {
        var q = Filter?.Trim() ?? "";
        var keep = SelectedGame;
        var list = string.IsNullOrEmpty(q)
            ? _allGames
            : _allGames.Where(g => g.AppId.Contains(q, StringComparison.OrdinalIgnoreCase)
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

            if (game == null)
            {
                GameTitle = "未选择游戏";
                SetProgress(0, 0);
                RefreshNotice();
                return;
            }

            GameTitle = game.GameName;

            var defs = SteamStatsSchema.Load(_steam.GetSteamPath() ?? "", game.AppId);
            if (defs == null)
            {
                SetProgress(0, 0);
                Notice = $"未找到成就定义：先在 Steam 里启动一次这个游戏让客户端缓存成就列表，再点「重试」。\n期望文件：{SteamStatsSchema.PathFor(_steam.GetSteamPath() ?? "", game.AppId)}";
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
        RefreshNotice();
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

    private void RefreshNotice()
    {
        if (SelectedGame != null && Rows.Count == 0) return;   // 已有更具体的提示
        if (!SteamRunning)
        {
            Notice = "Steam 未运行：现在只能编辑本地留底，「保存到 Steam」与「从 Steam 读取」不可用。";
            NoticeSeverity = InfoBarSeverity.Warning;
            return;
        }
        if (SelectedGame == null)
        {
            Notice = "从左侧选一个游戏开始编辑。";
            NoticeSeverity = InfoBarSeverity.Informational;
            return;
        }
        var extra = string.IsNullOrEmpty(_steamId) ? "" : $"　·　留底来自账号 {_steamId}";
        Notice = $"改动会立即存进本地留底；入库游戏的 Steam 端状态服务端不认，要重启后仍在需要 CloudRedirect（下一步做）。{extra}";
        NoticeSeverity = InfoBarSeverity.Informational;
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

        var changes = Rows
            .Where(r => _baseline.TryGetValue(r.Name, out var b) ? b != r.Achieved : r.Achieved)
            .Select(r => new AchievementRecord { Name = r.Name, Achieved = r.Achieved })
            .ToList();
        if (changes.Count == 0)
        {
            ToastService.ShowInfo("成就", "没有需要保存的改动");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _stats.ApplyAsync(SelectedGame.AppId, changes);
            if (result is { Ok: true })
            {
                MergeFromResult(result);
                SaveStore("steam");
                var tail = string.IsNullOrEmpty(result.Warning) ? "" : $"（{result.Warning}）";
                ToastService.ShowSuccess("成就已写入 Steam", $"{result.Changed} 项{tail}");
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
                if (!result.StatsReady) ToastService.ShowWarning("从 Steam 读取", "没等到成就数据回调，读到的是客户端当前状态");
                else ToastService.ShowSuccess("从 Steam 读取", $"{result.Achievements.Count(a => a.Achieved)} 项已解锁");
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

    [RelayCommand]
    private Task ReloadAsync() => LoadGameAsync(SelectedGame);
}
