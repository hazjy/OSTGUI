using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OSTGUI.Models;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// 修改器页：搜索 / 已下载两个列表 + 下载、启动、删除 + 「进程绑定」
/// （把修改器绑到某个游戏，游戏一跑就自动启动它，见 Services/TrainerMonitor.cs）。
/// </summary>
public partial class TrainerViewModel : ObservableObject
{
    private readonly TrainerCatalogService _catalog;
    private readonly TrainerDownloadService _downloads;
    private readonly TrainerBindingService _bindingService;
    private readonly ConfigService _config;

    private bool _initialized;
    private bool _loadingConfig;
    /// <summary>搜索页是否已抓过：切回来不重复抓（抓一次就够，除非改了查询词或手动点搜索）</summary>
    private bool _searchLoaded;
    private string _loadedQuery = "";

    public ObservableCollection<TrainerInfo> Items { get; } = new();
    public ObservableCollection<TrainerBinding> Bindings { get; } = new();

    /// <summary>已下载的修改器（绑定对话框的候选）</summary>
    public ObservableCollection<TrainerInfo> LocalTrainers { get; } = new();

    /// <summary>
    /// 「已下载」视图**自己的**列表（含本地过滤结果）。
    /// 刻意不复用搜索用的 <see cref="Items"/>：两边共用一个集合时，搜索的网络请求晚回来
    /// 会把搜索结果糊到"已下载"上（2026-09-25 实测踩到）。
    /// </summary>
    public ObservableCollection<TrainerInfo> LocalItems { get; } = new();

    [ObservableProperty] private string _query = "";
    /// <summary>「已下载」视图的本地过滤词（只筛本地，不发请求；与网页搜索的 Query 各管一摊）</summary>
    [ObservableProperty] private string _localFilter = "";
    [ObservableProperty] private int _viewIndex;          // 0 搜索 / 1 已下载
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _monitorStatus = "";

    /// <summary>监控开关：开了 GUI 退出后仍按绑定工作（默认关）</summary>
    [ObservableProperty] private bool _monitorEnabled;

    /// <summary>当前下载目录（显示用；改目录见 SetDownloadDir）</summary>
    [ObservableProperty] private string _downloadDirText = "";

    /// <summary>改下载目录：只改内存（退出时统一落盘），立刻生效并刷新已下载列表</summary>
    public void SetDownloadDir(string? dir)
    {
        var value = string.IsNullOrWhiteSpace(dir) ? "" : dir.Trim();
        _config.Update(c => c.TrainerDownloadDir = value);
        UpdateDownloadDirText();
        RefreshLocalTrainers();
        if (ViewIndex == 1) _ = LoadViewAsync();
        ToastService.ShowSuccess("下载目录已切换", DownloadDirText);
    }

    private void UpdateDownloadDirText() =>
        DownloadDirText = "下载目录：" + _downloads.Dir;

    public TrainerViewModel(
        TrainerCatalogService catalog, TrainerDownloadService downloads, TrainerBindingService bindingService,
        ConfigService config)
    {
        _catalog = catalog;
        _downloads = downloads;
        _bindingService = bindingService;
        _config = config;

        _loadingConfig = true;
        _monitorEnabled = config.Config.TrainerMonitorEnabled;
        _loadingConfig = false;
    }

    partial void OnViewIndexChanged(int value) => _ = LoadViewAsync();
    partial void OnLocalFilterChanged(string value) => ApplyLocalFilter();
    partial void OnQueryChanged(string value) => _searchLoaded = false;   // 查询词变了，旧结果作废
    partial void OnMonitorEnabledChanged(bool value)
    {
        if (!_loadingConfig) _config.Update(c => c.TrainerMonitorEnabled = value);   // 只改内存，退出时统一落盘
        ApplyMonitorState();
    }

    /// <summary>每次进页面：刷新已下载、绑定与监控状态（列表按当前视图按需拉）</summary>
    public async Task InitializeAsync()
    {
        RefreshLocalTrainers();
        ReloadBindings();
        ApplyMonitorState();
        UpdateDownloadDirText();

        if (!_initialized)
        {
            _initialized = true;
            await LoadViewAsync();
        }
    }

    public async Task LoadViewAsync(bool force = false)
    {
        if (IsBusy) return;

        if (ViewIndex == 1)
        {
            RefreshLocalTrainers();   // 走索引，只动 LocalItems（不碰搜索的 Items）
            return;
        }

        // 已经搜过同样的词就直接用现成结果（切页面不该重新联网抓一次）
        if (_searchLoaded && !force)
        {
            StatusText = SearchStatusText();
            return;
        }

        IsBusy = true;
        try
        {
            List<TrainerInfo> list;
            try
            {
                // 只有两个视图：0 = 搜索，1 = 已下载（已在上面的分支里处理）
                list = ViewIndex == 0 ? await _catalog.SearchAsync(Query) : new List<TrainerInfo>();
            }
            catch (Exception ex)
            {
                // 抓取失败（站点/网络不可用）不能让异常冒出去：这里是 fire-and-forget 调用
                LogService.AddAppLog($"trainer 列表加载失败: {ex.Message}");
                Items.Clear();
                StatusText = "抓取失败：网络或站点不可用（详见日志）";
                return;
            }

            // 抓取期间用户可能已经切到别的视图：迟到的结果不许再写状态（集合本来就分开了）
            if (ViewIndex != 0) return;

            Items.Clear();
            foreach (var t in MarkDownloaded(list)) Items.Add(t);

            _searchLoaded = true;
            _loadedQuery = Query.Trim();
            StatusText = SearchStatusText();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>搜索视图的状态栏文案（含"没找到"——页面真结果 0 条时的正常情况，不是故障）</summary>
    private string SearchStatusText()
    {
        if (_loadedQuery.Length == 0) return "输入游戏名后点搜索";
        if (Items.Count == 0) return $"没找到「{_loadedQuery}」对应的修改器（站点上确实有却搜不到时，可能是站点改版，详见日志）";
        return $"搜索「{_loadedQuery}」{Items.Count} 条";
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchLoaded = false;                        // 点搜索 = 明确要求重搜
        if (ViewIndex == 0) await LoadViewAsync(force: true);
        else ViewIndex = 0;                           // 从「已下载」切回搜索视图
    }

    [RelayCommand]
    private async Task DownloadAsync(TrainerInfo? trainer)
    {
        if (trainer == null || trainer.IsDownloading) return;

        IsBusy = true;
        trainer.IsDownloading = true;
        trainer.DownloadProgress = 0;
        try
        {
            var download = await _catalog.GetDownloadAsync(trainer.PageUrl);
            if (download == null)
            {
                ToastService.ShowError("下载失败", "详情页里没找到附件链接（站点结构可能已变）");
                return;
            }

            // 进度改显示在状态栏（行里不再放进度条：搜索结果行要和已下载行长得一样）
            var progress = new Progress<double>(p =>
            {
                trainer.DownloadProgress = p;
                StatusText = $"正在下载 {trainer.GameName} {p:0}%";
            });
            var (path, error) = await _downloads.DownloadAsync(
                download.Value.Url, download.Value.FileName, trainer.PageUrl, progress);
            if (path == null)
            {
                ToastService.ShowError("下载失败", error.Length > 0 ? error : "详见日志");
                return;
            }

            trainer.LocalPath = path;
            RefreshLocalTrainers();
            if (ViewIndex == 0) StatusText = SearchStatusText();   // 把"正在下载 x%"换回搜索结果说明
            ToastService.ShowSuccess("下载完成", Path.GetFileName(path));
        }
        finally
        {
            trainer.IsDownloading = false;
            IsBusy = false;
        }
    }

    /// <summary>启动修改器：只有用户点了才运行（下载完不自动执行）</summary>
    [RelayCommand]
    private void Launch(TrainerInfo? trainer)
    {
        var exe = trainer?.LocalPath ?? "";
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            ToastService.ShowWarning("无法启动", "还没下载这个修改器");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? _downloads.Dir,
                UseShellExecute = true,
            });

            LogService.AddAppLog($"trainer 手动启动 {Path.GetFileName(exe)}");
            ToastService.ShowSuccess("已启动", Path.GetFileName(exe));
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 启动失败 {exe}: {ex.Message}");
            ToastService.ShowError("启动失败", ex.Message);
        }
    }

    /// <summary>
    /// 更新已下载的修改器：取**最新附件**下载覆盖，并同步更新索引（绑定记的是名称，无需改写）。
    /// 找文章优先用条目里存的详情页；没有（早先下载的记录）就用**官方 RSS** 按名字搜出来——
    /// 但文件直链官方只在文章页里给（RSS 没有 enclosure、正文也不含附件表），只能从那一页取一次。
    /// </summary>
    [RelayCommand]
    private async Task UpdateAsync(TrainerInfo? trainer)
    {
        if (trainer == null || IsBusy) return;

        var oldPath = trainer.LocalPath;
        IsBusy = true;
        try
        {
            StatusText = $"正在检查更新 {trainer.GameName}…";
            var page = trainer.PageUrl;
            if (string.IsNullOrEmpty(page))
            {
                StatusText = $"正在用官方 RSS 找 {trainer.GameName} 的文章…";
                page = await FindPageUrlAsync(trainer.GameName);
                if (string.IsNullOrEmpty(page))
                {
                    StatusText = "更新失败：RSS 里没找到这个修改器";
                    ToastService.ShowWarning("找不到来源", $"官方 RSS 里没搜到「{trainer.GameName}」");
                    return;
                }
            }

            var latest = await _catalog.GetDownloadAsync(page);
            if (latest == null)
            {
                StatusText = "更新失败：文章页里没找到附件";
                ToastService.ShowError("更新失败", "文章页里没找到附件链接（站点结构可能已变）");
                return;
            }

            if (string.Equals(Path.GetFileNameWithoutExtension(oldPath),
                    Path.GetFileNameWithoutExtension(latest.Value.FileName), StringComparison.OrdinalIgnoreCase))
            {
                StatusText = $"已是最新：{trainer.GameName}";
                ToastService.ShowInfo("已是最新", trainer.GameName);
                return;
            }

            var progress = new Progress<double>(p => StatusText = $"正在更新 {trainer.GameName} {p:0}%");
            var (path, error) = await _downloads.DownloadAsync(latest.Value.Url, latest.Value.FileName, page, progress);

            if (path == null)
            {
                // 失败时旧文件原样不动（新文件先落盘、成功后才动旧引用）
                StatusText = "更新失败：网络或站点异常（详见日志）";
                ToastService.ShowError("更新失败", error.Length > 0 ? error : "详见日志");
                return;
            }

            _downloads.CommitUpdate(oldPath, path, Path.GetFileNameWithoutExtension(latest.Value.FileName),
                page, latest.Value.Url);
            RefreshLocalTrainers();
            ReloadBindings();              // 名称→路径 的解析结果可能变了，列表跟着刷新
            StatusText = $"已更新：{trainer.GameName} → {Path.GetFileName(path)}";
            ToastService.ShowSuccess("更新完成", Path.GetFileName(path));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>用官方 RSS 按名字找文章页（本地名形如 Crimson.Desert.Enhanced.v1.0-v2.0x.Plus.12.Trainer-FLiNG）</summary>
    private async Task<string?> FindPageUrlAsync(string trainerName)
    {
        var query = Regex.Replace(trainerName, @"\.v\d.*$", "");           // 砍掉版本尾巴
        query = Regex.Replace(query, @"[-_.]+", " ").Replace("FLiNG", "").Trim();
        if (query.Length == 0) return null;

        var hits = await _catalog.SearchAsync(query);
        var target = Norm(query);
        return (hits.FirstOrDefault(h => Norm(h.GameName) == target) ?? hits.FirstOrDefault())?.PageUrl;
    }

    /// <summary>比对用归一化：只留字母数字（大小写不敏感）</summary>
    private static string Norm(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [RelayCommand]
    private void Reveal(TrainerInfo? trainer)
    {
        if (trainer != null) _downloads.RevealInExplorer(trainer.LocalPath);
    }

    [RelayCommand]
    private void Delete(TrainerInfo? trainer)
    {
        if (trainer == null || string.IsNullOrEmpty(trainer.LocalPath)) return;

        _downloads.Delete(trainer.LocalPath);
        trainer.LocalPath = "";
        RefreshLocalTrainers();
        if (ViewIndex == 1) _ = LoadViewAsync();
        ToastService.ShowSuccess("已删除", trainer.GameName);
    }

    // ── 绑定 ────────────────────────────────────────────────────────────────

    private void ReloadBindings()
    {
        Bindings.Clear();
        foreach (var b in _bindingService.Load()) Bindings.Add(b);
        UpdateMonitorStatus();
    }

    public void SaveBindings()
    {
        _bindingService.Save(Bindings.ToList());
        UpdateMonitorStatus();
        ApplyMonitorState();
    }

    /// <summary>绑定对话框点确定后调用：同一个修改器只留一条绑定（按**名称**去重）</summary>
    public void AddOrUpdateBinding(string trainerName, string gameExe, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(trainerName) || string.IsNullOrWhiteSpace(gameExe)) return;

        var same = Bindings.FirstOrDefault(b =>
            string.Equals(b.TrainerName, trainerName, StringComparison.OrdinalIgnoreCase));
        if (same != null) Bindings.Remove(same);

        Bindings.Add(new TrainerBinding
        {
            TrainerName = trainerName,
            GameName = Path.GetFileNameWithoutExtension(gameExe),
            GameExePath = gameExe,
            IsEnabled = enabled,
        });
        SaveBindings();
        ToastService.ShowSuccess("已绑定", $"{Path.GetFileNameWithoutExtension(gameExe)} → {trainerName}");
    }

    [RelayCommand]
    private void RemoveBinding(TrainerBinding? binding)
    {
        if (binding == null) return;
        Bindings.Remove(binding);
        SaveBindings();
        ToastService.ShowSuccess("已删除绑定", binding.GameName);
    }

    /// <summary>绑定行的启用勾选</summary>
    public void SetBindingEnabled(TrainerBinding binding, bool enabled)
    {
        binding.IsEnabled = enabled;
        SaveBindings();
    }

    // ── 监控子进程 ──────────────────────────────────────────────────────────

    /// <summary>按名称查修改器的实际路径（绑定对话框的「查询」用；名字就是「复制名称」拿到的那个）</summary>
    public string? FindTrainerPath(string name) => TrainerDownloadService.FindTrainerPath(name);

    private static string PidPath => Path.Combine(TrainerDownloadService.DefaultDir, "monitor.pid");

    /// <summary>按开关与"是否有启用绑定"起停监控子进程；关掉时主动结束它，不留后台进程</summary>
    public void ApplyMonitorState()
    {
        var shouldRun = MonitorEnabled && Bindings.Any(b => b.IsEnabled);
        var running = FindMonitorPid();

        if (shouldRun && running == null)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OSTGUI.exe"),
                    Arguments = "--trainer-monitor",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                LogService.AddAppLog("trainer 监控子进程已启动");
            }
            catch (Exception ex)
            {
                LogService.AddAppLog($"trainer 监控启动失败: {ex.Message}");
            }
            running = FindMonitorPid();
        }
        else if (!shouldRun && running != null)
        {
            try
            {
                Process.GetProcessById(running.Value).Kill();
                LogService.AddAppLog($"trainer 监控子进程已停止 pid={running.Value}");
            }
            catch { }
            running = null;
        }

        UpdateMonitorStatus(running);
    }

    private static int? FindMonitorPid()
    {
        try
        {
            if (!File.Exists(PidPath)) return null;
            if (!int.TryParse(File.ReadAllText(PidPath).Trim(), out var pid)) return null;

            var proc = Process.GetProcessById(pid);
            return proc.HasExited ? null : pid;
        }
        catch { return null; }   // 进程不在了 / pid 文件过期
    }

    private void UpdateMonitorStatus(int? pid = null)
    {
        pid ??= FindMonitorPid();
        var enabled = Bindings.Count(b => b.IsEnabled);
        var monitor = pid == null
            ? "监控未运行"
            : $"监控运行中（pid {pid}）";
        MonitorStatus = $"绑定 {Bindings.Count} 条（启用 {enabled}）· {monitor}";
    }

    private void RefreshLocalTrainers()
    {
        LocalTrainers.Clear();
        foreach (var t in _downloads.ListLocal()) LocalTrainers.Add(t);
        ApplyLocalFilter();
    }

    /// <summary>本地过滤：只动 LocalItems（「已下载」自己的集合），与网页搜索结果互不影响</summary>
    private void ApplyLocalFilter()
    {
        var q = LocalFilter?.Trim() ?? "";
        var list = string.IsNullOrEmpty(q)
            ? LocalTrainers.ToList()
            : LocalTrainers.Where(t => t.GameName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        LocalItems.Clear();
        foreach (var t in list) LocalItems.Add(t);

        if (ViewIndex == 1)
            StatusText = q.Length > 0
                ? $"已下载 {LocalTrainers.Count} 个（过滤「{q}」后 {list.Count} 个）"
                : $"已下载 {list.Count} 个修改器";
    }

    /// <summary>把条目与本地已下载文件对上（同名的就算已下载）</summary>
    private IEnumerable<TrainerInfo> MarkDownloaded(IEnumerable<TrainerInfo> list)
    {
        var local = _downloads.ListLocal();
        foreach (var item in list)
        {
            var match = local.FirstOrDefault(l =>
                l.GameName.Equals(item.GameName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                item.LocalPath = match.LocalPath;
                item.UpdateDate = item.UpdateDate.Length > 0 ? item.UpdateDate : match.UpdateDate;
            }
            yield return item;
        }
    }
}
