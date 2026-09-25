using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OSTGUI.Models;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// 修改器页：热门 / 新品 / 搜索 / 已下载四种列表 + 下载、启动、删除 + 「进程绑定」
/// （把修改器绑到某个游戏，游戏一跑就自动启动它，见 Services/TrainerMonitor.cs）。
/// </summary>
public partial class TrainerViewModel : ObservableObject
{
    private readonly TrainerCatalogService _catalog;
    private readonly TrainerDownloadService _downloads;
    private readonly TrainerBindingService _bindingService;
    private readonly LibraryScanner _scanner;
    private readonly OnlineFixService _onlineFix;
    private readonly ConfigService _config;

    private bool _initialized;
    private bool _loadingConfig;

    public ObservableCollection<TrainerInfo> Items { get; } = new();
    public ObservableCollection<TrainerBinding> Bindings { get; } = new();

    /// <summary>绑定对话框用的游戏下拉（入库游戏，AppId + 名字）</summary>
    public ObservableCollection<LibraryItem> Games { get; } = new();

    /// <summary>已下载的修改器（绑定对话框的候选）</summary>
    public ObservableCollection<TrainerInfo> LocalTrainers { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private int _viewIndex;          // 0 热门 / 1 新品 / 2 搜索 / 3 已下载
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _monitorStatus = "";

    /// <summary>监控开关：开了 GUI 退出后仍按绑定工作（默认关）</summary>
    [ObservableProperty] private bool _monitorEnabled;

    public TrainerViewModel(
        TrainerCatalogService catalog, TrainerDownloadService downloads, TrainerBindingService bindingService,
        LibraryScanner scanner, OnlineFixService onlineFix, ConfigService config)
    {
        _catalog = catalog;
        _downloads = downloads;
        _bindingService = bindingService;
        _scanner = scanner;
        _onlineFix = onlineFix;
        _config = config;

        _loadingConfig = true;
        _monitorEnabled = config.Config.TrainerMonitorEnabled;
        _loadingConfig = false;
    }

    partial void OnViewIndexChanged(int value) => _ = LoadViewAsync();
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

        if (!_initialized)
        {
            _initialized = true;
            await LoadViewAsync();
            _ = LoadGamesAsync();
        }
    }

    private async Task LoadGamesAsync()
    {
        try
        {
            var items = await _scanner.ScanLibraryAsync();
            Games.Clear();
            foreach (var item in items.Where(i => i.AppId != "N/A" && i.AppId.All(char.IsDigit)))
                Games.Add(item);
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 载入游戏列表失败: {ex.Message}");
        }
    }

    public async Task LoadViewAsync()
    {
        if (IsBusy) return;

        if (ViewIndex == 3)
        {
            RefreshLocalTrainers();
            Items.Clear();
            foreach (var t in LocalTrainers) Items.Add(t);
            StatusText = $"已下载 {Items.Count} 个修改器";
            return;
        }

        IsBusy = true;
        try
        {
            List<TrainerInfo> list;
            try
            {
                list = ViewIndex switch
                {
                    0 => await _catalog.GetHotAsync(),
                    1 => await _catalog.GetNewAsync(),
                    2 => await _catalog.SearchAsync(Query),
                    _ => new List<TrainerInfo>(),
                };
            }
            catch (Exception ex)
            {
                // 抓取失败（站点/网络不可用）不能让异常冒出去：这里是 fire-and-forget 调用
                LogService.AddAppLog($"trainer 列表加载失败: {ex.Message}");
                Items.Clear();
                StatusText = "抓取失败：网络或站点不可用（详见日志）";
                return;
            }

            Items.Clear();
            foreach (var t in MarkDownloaded(list)) Items.Add(t);

            StatusText = ViewIndex switch
            {
                0 => $"热门 {Items.Count} 条",
                1 => $"新品 {Items.Count} 条",
                2 => string.IsNullOrWhiteSpace(Query) ? "输入游戏名后点搜索" : $"搜索「{Query}」{Items.Count} 条",
                _ => "",
            };
            if (ViewIndex is 0 or 1 or 2 && Items.Count == 0)
                StatusText += "（没抓到内容，可能是站点改版或网络异常，详见日志）";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        ViewIndex = 2;
        await LoadViewAsync();
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

            var progress = new Progress<double>(p => trainer.DownloadProgress = p);
            var path = await _downloads.DownloadAsync(download.Value.Url, download.Value.FileName, progress);
            if (path == null)
            {
                ToastService.ShowError("下载失败", "网络异常或文件被占用，详见日志");
                return;
            }

            trainer.LocalPath = path;
            RefreshLocalTrainers();
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
                WorkingDirectory = Path.GetDirectoryName(exe) ?? TrainerDownloadService.TrainerDir,
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
        if (ViewIndex == 3) _ = LoadViewAsync();
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

    /// <summary>绑定对话框点确定后调用：同一修改器只留一条绑定</summary>
    public void AddOrUpdateBinding(string appId, string gameName, string gameExe, string trainerExe, bool enabled)
    {
        var same = Bindings.FirstOrDefault(b =>
            string.Equals(b.TrainerFilePath, trainerExe, StringComparison.OrdinalIgnoreCase));
        if (same != null) Bindings.Remove(same);

        Bindings.Add(new TrainerBinding
        {
            AppId = appId,
            GameName = gameName,
            GameExePath = gameExe,
            TrainerFilePath = trainerExe,
            IsEnabled = enabled,
        });
        SaveBindings();
        ToastService.ShowSuccess("已绑定", $"{gameName} → {Path.GetFileName(trainerExe)}");
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

    /// <summary>按 AppID 找游戏主程序（绑定对话框自动带出 exe 用）</summary>
    public string? ResolveGameExe(string appId) => _onlineFix.ResolveGameExe(appId);

    // ── 监控子进程 ──────────────────────────────────────────────────────────

    private static string PidPath => Path.Combine(TrainerDownloadService.TrainerDir, "monitor.pid");

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
