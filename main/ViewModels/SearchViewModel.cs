using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using OSTGUI.Models;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// 搜索入库页 ViewModel
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public partial class SearchViewModel : ObservableObject
{
    private readonly GameSearchService _searchService;
    private readonly ManifestDownloadService _manifestService;
    private readonly SteamService _steamService;
    private readonly ConfigService _configService;
    private readonly CoverImageService _coverService;
    private bool _isLoadingOptions;

    /// <summary>当前入库任务的取消源（每次 AddGameAsync 新建、finally 里释放）；取消只作用于"当前这次"</summary>
    private CancellationTokenSource? _addCts;

    // 命令手写在 VM 上：Native AOT 下 CsWinRT 绑定提供器看不到源生成成员，XAML {Binding} 会失效
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand AddGameCommand { get; }
    public IRelayCommand CancelAddCommand { get; }

    private string _searchQuery = "";

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    private bool _isSearching;

    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }

    private bool _isAdding;

    public bool IsAdding
    {
        get => _isAdding;
        set => SetProperty(ref _isAdding, value);
    }

    /// <summary>取消已按下、链路还在 unwind。用它给按钮上"取消中…"的即时反馈，手感不依赖链路返回速度</summary>
    private bool _isCancelling;

    public bool IsCancelling
    {
        get => _isCancelling;
        set
        {
            if (SetProperty(ref _isCancelling, value))
            {
                OnPropertyChanged(nameof(CancelButtonText));
            }
        }
    }

    /// <summary>取消按钮文案</summary>
    public string CancelButtonText => IsCancelling ? "取消中…" : "取消任务";

    private string _statusType = "Info";

    public string StatusType
    {
        get => _statusType;
        set => SetProperty(ref _statusType, value);
    }

    private string _statusMessage = "输入游戏名称或 AppID 进行搜索";

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private double _progressValue;

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    // 当前选中的结果（用于入库）
    private SearchResult? _selectedResult;

    public SearchResult? SelectedResult
    {
        get => _selectedResult;
        set => SetProperty(ref _selectedResult, value);
    }

    // 搜索结果列表
    private ObservableCollection<SearchResult> _searchResults = new();

    // 声明类型用 IList<T>：WinRT 内建映射（IVector<T>），Native AOT 下无需在
    // WinRTGlobalVtableLookup 里登记闭合泛型；运行时对象仍是 ObservableCollection（见字段），
    // 集合变更通知行为不变。
    public IList<SearchResult> SearchResults
    {
        get => _searchResults;
        set => SetProperty(ref _searchResults, value as ObservableCollection<SearchResult> ?? new(value));
    }

    // 是否已执行过搜索（用于控制"无结果"提示的显示）
    private bool _hasSearched;

    public bool HasSearched
    {
        get => _hasSearched;
        set
        {
            if (SetProperty(ref _hasSearched, value))
            {
                OnPropertyChanged(nameof(ShowNoResults));
            }
        }
    }

    // 入库选项（全局）
    private bool _addAllDlc;

    public bool AddAllDlc
    {
        get => _addAllDlc;
        set
        {
            if (SetProperty(ref _addAllDlc, value))
            {
                OnAddAllDlcChanged(value);
            }
        }
    }

    private bool _fixedVersion;

    public bool FixedVersion
    {
        get => _fixedVersion;
        set
        {
            if (SetProperty(ref _fixedVersion, value))
            {
                OnFixedVersionChanged(value);
            }
        }
    }

    private bool _downloadManifest;

    public bool DownloadManifest
    {
        get => _downloadManifest;
        set
        {
            if (SetProperty(ref _downloadManifest, value))
            {
                OnDownloadManifestChanged(value);
            }
        }
    }

    public bool HasResults => SearchResults.Count > 0;
    public bool ShowNoResults => HasSearched && !HasResults;

    /// <summary>搜索结果视图形态：list / grid。持久化在 config.json 的 SearchViewMode
    /// （与入库管理各自独立记忆；做法与 <see cref="LibraryViewModel"/> 一致）</summary>
    private string _viewMode = "list";

    public string ViewMode
    {
        get => _viewMode;
        set
        {
            if (SetProperty(ref _viewMode, value))
            {
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(IsGridView));
            }
        }
    }

    public bool IsListView => ViewMode != "grid";
    public bool IsGridView => ViewMode == "grid";

    /// <summary>
    /// 切视图形态。写盘失败不影响切换（内存态优先，下次重开最多回到上一档）
    /// </summary>
    public Task SetViewModeAsync(string mode)
    {
        mode = mode == "grid" ? "grid" : "list";
        if (ViewMode == mode) return Task.CompletedTask;

        ViewMode = mode;
        _configService.Update(c => c.SearchViewMode = mode);   // 只改内存，退出时统一落盘
        return Task.CompletedTask;
    }

    public SearchViewModel(
        GameSearchService searchService,
        ManifestDownloadService manifestService,
        SteamService steamService,
        ConfigService configService,
        CoverImageService coverService)
    {
        _searchService = searchService;
        _manifestService = manifestService;
        _steamService = steamService;
        _configService = configService;
        _coverService = coverService;

        // 视图形态跟着上次选择（脏值一律归一到 list）
        _viewMode = configService.Config.SearchViewMode == "grid" ? "grid" : "list";

        // 集合内容变化时同步刷新 HasResults 与无结果提示
        _searchResults.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(ShowNoResults));
        };

        LoadOptionsFromConfig();

        SearchCommand = new AsyncRelayCommand(SearchAsyncEntryAsync);
        AddGameCommand = new AsyncRelayCommand<SearchResult?>(AddGameAsync);
        CancelAddCommand = new RelayCommand(CancelAdd);
    }

    public void LoadOptionsFromConfig()
    {
        _isLoadingOptions = true;
        try
        {
            var c = _configService.Config;
            AddAllDlc = c.DefaultAddAllDlc;
            FixedVersion = c.StFixedVersionDefault;
            DownloadManifest = c.StDownloadManifestDefault;
        }
        finally
        {
            _isLoadingOptions = false;
        }
    }

    public void SaveOptionsToConfig()
    {
        // 只改内存：退出时统一落盘（也就不存在"关得太快、异步保存没写完"的问题）
        _configService.Update(c =>
        {
            c.DefaultAddAllDlc = AddAllDlc;
            c.StFixedVersionDefault = FixedVersion;
            c.StDownloadManifestDefault = DownloadManifest;
        });
    }

    // 勾选状态变化时立即保存，防止下次打开页面/重启应用后重置
    private void OnAddAllDlcChanged(bool value)
    {
        if (!_isLoadingOptions && _configService.IsLoaded)
            SaveOptionsToConfig();
    }

    private void OnFixedVersionChanged(bool value)
    {
        if (!_isLoadingOptions && _configService.IsLoaded)
            SaveOptionsToConfig();
    }

    private void OnDownloadManifestChanged(bool value)
    {
        if (!_isLoadingOptions && _configService.IsLoaded)
            SaveOptionsToConfig();
    }

    /// <summary>
    /// 命令入口包装：只为了把异常**记下来**再原样抛出（外面 `_ = ExecuteAsync(null)` 是
    /// fire-and-forget，异常会被静默吞掉 —— 这条日志是唯一看得见的地方）。
    /// </summary>
    private async Task SearchAsyncEntryAsync()
    {
        try
        {
            await SearchAsync();
        }
        catch (Exception ex)
        {
            LogService.Diag($"搜索：命令层捕获异常 {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            LogService.Diag("搜索：关键词为空 → 提前返回（一个请求都没发）");
            SetStatus("请输入游戏名称或 AppID", "Warning");
            return;
        }

        IsSearching = true;
        SearchResults.Clear();
        SelectedResult = null;
        HasSearched = false;
        SetStatus("正在搜索...", "Info");

        try
        {
            var query = SearchQuery.Trim();
            LogService.Event($"开始搜索: {query}");

            // 检查是否是 Steam 链接
            var appIdFromUrl = GameSearchService.ParseAppIdFromUrl(query);
            if (appIdFromUrl != null)
                query = appIdFromUrl;

            // 检查是否是纯 AppID
            if (int.TryParse(query, out _))
            {
                LogService.Event($"按 AppID 搜索: {query}");
                var result = await _searchService.SearchByAppIdAsync(query);
                if (result.Success)
                {
                    SearchResults.Add(result);
                    SelectedResult = result;
                    LogService.Event($"找到: {result.Name} (AppID {result.AppId})");
                    SetStatus($"识别成功: {result.Name}", "Success");
                }
                else
                {
                    LogService.Diag($"搜索失败: {result.ErrorMessage}");
                    SetStatus($"未找到匹配的游戏: {result.ErrorMessage}", "Error");
                }
            }
            else
            {
                // 按名称搜索
                LogService.Event($"按名称搜索: {query}");
                var results = await _searchService.SearchByNameAsync(query);
                if (results.Count > 0)
                {
                    foreach (var r in results)
                        SearchResults.Add(r);
                    SelectedResult = results[0];
                    LogService.Event($"找到 {results.Count} 个匹配结果，首个: {results[0].Name} (AppID {results[0].AppId})");
                    SetStatus($"找到 {results.Count} 个匹配结果", "Success");
                }
                else
                {
                    var msg = ContainsChinese(query)
                        ? "未找到匹配的游戏（Steam 对中文名的搜索支持有限，建议改用英文名或 AppID）"
                        : "未找到匹配的游戏，请尝试使用 AppID";
                    LogService.Event(msg);
                    // 失败路径必须落盘：Event 只进运行时日志、**不写文件**，
                    // 用户报"搜什么都超时或无结果"时文件里一条都看不到（2026-09-27 补）
                    LogService.Diag($"搜索「{query}」三个源都没结果（storesearch / 商店搜索页 / 关键词接口）");
                    SetStatus(msg, "Error");
                }
            }
        }
        catch (Exception ex)
        {
            // 同上层：这条以前完全没记录（连运行时日志都没有），失败原因查不到（2026-09-27 补）
            LogService.Diag($"搜索「{SearchQuery.Trim()}」异常：{ex.GetType().Name}: {ex.Message}");
            SetStatus($"搜索失败: {ex.Message}", "Error");
        }
        finally
        {
            IsSearching = false;
            HasSearched = true;
        }

        // 缩略图：只走内存（不落缓存）、失败静默。先快照，免得下一次搜索把集合换掉
        _ = LoadThumbnailsAsync(SearchResults.ToList());
    }

    /// <summary>
    /// 拉搜索结果缩略图。**不落盘**（搜索结果是临时的，别污染 covers 缓存）；
    /// 并发 4，失败就让卡片显示占位图标。必须在 UI 线程调用（BitmapImage 是 DependencyObject）
    /// </summary>
    private async Task LoadThumbnailsAsync(List<SearchResult> results)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(results.Select(async result =>
        {
            if (result.Thumbnail is not null || !result.AppId.All(char.IsAsciiDigit)) return;
            await gate.WaitAsync();
            try
            {
                var bytes = await _coverService.FetchThumbnailBytesAsync(result.AppId);
                if (bytes is not null)
                    result.Thumbnail = await CreateBitmapAsync(bytes);
            }
            catch { }
            finally { gate.Release(); }
        }));
    }

    /// <summary>
    /// 字节 → 位图。**刻意不设 `DecodePixelWidth`**（2026-09-22 实测的坑，见工作区 `doc/开发踩坑-UI.md`）：
    /// 流解码（`SetSourceAsync`）路径上 `DecodePixelType=Logical` 不生效，120 被当**物理像素**用，
    /// 首拍上屏用的是 ≈120px 的表面、再被 225% DPI 的卡片放大 2.25 倍 → 明显发糊；
    /// 切页重建 `Image` 后按真实布局尺寸重新取表面才变清楚（同一矩形梯度能量 13.6 → 19.5）。
    /// ⚠️ 探针 `bitmap.PixelWidth` 读到的是 **460**（原图尺寸，不是上屏表面），别拿它当判据。
    /// 不设即按原生 460×215 解码，显示端是缩小，任何 DPI 都清晰。
    /// ponytail: 每个结果常驻约 400KB 位图（十来条结果 ≈ 4MB）；真嫌大再改成把页面
    /// `XamlRoot.RasterizationScale` 传进来、用 `Physical` 解 `120×scale`。
    /// </summary>
    private static async Task<BitmapImage> CreateBitmapAsync(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }

    private async Task AddGameAsync(SearchResult? result = null)
    {
        var target = result ?? SelectedResult;
        if (target == null)
        {
            SetStatus("请先搜索游戏", "Warning");
            return;
        }

        if (string.IsNullOrEmpty(_steamService.GetSteamPath()))
        {
            SetStatus("Steam 路径未设置，请在设置中配置", "Error");
            return;
        }

        // 一次只跑一个入库任务：取消按钮只能作用于"当前这次"，并发跑两个会让被顶掉的那个无法取消
        if (IsAdding)
        {
            SetStatus("已有入库任务在进行，先取消或等它结束", "Warning");
            return;
        }

        _addCts?.Dispose();
        _addCts = new CancellationTokenSource();
        var ct = _addCts.Token;

        IsAdding = true;
        IsCancelling = false;   // 新一轮：清掉上一轮可能遗留的"取消中"状态
        ProgressValue = 0;
        LogService.Clear();
        LogService.Event($"开始入库 AppID: {target.AppId}");

        try
        {
            var appId = target.AppId;
            LogService.Event($"游戏名称: {target.Name}");

            var steamPath = _steamService.GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                LogService.Diag("错误: Steam 路径未设置");
                SetStatus("Steam 路径未设置", "Error");
                return;
            }
            LogService.Event($"Steam 路径: {steamPath}");

            var progress = new Progress<string>(msg => LogService.Event(msg));
            AddGameResult res;

            if (DownloadManifest)
            {
                // 多源级联：MHub（配了 key 时）→ Sudama 兜底，任一成功即完成
                var mhubKey = _configService.Config.ManifestSources?
                    .FirstOrDefault(s => s.Id == "mhub")?.ApiKey
                    ?? _configService.Config.ManifestHubApiKey;

                if (!string.IsNullOrEmpty(mhubKey))
                {
                    LogService.Event("使用 ManifestHub 下载清单...");
                    res = await _manifestService.DownloadFromManifestHubAsync(
                        appId, FixedVersion, AddAllDlc, progress, ct);
                }
                else
                {
                    res = new AddGameResult();
                }

                // MHub 失败（含未配 key）时兜底走 Sudama：仅作为密钥源生成 Lua（不下载清单），清单需由清单源获取。
                // 被用户取消时不兜底——取消的语义是"停下来"，不是"换个源接着跑"
                if (!res.Success && !res.Cancelled)
                {
                    LogService.Event("尝试 Sudama 兜底...");
                    res = await _manifestService.DownloadFromSudamaAsync(
                        appId, FixedVersion, AddAllDlc, progress, ct);
                }
            }
            else
            {
                // 关闭清单下载：跳过清单源，直接用密钥源生成 Lua，清单由内核运行时兜底获取
                LogService.Event("已关闭清单下载，跳过清单源，清单由内核运行时兜底获取...");
                res = await _manifestService.DownloadFromSudamaAsync(
                    appId, FixedVersion, AddAllDlc, progress, ct);
            }

            // 用户取消：既不是成功也不是失败，安静收尾（不弹通知——是用户自己发起的）
            if (res.Cancelled)
            {
                LogService.Event("入库已取消（临时文件已清理）");
                SetStatus("已取消入库", "Info");
                return;
            }

            ProgressValue = 100;
            LogService.Event(res.Message);

            if (res.Success)
            {
                // 异常 = 成功入库但有缺漏：缺失哪个清单 / 缺失哪个密钥
                var warnings = new List<string>();
                if (DownloadManifest && res.MissingManifests.Count > 0)
                    warnings.Add($"缺失清单: {string.Join(", ", res.MissingManifests)}");
                if (res.MissingKeys.Count > 0)
                    warnings.Add($"缺失密钥: {string.Join(", ", res.MissingKeys)}");

                if (warnings.Count > 0)
                {
                    var abnormalMsg = $"入库异常: {string.Join("；", warnings)}";
                    LogService.Event(abnormalMsg);
                    if (_configService.Config.ShowSystemNotifications)
                        Services.ToastService.ShowWarning("入库异常",
                            $"{target.Name} (AppID {appId}) {string.Join("；", warnings)}");
                    SetStatus(abnormalMsg, "Warning");
                }
                else
                {
                    var parts = new List<string>();
                    if (res.DlcCount > 0) parts.Add($"{res.DlcCount} 个 DLC");
                    if (res.ManifestCount > 0) parts.Add($"{res.ManifestCount} 个清单");
                    if (res.KeyCount > 0) parts.Add($"{res.KeyCount} 个密钥");
                    var detail = parts.Count > 0 ? string.Join("，", parts) : "（无清单/密钥）";
                    var successMsg = $"{target.Name} (AppID {appId}) 已入库，添加了 {detail}";
                    if (_configService.Config.ShowSystemNotifications)
                        Services.ToastService.ShowSuccess("入库成功", successMsg);
                    SetStatus(successMsg, "Success");
                }
                SaveOptionsToConfig();
            }
            else
            {
                // 入库失败：用户发起的操作未完成，保留错误提示，避免静默失败
                if (_configService.Config.ShowSystemNotifications)
                    Services.ToastService.ShowError("入库失败", res.Message);
                SetStatus(res.Message, "Error");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常情况下 ManifestDownloadService 会把取消包成 res.Cancelled，走不到这里；
            // 留着是防御：任何一处漏传 token 的取消也不会变成"入库失败"弹窗。
            // ⚠️ 必须带 ct.IsCancellationRequested 过滤：HttpClient 的**超时也是 OCE**，
            // 否则一次网络超时会被显示成"已取消入库"。
            LogService.Event("入库已取消");
            SetStatus("已取消入库", "Info");
        }
        catch (Exception ex)
        {
            LogService.Diag($"异常: {ex.Message}");
            SetStatus($"入库失败: {ex.Message}", "Error");
        }
        finally
        {
            IsAdding = false;
            IsCancelling = false;
            _addCts?.Dispose();
            _addCts = null;
            // 入库期间有过 17.5MB 级的整份缓存/清单缓冲，结束后压一次 LOH，把空洞还给系统
            OstMemory.CompactAfterLargeBuffers();
        }
    }

    /// <summary>
    /// 取消当前入库任务（按钮只在 IsAdding 时显示）。打断点与语义见
    /// <see cref="ManifestDownloadService.DownloadFromManifestHubAsync"/> 的注释：
    /// **所有会等的环节都可取消**（网络、逐份拷贝之间），只剩「lua 的原子写」这最后一步不打断——
    /// 所以点下去基本是立即返回，且永远不会留下半个 .lua。
    /// </summary>
    private void CancelAdd()
    {
        if (_addCts is null) return;
        IsCancelling = true;      // 立刻反馈：按钮变「取消中…」并禁用，别等链路返回
        _addCts.Cancel();
        LogService.Event("用户取消入库");
    }

    private void SetStatus(string message, string type)
    {
        StatusMessage = message;
        StatusType = type;
    }

    private static bool ContainsChinese(string text)
    {
        return text.Any(c => c >= '\u4e00' && c <= '\u9fff');
    }
}
