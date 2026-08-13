using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OSTGUI.Models;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// 搜索入库页 ViewModel
/// </summary>
public partial class SearchViewModel : ObservableObject
{
    private readonly GameSearchService _searchService;
    private readonly ManifestService _manifestService;
    private readonly SteamService _steamService;
    private readonly ConfigService _configService;
    private bool _isLoadingOptions;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _statusMessage = "输入游戏名称或 AppID 进行搜索";
    [ObservableProperty] private string _statusType = "Info";
    [ObservableProperty] private double _progressValue;

    // 当前选中的结果（用于入库）
    [ObservableProperty] private SearchResult? _selectedResult;

    // 搜索结果列表
    [ObservableProperty] private ObservableCollection<SearchResult> _searchResults = new();

    // 是否已执行过搜索（用于控制"无结果"提示的显示）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private bool _hasSearched;

    // 入库选项（全局）
    [ObservableProperty] private bool _addAllDlc;
    [ObservableProperty] private bool _patchDepotKey;
    [ObservableProperty] private bool _fixedVersion;

    public ObservableCollection<string> Logs => LogService.Logs;
    public string LogText => string.Join("\n", LogService.Logs);
    public bool HasResults => SearchResults.Count > 0;
    public bool ShowNoResults => HasSearched && !HasResults;

    public SearchViewModel(
        GameSearchService searchService,
        ManifestService manifestService,
        SteamService steamService,
        ConfigService configService)
    {
        _searchService = searchService;
        _manifestService = manifestService;
        _steamService = steamService;
        _configService = configService;

        // 集合内容变化时同步刷新 HasResults 与无结果提示
        SearchResults.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(ShowNoResults));
        };

        LoadOptionsFromConfig();
    }

    public void LoadOptionsFromConfig()
    {
        _isLoadingOptions = true;
        try
        {
            var c = _configService.Config;
            AddAllDlc = c.DefaultAddAllDlc;
            PatchDepotKey = c.DefaultPatchDepotKey;
            FixedVersion = c.StFixedVersionDefault;
        }
        finally
        {
            _isLoadingOptions = false;
        }
    }

    public void SaveOptionsToConfig()
    {
        // 同步等待落盘，避免应用关闭/重启时异步保存未完成导致选项丢失
        _configService.UpdateAndSaveAsync(c =>
        {
            c.DefaultAddAllDlc = AddAllDlc;
            c.DefaultPatchDepotKey = PatchDepotKey;
            c.StFixedVersionDefault = FixedVersion;
        }).GetAwaiter().GetResult();
    }

    // 勾选状态变化时立即保存，防止下次打开页面/重启应用后重置
    partial void OnAddAllDlcChanged(bool value)
    {
        if (!_isLoadingOptions && _configService.IsLoaded)
            SaveOptionsToConfig();
    }

    partial void OnPatchDepotKeyChanged(bool value)
    {
        if (!_isLoadingOptions && _configService.IsLoaded)
            SaveOptionsToConfig();
    }


    partial void OnFixedVersionChanged(bool value)
    {
        if (!_isLoadingOptions && _configService.IsLoaded)
            SaveOptionsToConfig();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
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
            LogService.AddLog($"开始搜索: {query}");

            // 检查是否是 Steam 链接
            var appIdFromUrl = GameSearchService.ParseAppIdFromUrl(query);
            if (appIdFromUrl != null)
                query = appIdFromUrl;

            // 检查是否是纯 AppID
            if (int.TryParse(query, out _))
            {
                LogService.AddLog($"按 AppID 搜索: {query}");
                var result = await _searchService.SearchByAppIdAsync(query);
                if (result.Success)
                {
                    SearchResults.Add(result);
                    SelectedResult = result;
                    LogService.AddLog($"找到: {result.Name} (AppID {result.AppId})");
                    SetStatus($"识别成功: {result.Name}", "Success");
                }
                else
                {
                    LogService.AddLog($"搜索失败: {result.ErrorMessage}");
                    SetStatus($"未找到匹配的游戏: {result.ErrorMessage}", "Error");
                }
            }
            else
            {
                // 按名称搜索
                LogService.AddLog($"按名称搜索: {query}");
                var results = await _searchService.SearchByNameAsync(query);
                if (results.Count > 0)
                {
                    foreach (var r in results)
                        SearchResults.Add(r);
                    SelectedResult = results[0];
                    LogService.AddLog($"找到 {results.Count} 个匹配结果，首个: {results[0].Name} (AppID {results[0].AppId})");
                    SetStatus($"找到 {results.Count} 个匹配结果", "Success");
                }
                else
                {
                    var msg = ContainsChinese(query)
                        ? "未找到匹配的游戏（Steam 对中文名的搜索支持有限，建议改用英文名或 AppID）"
                        : "未找到匹配的游戏，请尝试使用 AppID";
                    LogService.AddLog(msg);
                    SetStatus(msg, "Error");
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"搜索失败: {ex.Message}", "Error");
        }
        finally
        {
            IsSearching = false;
            HasSearched = true;
        }
    }

    [RelayCommand]
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

        IsAdding = true;
        ProgressValue = 0;
        LogService.Clear();
        LogService.AddLog($"开始入库 AppID: {target.AppId}");

        try
        {
            var appId = target.AppId;
            LogService.AddLog($"游戏名称: {target.Name}");

            var steamPath = _steamService.GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                LogService.AddLog("错误: Steam 路径未设置");
                SetStatus("Steam 路径未设置", "Error");
                return;
            }
            LogService.AddLog($"Steam 路径: {steamPath}");

            var progress = new Progress<string>(msg => LogService.AddLog(msg));
            var (success, message) = (false, "");
            var missingKeys = new List<string>();

            // 多源级联：MHub（配了 key 时）→ GitHub → Sudama，任一成功即完成
            var mhubKey = _configService.Config.ManifestSources?
                .FirstOrDefault(s => s.Id == "mhub")?.ApiKey
                ?? _configService.Config.ManifestHubApiKey;

            if (!string.IsNullOrEmpty(mhubKey))
            {
                LogService.AddLog("使用 ManifestHub 下载清单...");
                (success, message, missingKeys) = await _manifestService.DownloadFromManifestHubAsync(
                    appId, FixedVersion, AddAllDlc, progress);
            }

            if (!success)
            {
                LogService.AddLog("使用 GitHub 下载清单...");
                (success, message, missingKeys) = await _manifestService.DownloadFromGithubAsync(
                    appId, FixedVersion, AddAllDlc, PatchDepotKey, progress);
            }

            // GitHub 失败时兜底走 Sudama（Sudama 自身会尽力下载 manifest，失败也给出明确日志）
            if (!success)
            {
                LogService.AddLog("尝试 Sudama 兜底...");
                (success, message, missingKeys) = await _manifestService.DownloadFromSudamaAsync(
                    appId, FixedVersion, AddAllDlc, progress);
            }

            ProgressValue = 100;
            LogService.AddLog(message);

            if (success)
            {
                // 清单文件未获取到 → 入库异常（系统通知警告）
                if (message.Contains("未下载到清单文件"))
                {
                    var abnormalMsg = $"入库异常: {message}";
                    LogService.AddLog(abnormalMsg);
                    Services.ToastService.ShowWarning("入库异常", $"{target.Name} (AppID {appId}) 未能获取到清单文件，可能无法正常解锁");
                    SetStatus(abnormalMsg, "Warning");
                }
                else if (missingKeys.Count > 0)
                {
                    var abnormalMsg = $"入库异常: 缺少解密密钥: {string.Join(", ", missingKeys)}";
                    LogService.AddLog(abnormalMsg);
                    Services.ToastService.ShowWarning("入库异常",
                        $"{target.Name} (AppID {appId}) 缺少解密密钥: {string.Join(", ", missingKeys)}，Steam 内容均为 AES-256 加密，缺少密钥将无法正常下载");
                    SetStatus(abnormalMsg, "Warning");
                }
                else
                {
                    Services.ToastService.ShowSuccess("入库成功", $"{target.Name} (AppID {appId}) 已入库");
                    SetStatus(message, "Success");
                }
                SaveOptionsToConfig();
            }
            else
            {
                Services.ToastService.ShowError("入库失败", message);
                SetStatus(message, "Error");
            }
        }
        catch (Exception ex)
        {
            LogService.AddLog($"异常: {ex.Message}");
            SetStatus($"入库失败: {ex.Message}", "Error");
        }
        finally
        {
            IsAdding = false;
        }
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
