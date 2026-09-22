using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
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
    private readonly SteamGameInfoService _gameInfoService;
    private readonly SteamService _steamService;
    private readonly ConfigService _configService;
    private readonly GameNameCacheService _nameCache;
    private readonly CoverImageService _coverService;

    [ObservableProperty] private ObservableCollection<LibraryItem> _libraryItems = new();
    [ObservableProperty] private ObservableCollection<LibraryItem> _selectedItems = new();
    [ObservableProperty] private LibraryItem? _lastRightClickedItem;
    // 全量主列表：排序/过滤的数据源；LibraryItems 仅是它的视图投影
    private List<LibraryItem> _allItems = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isLoading;

    public bool IsBusy => IsLoading;

    /// <summary>入库管理视图形态：list / grid。持久化在 config.json 的 LibraryViewMode</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView), nameof(IsGridView))]
    private string _viewMode = "list";

    public bool IsListView => ViewMode != "grid";
    public bool IsGridView => ViewMode == "grid";

    /// <summary>封面解码宽度（逻辑像素）= **网格**卡片图框的宽度（200×94）。列表卡片图框是 120×56，
    /// 用这张 450 物理像素的位图显示只是降采样，**比按 120 解更锐**。
    /// 要害是**两档共用同一个宽度、位图只建一次**：按视图来回重建的话，任何一次没重建到，
    /// 卡片就会带着另一档的宽度显示（网格里 1.67× 放大 = 糊）——2026-09-22 的"时糊时清"就是这么来的</summary>
    private const int CoverDecodeWidth = 200;

    /// <summary>页面标题：入库管理 + 游戏总数，计数用 CJK 角括号「」括住（U+300C / U+300D）。
    /// WinUI 的 {Binding} 不支持 StringFormat，故按惯例在 VM 里拼；0（还没扫完）时只显示"入库管理"，免得闪一下</summary>
    public string TitleText => TotalCount > 0 ? $"入库管理 「{TotalCount}」" : "入库管理";

    [ObservableProperty] private string _searchFilter = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleText))]
    private int _totalCount;
    [ObservableProperty] private int _fixedCount;
    [ObservableProperty] private int _autoCount;
    [ObservableProperty] private double _progressValue;

    public LibraryViewModel(
        LuaConfigService luaService,
        GameSearchService searchService,
        SteamGameInfoService gameInfoService,
        SteamService steamService,
        ConfigService configService,
        GameNameCacheService nameCache,
        CoverImageService coverService)
    {
        _luaService = luaService;
        _searchService = searchService;
        _gameInfoService = gameInfoService;
        _steamService = steamService;
        _configService = configService;
        _nameCache = nameCache;
        _coverService = coverService;

        // 视图形态跟着上次选择（脏值一律归一到 list）
        _viewMode = configService.Config.LibraryViewMode == "grid" ? "grid" : "list";
    }

    /// <summary>
    /// 加载入库游戏列表
    /// </summary>
    [RelayCommand]
    private async Task LoadLibraryAsync()
    {
        IsLoading = true;
        _allItems = new List<LibraryItem>();
        LibraryItems = new ObservableCollection<LibraryItem>();
        TotalCount = 0;
        FixedCount = 0;
        AutoCount = 0;
        ProgressValue = 0;

        try
        {
            var items = await _luaService.ScanLibraryAsync();
            ProgressValue = 30;

            // 1) 显示名直接读缓存（零联网）；2) 缺失名的主游戏后台静默补（限流在批量方法内）。
            //    DLC/depot 引用 ID 不是有效游戏，不参与取名（避免每次启动对它们无效重试）
            var luaDir = _steamService.GetLuaConfigDir() ?? "";
            var mainIds = items
                .Where(i => i.AppId != "N/A" && !string.IsNullOrEmpty(i.GameName) && i.GameName.StartsWith("AppID")
                    && File.Exists(Path.Combine(luaDir, i.AppId + ".lua")))
                .Select(i => i.AppId)
                .Distinct()
                .ToList();

            foreach (var item in items)
            {
                if (_nameCache.TryGet(item.AppId, out var name))
                    item.GameName = name;
            }
            ProgressValue = 50;

            // 后台补名：成功后下次刷新列表即显示新名（不阻塞页面）
            if (mainIds.Count > 0)
                _ = BackfillNamesAsync(mainIds);

            // DLC 信息不再刷新时预加载，改为点"入库信息"时按需获取

            // 应用排序
            _allItems = ApplySort(items);

            ProgressValue = 90;

            // 刷新视图（应用搜索过滤）
            RefreshView();

            // 封面不在这里预加载：由页面在卡片实体化时逐条调 EnsureCoverAsync（懒加载）
            // （全量预加载会在进页面时给每个 appid 都发请求/读盘/建位图，库大了就是白等）

            ProgressValue = 100;
        }
        catch { }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 后台静默补主游戏名（缓存缺失/过期项，限流并发，失败不影响页面）
    /// </summary>
    private async Task BackfillNamesAsync(List<string> mainIds)
    {
        try
        {
            await _searchService.GetGameNamesBatchAsync(mainIds);
        }
        catch { }
    }

    /// <summary>
    /// 切入库管理的视图形态：list / grid。写盘失败不影响切换（内存态优先，
    /// 下次重开最多回到上一档，不为此弹错）
    /// </summary>
    public async Task SetViewModeAsync(string mode)
    {
        mode = mode == "grid" ? "grid" : "list";
        if (ViewMode == mode) return;

        ViewMode = mode;
        try { await _configService.UpdateAndSaveAsync(c => c.LibraryViewMode = mode); }
        catch { }
    }

    /// <summary>
    /// 按需加载单条封面：由页面在卡片实体化（滚进视口）时调用，不预先全量加载。
    /// 并发上限在 CoverImageService（信号量）里；这里只把拿到的文件变成 BitmapImage——
    /// 必须在 UI 线程调用/赋值（BitmapImage 是 DependencyObject）。
    ///
    /// **只加载一次**：拿到就不再动它（切档也不重建，理由见 <see cref="CoverDecodeWidth"/>）
    /// </summary>
    public async Task EnsureCoverAsync(LibraryItem item)
    {
        if (item.Cover != null) return;
        try
        {
            var path = await _coverService.EnsureCoverFileAsync(item.AppId);
            if (path != null)
                item.Cover = CreateBitmap(path);
        }
        catch { }
    }

    /// <summary>
    /// 由本地封面文件构造位图。DecodePixelWidth 必须先于 UriSource 设置，
    /// 否则按原图 460×215 全量解码，几十张就是几十 MB
    /// </summary>
    private static BitmapImage CreateBitmap(string path)
    {
        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelWidth = CoverDecodeWidth
        };
        bitmap.UriSource = new Uri(path);
        return bitmap;
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
    /// 搜索关键词变化时触发输入即过滤
    /// </summary>
    partial void OnSearchFilterChanged(string value) => RefreshView();

    /// <summary>
    /// 以全量主列表为源，应用排序与搜索过滤后刷新视图
    /// </summary>
    private void RefreshView()
    {
        var sorted = ApplySort(_allItems);
        var filtered = ApplyFilter(sorted);
        LibraryItems = new ObservableCollection<LibraryItem>(filtered);

        // 统计始终基于全库，不随搜索过滤变化
        TotalCount = _allItems.Count(i => i.AppId != "N/A");
        FixedCount = _allItems.Count(i => i.VersionMode == "fixed");
        AutoCount = _allItems.Count(i => i.VersionMode == "auto");
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
                // 成功提示可在设置中开关
                if (_configService.Config.ShowVersionChangeNotifications)
                    Services.ToastService.ShowSuccess("版本状态更改", message);
            }
            else
            {
                // 失败通知始终显示（重要错误信息）
                Services.ToastService.ShowError("版本状态更改", message);
            }
        }
    }

/// <summary>
    /// 补齐版本配置：从 CDN 获取 depot / GID，写入注释形式的 setManifestid 对应关系（不下载清单）
    /// </summary>
    [RelayCommand]
    private async Task RepairVersionConfigAsync(LibraryItem? item = null)
    {
        item ??= LastRightClickedItem;
        if (item == null || item.AppId == "N/A") return;

        IsLoading = true;

        try
        {
            var gameDetails = await _gameInfoService.GetGameDetailsFromSteamAsync(item.AppId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
            {
                Services.ToastService.ShowError("补齐版本配置", "获取 depot 信息失败");
                return;
            }

            var depots = gameDetails.Depots.Values
                .Select(d => (depotId: d.DepotId, manifestGid: d.Manifests.Count > 0 ? d.Manifests[0] : ""))
                .ToList();
            // 允许空 manifestGid，LuaConfigService 会根据 Lua 文件中的 depot 列表自动豁免无清单的 depot

            var (success, message) = await _luaService.RepairVersionConfigAsync(item.AppId, depots);
            if (success)
            {
                Services.ToastService.ShowSuccess("补齐版本配置", message);
                await LoadLibraryAsync();
            }
            else
            {
                Services.ToastService.ShowError("补齐版本配置", message);
            }
        }
        finally
        {
            IsLoading = false;
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
            CopyToClipboard(ids);
        }
        else
        {
            CopyToClipboard(item.AppId);
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
        }
        catch { }
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
            }
            else
            {
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
        // 默认排序：AppID 倒序（最新入库在前）
        return items.OrderByDescending(i =>
        {
            if (int.TryParse(i.AppId, out var id)) return id;
            return 0;
        }).ToList();
    }

    private List<LibraryItem> ApplyFilter(List<LibraryItem> items)
    {
        if (string.IsNullOrWhiteSpace(SearchFilter)) return items;

        var filter = SearchFilter.ToLowerInvariant();
        return items.Where(i =>
            i.GameName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            i.AppId.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static void CopyToClipboard(string text)
    {
        // WinUI 3 clipboard
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }


}
