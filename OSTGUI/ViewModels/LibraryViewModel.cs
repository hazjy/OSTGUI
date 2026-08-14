using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OSTGUI.Models;
using OSTGUI.Services;

namespace OSTGUI.ViewModels;

/// <summary>
/// 入库游戏管理 ViewModel
/// 支持列表/卡片视图切换、多选、右键菜单操作
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly LuaConfigService _luaService;
    private readonly GameSearchService _searchService;
    private readonly GameInfoService _gameInfoService;
    private readonly SteamService _steamService;
    private readonly ConfigService _configService;

    [ObservableProperty] private ObservableCollection<LibraryItem> _libraryItems = new();
    [ObservableProperty] private ObservableCollection<LibraryItem> _selectedItems = new();
    [ObservableProperty] private LibraryItem? _lastRightClickedItem;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isLoading;

    public bool IsBusy => IsLoading;

    [ObservableProperty] private string _statusMessage = "准备加载库...";
    [ObservableProperty] private string _statusType = "Info";
    [ObservableProperty] private string _viewMode = "list"; // list, grid
    [ObservableProperty] private string _sortMode = "default"; // default, az, za
    [ObservableProperty] private string _searchFilter = "";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _fixedCount;
    [ObservableProperty] private int _autoCount;
    [ObservableProperty] private double _progressValue;

    public LibraryViewModel(
        LuaConfigService luaService,
        GameSearchService searchService,
        GameInfoService gameInfoService,
        SteamService steamService,
        ConfigService configService)
    {
        _luaService = luaService;
        _searchService = searchService;
        _gameInfoService = gameInfoService;
        _steamService = steamService;
        _configService = configService;

        // 恢复上次的视图模式
        ViewMode = configService.Config.LibraryViewMode;
        if (string.IsNullOrEmpty(ViewMode)) ViewMode = "list";
    }

    /// <summary>
    /// 加载入库游戏列表
    /// </summary>
    [RelayCommand]
    private async Task LoadLibraryAsync()
    {
        IsLoading = true;
        LibraryItems = new ObservableCollection<LibraryItem>();
        TotalCount = 0;
        FixedCount = 0;
        AutoCount = 0;
        SetStatus("正在扫描入库游戏...", "Info");
        ProgressValue = 0;

        try
        {
            var items = await _luaService.ScanLibraryAsync();
            ProgressValue = 30;

            // 批量补游戏名：缓存命中不联网，仅缓存缺失的联网获取（限并发，只慢一次）
            var appIds = items
                .Where(i => i.AppId != "N/A" && !string.IsNullOrEmpty(i.GameName) && i.GameName.StartsWith("AppID"))
                .Select(i => i.AppId)
                .Distinct()
                .ToList();

            if (appIds.Count > 0)
            {
                ProgressValue = 50;
                var names = await _searchService.GetGameNamesBatchAsync(appIds);
                ProgressValue = 80;

                foreach (var item in items)
                {
                    if (names.TryGetValue(item.AppId, out var name))
                        item.GameName = name;
                }
            }

            // DLC 信息不再刷新时预加载，改为点"入库信息"时按需获取

            // 应用排序
            items = ApplySort(items);

            ProgressValue = 90;

            // 应用过滤
            var filtered = ApplyFilter(items);

            LibraryItems = new ObservableCollection<LibraryItem>(filtered);
            TotalCount = filtered.Count(i => i.AppId != "N/A");
            FixedCount = filtered.Count(i => i.VersionMode == "fixed");
            AutoCount = filtered.Count(i => i.VersionMode == "auto");

            ProgressValue = 100;
            SetStatus($"共 {TotalCount} 个已入库游戏 | 固定版本 {FixedCount} | 自动更新 {AutoCount}", "Info");
        }
        catch (Exception ex)
        {
            SetStatus($"加载失败: {ex.Message}", "Error");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 按需加载单个游戏的 DLC 信息（入库信息对话框打开时调用）
    /// </summary>
    public async Task LoadDlcInfoAsync(LibraryItem item)
    {
        try
        {
            var dlcInfo = await _gameInfoService.GetDlcInfoAsync(item.AppId);
            foreach (var dlc in dlcInfo)
                dlc.Status = item.InstalledAppIds.Contains(dlc.AppId) ? "installed" : "";
            item.DlcList = dlcInfo;
        }
        catch { }
    }

    /// <summary>
    /// 切换视图模式并记忆状态
    /// </summary>
    [RelayCommand]
    private async Task ToggleViewModeAsync(string? mode = null)
    {
        if (!string.IsNullOrEmpty(mode))
            ViewMode = mode;
        else
            ViewMode = ViewMode == "list" ? "grid" : "list";

        // 记住视图选择
        await _configService.UpdateAndSaveAsync(c => c.LibraryViewMode = ViewMode);

        // 重新加载以应用新视图
        await LoadLibraryAsync();
    }

    /// <summary>
    /// 切换排序模式
    /// </summary>
    [RelayCommand]
    private void ChangeSortMode(string mode)
    {
        SortMode = mode;
        var sorted = ApplySort(LibraryItems.ToList());
        LibraryItems = new ObservableCollection<LibraryItem>(ApplyFilter(sorted));
    }

    /// <summary>
    /// 应用搜索过滤
    /// </summary>
    [RelayCommand]
    private void ApplySearchFilter()
    {
        var items = ApplySort(LibraryItems.ToList());
        LibraryItems = new ObservableCollection<LibraryItem>(ApplyFilter(items));
    }

    // ==================== 右键菜单操作 ====================

    /// <summary>
    /// 切换版本模式（锁定/解锁游戏版本）
    /// </summary>
    [RelayCommand]
    private async Task ToggleVersionAsync(LibraryItem? item = null)
    {
        item ??= LastRightClickedItem;
        if (item == null || item.AppId == "N/A") return;

        var targets = SelectedItems.Count > 1 
            ? new List<LibraryItem>(SelectedItems) 
            : new List<LibraryItem> { item };

        foreach (var target in targets)
        {
            var (success, message, newMode) = await _luaService.ToggleVersionModeAsync(target);
            if (success)
            {
                SetStatus(message, "Success");
                // 成功提示可在设置中开关
                if (_configService.Config.ShowVersionChangeNotifications)
                    Services.ToastService.ShowSuccess("版本状态更改", message);
            }
            else
            {
                SetStatus(message, "Error");
                // 失败通知始终显示（重要错误信息）
                Services.ToastService.ShowError("版本状态更改", message);
            }
        }
    }

    /// <summary>
    /// 复制 AppID 到剪贴板
    /// </summary>
    [RelayCommand]
    private async Task CopyAppIdAsync(LibraryItem? item = null)
    {
        item ??= LastRightClickedItem;
        if (item == null) return;

        if (SelectedItems.Count > 1)
        {
            var ids = string.Join("\n", SelectedItems.Select(i => i.AppId));
            await CopyToClipboardAsync(ids);
            SetStatus($"已复制 {SelectedItems.Count} 个 AppID 到剪贴板", "Success");
        }
        else
        {
            await CopyToClipboardAsync(item.AppId);
            SetStatus($"已复制 AppID {item.AppId} 到剪贴板", "Success");
        }
    }

    /// <summary>
    /// 使用记事本编辑 Lua 配置
    /// </summary>
    [RelayCommand]
    private void EditLua(LibraryItem? item = null)
    {
        item ??= LastRightClickedItem;
        if (item == null) return;

        var luaDir = _steamService.GetLuaConfigDir();
        if (string.IsNullOrEmpty(luaDir)) return;

        var filePath = Path.Combine(luaDir, item.FileName);
        if (!File.Exists(filePath))
        {
            // 尝试 AppID.lua
            filePath = Path.Combine(luaDir, $"{item.AppId}.lua");
            if (!File.Exists(filePath))
            {
                SetStatus("Lua 文件不存在", "Error");
                return;
            }
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{filePath}\"",
                UseShellExecute = true
            });
            SetStatus($"已打开 {Path.GetFileName(filePath)}", "Info");
        }
        catch (Exception ex)
        {
            SetStatus($"打开记事本失败: {ex.Message}", "Error");
        }
    }

    /// <summary>
    /// 删除入库
    /// </summary>
    [RelayCommand]
    private async Task DeleteItemAsync(LibraryItem? item = null)
    {
        item ??= LastRightClickedItem;
        if (item == null) return;

        var targets = SelectedItems.Count > 1 
            ? new List<LibraryItem>(SelectedItems) 
            : new List<LibraryItem> { item };
        var count = 0;

        foreach (var target in targets)
        {
            var (success, message) = await _luaService.DeleteLibraryItemAsync(target);
            if (success)
            {
                count++;
                SetStatus(message, "Success");
            }
            else
            {
                SetStatus(message, "Error");
            }
        }

        if (count > 0) await LoadLibraryAsync();
    }

    /// <summary>
    /// 在 Steam 商店查看
    /// </summary>
    [RelayCommand]
    private async Task ViewOnSteamStoreAsync(LibraryItem? item = null)
    {
        item ??= LastRightClickedItem;
        if (item == null) return;

        var url = $"https://store.steampowered.com/app/{item.AppId}";
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch { }
    }

    /// <summary>
    /// 选择全部
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        SelectedItems = new ObservableCollection<LibraryItem>(
            LibraryItems.Where(i => i.AppId != "N/A"));
    }

    /// <summary>
    /// 取消选择
    /// </summary>
    [RelayCommand]
    private void ClearSelection()
    {
        SelectedItems.Clear();
    }

    // ==================== 私有方法 ====================

    private List<LibraryItem> ApplySort(List<LibraryItem> items)
    {
        return SortMode switch
        {
            "az" => items.OrderBy(i => i.GameName).ToList(),
            "za" => items.OrderByDescending(i => i.GameName).ToList(),
            _ => items.OrderByDescending(i =>
            {
                if (int.TryParse(i.AppId, out var id)) return id;
                return 0;
            }).ToList(), // default: 按 appid 倒序
        };
    }

    private List<LibraryItem> ApplyFilter(List<LibraryItem> items)
    {
        if (string.IsNullOrWhiteSpace(SearchFilter)) return items;

        var filter = SearchFilter.ToLowerInvariant();
        return items.Where(i =>
            i.GameName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            i.AppId.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        // WinUI 3 clipboard
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        await Task.CompletedTask;
    }

    private void SetStatus(string message, string type)
    {
        StatusMessage = message;
        StatusType = type;
    }
}
