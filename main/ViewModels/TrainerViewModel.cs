using System.Collections.ObjectModel;
using System.Diagnostics;
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

    /// <summary>监控按钮的可用状态：跑着时只能点「停止」，停着时只能点「运行」</summary>
    [ObservableProperty] private bool _canStartMonitor = true;
    [ObservableProperty] private bool _canStopMonitor;

    /// <summary>当前下载目录（显示用；改目录见 SetDownloadDir）</summary>
    [ObservableProperty] private string _downloadDirText = "";

    /// <summary>运行监控：建立后台服务，使修改器随游戏启停（GUI 退出了也继续）</summary>
    public void StartMonitor()
    {
        _config.Update(c => c.TrainerMonitorEnabled = true);   // 只改内存，退出时统一落盘
        _ = ApplyMonitorStateAsync();
    }

    /// <summary>停止监控：把后台进程结束掉，不留驻留</summary>
    public void StopMonitor()
    {
        _config.Update(c => c.TrainerMonitorEnabled = false);
        _ = ApplyMonitorStateAsync();
    }

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
        DownloadDirText = "路径：" + _downloads.Dir;

    public TrainerViewModel(
        TrainerCatalogService catalog, TrainerDownloadService downloads, TrainerBindingService bindingService,
        ConfigService config)
    {
        _catalog = catalog;
        _downloads = downloads;
        _bindingService = bindingService;
        _config = config;
    }

    partial void OnViewIndexChanged(int value) => _ = LoadViewAsync();
    partial void OnLocalFilterChanged(string value) => ApplyLocalFilter();
    partial void OnQueryChanged(string value) => _searchLoaded = false;   // 查询词变了，旧结果作废

    /// <summary>每次进页面：刷新已下载、绑定与监控状态（列表按当前视图按需拉）</summary>
    public async Task InitializeAsync()
    {
        RefreshLocalTrainers();
        ReloadBindings();
        await ApplyMonitorStateAsync();
        UpdateDownloadDirText();

        if (!_initialized)
        {
            _initialized = true;
            await LoadViewAsync();
        }
    }

    public async Task LoadViewAsync(bool force = false)
    {
        // 「已下载」是纯本地刷新，**必须在 IsBusy 之前处理**：否则下载/搜索进行中切过去，
        // 列表会保留上一次的搜索结果（切到已下载却看到搜索结果）
        if (ViewIndex == 1)
        {
            RefreshLocalTrainers();   // 走索引，只动 LocalItems（不碰搜索的 Items）
            return;
        }

        if (IsBusy) return;

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
                LogService.Diag($"trainer 列表加载失败: {ex.Message}");
                Items.Clear();
                StatusText = SearchStatusText();
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
        if (_loadedQuery.Length == 0) return "使用搜索框搜索...";
        if (Items.Count == 0) return $"未找到[{_loadedQuery}]的结果；详情请看日志";
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
        try
        {
            var download = await _catalog.GetDownloadAsync(trainer.PageUrl);
            if (download == null)
            {
                return;   // 失败只进日志（用户要求不留提示）
            }

            // 进度本身就显示在这一行小字上（正在下载 xxx 42%）；下完先留着（100%），2 秒后再换回搜索结果条数
            var progress = MakeProgress("正在下载", trainer.GameName);
            var (path, error) = await _downloads.DownloadAsync(
                download.Value.Url, download.Value.FileName, trainer.PageUrl, progress);
            if (path == null)
            {
                return;   // 失败只进日志（用户要求不留提示）
            }

            trainer.LocalPath = path;
            RefreshLocalTrainers();
            _ = RevertStatusLaterAsync();
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
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? _downloads.Dir,
                UseShellExecute = true,
            });

            LogService.Event($"trainer 手动启动 {Path.GetFileName(exe)}");
            ToastService.ShowSuccess("已启动", Path.GetFileName(exe));
        }
        catch (Exception ex)
        {
            LogService.Diag($"trainer 启动失败 {exe}: {ex.Message}");
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
        if (trainer == null) return;
        if (IsBusy)
        {
            StatusText = "有任务进行中，请稍后再试";
            return;
        }

        var oldPath = trainer.LocalPath;
        IsBusy = true;
        try
        {
            StatusText = $"正在检查更新 {trainer.GameName}…";
            var page = trainer.PageUrl;
            if (string.IsNullOrEmpty(page))
            {
                page = await FindPageUrlAsync(trainer.GameName);
                if (string.IsNullOrEmpty(page))
                {
                    StatusText = "更新失败：修改器未找到";
                    return;
                }
            }

            var latest = await _catalog.GetDownloadAsync(page);
            if (latest == null)
            {
                StatusText = "更新失败：修改器未找到";
                return;
            }

            if (TrainerNames.IsSame(Path.GetFileName(oldPath), latest.Value.FileName))
            {
                StatusText = $"已是最新：{trainer.GameName}";
                _ = RevertStatusLaterAsync();
                ToastService.ShowInfo("已是最新", trainer.GameName);
                return;
            }

            var progress = MakeProgress("正在更新", trainer.GameName);
            var (path, _) = await _downloads.DownloadAsync(latest.Value.Url, latest.Value.FileName, page, progress);

            if (path == null)
            {
                // 失败时旧文件原样不动（新文件先落盘、成功后才动旧引用）
                StatusText = "更新失败：连接异常（详见日志）";
                return;
            }

            _downloads.CommitUpdate(oldPath, path, Path.GetFileName(path), page, latest.Value.Url);
            RefreshLocalTrainers();
            ReloadBindings();              // 名称→路径 的解析结果可能变了，列表跟着刷新
            StatusText = $"已更新：{trainer.GameName} → {Path.GetFileName(path)}";
            _ = RevertStatusLaterAsync();
            ToastService.ShowSuccess("更新完成", Path.GetFileName(path));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>用官方 RSS 按名字找文章页（本地名形如 Crimson.Desert.Enhanced.v1.0-v2.0x.Plus.12.exe）</summary>
    private async Task<string?> FindPageUrlAsync(string trainerName)
    {
        var query = TrainerNames.SearchQuery(trainerName);
        if (query.Length == 0) return null;

        var target = TrainerNames.Normalize(query);
        var hits = await _catalog.SearchAsync(query);
        return (hits.FirstOrDefault(h => TrainerNames.Normalize(h.GameName) == target)
                ?? hits.FirstOrDefault())?.PageUrl;
    }

    /// <summary>
    /// 操作完成后隔 2 秒把状态行换回当前视图的底数状态（搜索=「搜索 xxx N 条」，已下载=「已下载 N 个修改器」）。
    /// 留这 2 秒是让人看到进度走完，否则刚下完就被换掉，像是没下完。
    /// </summary>
    private async Task RevertStatusLaterAsync()
    {
        await Task.Delay(2000);
        if (!IsBusy) StatusText = ViewIndex == 1 ? LocalStatusText() : SearchStatusText();
    }

    /// <summary>
    /// 进度回调（下载与更新共用）。整条进度就显示在状态行那行小字上：
    /// 有 Content-Length 就显示百分数；实测该站是分块响应（没长度）→ 改显示"已下载多少 MB"。
    /// </summary>
    private IProgress<(double Percent, long Bytes)> MakeProgress(string verb, string gameName) =>
        new Progress<(double Percent, long Bytes)>(v => StatusText = v.Percent >= 0
            ? $"{verb} {gameName} {v.Percent:0}%"
            : $"{verb} {gameName} {v.Bytes / 1024.0 / 1024.0:0.0} MB");

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

        // 文件没了，指向它的绑定就是死绑定 → 一并删掉（绑定的名称就是这里显示的名字，含扩展名差异也算同一条）
        var stale = Bindings.Where(b => TrainerNames.IsSame(b.TrainerName, trainer.GameName)).ToList();
        foreach (var binding in stale) Bindings.Remove(binding);
        if (stale.Count > 0)
        {
            SaveBindings();          // 落盘并让监控重载
            LogService.Diag($"trainer 删除 {trainer.GameName}：同时移除 {stale.Count} 条绑定");
        }

        RefreshLocalTrainers();
        if (ViewIndex == 1) _ = LoadViewAsync();
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
        _ = ApplyMonitorStateAsync();
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

    /// <summary>
    /// 按「运行 / 停止」两个按钮的意图起停监控子进程：想运行就一定有监控在跑（哪怕还没有绑定，它待命），
    /// 想停止就把它结束掉、不留后台进程。意图记在配置里（退出时统一落盘），下次开 GUI 按它继续。
    /// </summary>
    public async Task ApplyMonitorStateAsync()
    {
        var wanted = _config.Config.TrainerMonitorEnabled;
        var running = FindMonitorPid();

        if (wanted && running == null)
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
                LogService.Diag("trainer 监控子进程已启动");
            }
            catch (Exception ex)
            {
                LogService.Diag($"trainer 监控启动失败: {ex.Message}");
            }

            // 子进程写 pid 文件要一点时间，等一下再报状态（否则会误报"未运行"）。
            // 用 await 轮询：这里是 UI 线程，Thread.Sleep 会把界面冻住
            for (var i = 0; i < 10 && running == null; i++)
            {
                await Task.Delay(200);
                running = FindMonitorPid();
            }
        }
        else if (!wanted && running != null)
        {
            try
            {
                Process.GetProcessById(running.Value).Kill();
                LogService.Diag($"trainer 监控子进程已停止 pid={running.Value}");
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
            if (proc.HasExited) return null;

            // pid 可能被复用：确认还是我们的进程
            return proc.ProcessName.Equals("OSTGUI", StringComparison.OrdinalIgnoreCase) ? pid : null;
        }
        catch { return null; }   // 进程不在了 / pid 文件过期
    }

    /// <summary>只刷新监控状态（不碰起停）——页面每 2 秒轮询用：进程被任务管理器结束后按钮要跟着变</summary>
    public void RefreshMonitorStatus() => UpdateMonitorStatus();

    private void UpdateMonitorStatus(int? pid = null)
    {
        pid ??= FindMonitorPid();
        var enabled = Bindings.Count(b => b.IsEnabled);

        var monitor = pid == null
            ? (_config.Config.TrainerMonitorEnabled ? "监控未运行" : "监控已关闭")
            : "监控已运行";
        MonitorStatus = $"共{Bindings.Count}条，启用{enabled}条 · {monitor}" + (pid == null ? "" : $"（pid {pid}）");

        // 跑着时只能点「停止」，停着时只能点「运行」
        CanStopMonitor = pid != null;
        CanStartMonitor = pid == null;
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

        if (ViewIndex == 1) StatusText = LocalStatusText();
    }

    /// <summary>「已下载」该显示的那句底数状态</summary>
    private string LocalStatusText() => $"已下载 {LocalItems.Count} 个修改器";

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
