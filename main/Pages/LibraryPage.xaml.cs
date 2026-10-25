using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Text;
using OSTGUI.Helpers;
using OSTGUI.Models;
using OSTGUI.Services;
using OSTGUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using WinRT;   // .As<T>()：AOT 下 XAML 侧资源的 CLR 硬转会抛 InvalidCastException（CsWinRT #2516）


namespace OSTGUI.Pages;

public sealed partial class LibraryPage : Page
{
    public LibraryViewModel VM { get; }

    /// <summary>无参构造：`Frame.Navigate` 需要它；VM 取单例 `MainViewModel` 上那一份（与侧边栏导航同源——
    /// 早先主页快捷入口用 DI 另取了一个实例，两处会拿到不同 VM）</summary>
    public LibraryPage() : this(App.Services.GetRequiredService<MainViewModel>().LibraryVM) { }

    public LibraryPage(LibraryViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        this.DataContext = vm;

        // 切换控件按上次选择回设（初始化期那次 SelectionChanged 已被 null 守卫挡掉）
        ViewSegmented.SelectedIndex = VM.IsGridView ? 1 : 0;

        Loaded += async (s, e) =>
        {
            if (VM.LibraryItems.Count == 0)
                await VM.LoadLibraryCommand.ExecuteAsync(null);
        };
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await VM.LoadLibraryCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 卡片被实体化时才加载封面（ListView 虚拟化：滚进视口才走到这里，滚出去回收后不再重复加载）。
    /// 这是框架自带的"按需内容"钩子，不需要给卡片包一层 UserControl
    /// </summary>
    private void LibraryList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            // 卡片滚出视口时指针可能还压着它：容器被复用时把悬浮态收掉，
            // 不然回收后的卡片会带着放大/高亮跑到别的位置去
            if (args.ItemContainer?.ContentTemplateRoot is Border recycled) CardHover.Exit(recycled);
            return;
        }
        if (args.Item is LibraryItem item)
            _ = VM.EnsureCoverAsync(item);   // 只加载一次；已加载的直接返回
    }

    /// <summary>网格视图的懒加载钩子：GridView 也继承 ListViewBase，语义与列表完全一样</summary>
    private void LibraryGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            if (args.ItemContainer?.ContentTemplateRoot is Border recycled) CardHover.Exit(recycled);
            return;
        }
        if (args.Item is LibraryItem item)
            _ = VM.EnsureCoverAsync(item);
    }

    // ==================== 卡片悬浮微交互 ====================
    // 缩放（ScaleTransform + Storyboard）、阴影（ThemeShadow）、高亮（背景刷）全部落在**卡片本身**上，
    // 不需要条目容器参与。实现与数值见 Helpers/CardHover.cs

    private void ListCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card) CardHover.Enter(card, ProbeCardHover.Background, CardHover.ListScale, CardHover.ListShadowZ);
    }

    private void ListCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card) CardHover.Exit(card);
    }

    private void GridCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card) CardHover.Enter(card, ProbeCardHover.Background, CardHover.GridScale, CardHover.GridShadowZ);
    }

    private void GridCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card) CardHover.Exit(card);
    }

    /// <summary>
    /// 视图形态切换。控件初始化阶段会提前触发一次，此时命名元素尚未就绪 → 守卫掉
    /// （照抄 NoSteamPage 的既有写法）
    ///
    /// 这里**不需要**再管已实体化卡片的封面：位图两档共用同一个解码宽度、只建一次，
    /// 切档只是换宿主显示同一张位图（按视图重建位图反而会造成"时糊时清"，见 VM 的注释）
    /// </summary>
    private void ViewSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryList == null || LibraryGrid == null) return;

        _ = VM.SetViewModeAsync(ViewSegmented.SelectedIndex == 1 ? "grid" : "list");
    }

    /// <summary>
    /// 版本切换器：顶部「保持最新版本」开关，下面是可选 build 列表（appinfo 的 depots.branches）。
    /// 只写 lua 的 setManifestid（不下载清单）——下载时由内核 manifest.lua 的请求码级联负责。
    /// </summary>
    private async void Version_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LibraryItem item }) return;
        VM.LastRightClickedItem = item;

        var luaService = App.Services.GetRequiredService<LuaConfigService>();
        var catalogService = App.Services.GetRequiredService<BuildCatalogService>();

        var content = await luaService.ReadLuaContentAsync(item.AppId);
        if (content is null)
        {
            (App.MainWindow as MainWindow)?.Notify("无法读取配置", $"未找到 {item.AppId}.lua");
            return;
        }

        var luaDepots = LuaConfigService.ExtractDepotIds(content);
        if (luaDepots.Count == 0)
        {
            (App.MainWindow as MainWindow)?.Notify("无法切换版本", "这个 lua 里没有可锁定的 depot");
            return;
        }

        var activeGids = BuildVersionPlanner.ParseActiveGids(content);   // 有未注释行 = 已锁定

        // ── 弹窗骨架 ────────────────────────────────────────────
        var panel = new StackPanel { Spacing = 12, Width = 520 };

        var keepLatest = new ToggleSwitch
        {
            Header = "保持最新版本",
            IsOn = activeGids.Count == 0,
            OnContent = "自动更新",
            OffContent = "锁定版本",
        };
        panel.Children.Add(keepLatest);
        panel.Children.Add(new TextBlock
        {
            Text = "开启时注释掉所有 setManifestid（跟随最新）；关闭时锁定到下面选中的 build。只改 lua，不下载清单。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
        });

        var status = new TextBlock { Text = "正在读取版本列表…", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var listPanel = new StackPanel { Spacing = 2 };
        var missingText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
        };
        var scroll = new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listPanel,
        };
        panel.Children.Add(status);
        panel.Children.Add(scroll);
        panel.Children.Add(missingText);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = $"版本 · {item.GameName}",
            Content = panel,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        // ⚠️ 宽度只能覆盖主题资源，不能设元素属性（见 doc/开发踩坑.md）
        dialog.Resources["ContentDialogMinWidth"] = 560.0;
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;

        BuildOption? selected = null;
        var radioButtons = new List<RadioButton>();
        var radioGroup = "ver-" + Guid.NewGuid().ToString("N")[..8];

        void UpdateMissingHint(BuildOption? option)
        {
            if (option is null || option.Covered >= option.Total)
            {
                missingText.Visibility = Visibility.Collapsed;
                return;
            }
            var lacking = luaDepots.Where(d => !option.Gids.ContainsKey(d)).OrderBy(d => d).ToList();
            missingText.Text = $"⚠ {lacking.Count} 个 depot 在这个 build 里没有清单，将保持自动更新：{string.Join("、", lacking)}";
            missingText.Visibility = Visibility.Visible;
        }

        keepLatest.Toggled += (_, _) =>
        {
            var listEnabled = !keepLatest.IsOn;
            foreach (var rb in radioButtons) rb.IsEnabled = listEnabled;
            dialog.IsPrimaryButtonEnabled = keepLatest.IsOn || selected is not null;
            if (keepLatest.IsOn) missingText.Visibility = Visibility.Collapsed;
            else UpdateMissingHint(selected);
        };

        dialog.Opened += async (_, _) =>
        {
            var catalog = await catalogService.GetAsync(item.AppId, luaDepots);
            status.Text = catalog.Ok
                ? "选择要锁定的 build："
                : (catalog.Error.Length > 0 ? catalog.Error : "没有可选版本");

            if (!catalog.Ok)
            {
                dialog.IsPrimaryButtonEnabled = keepLatest.IsOn;   // 至少还能"保持最新"
                return;
            }

            // 预选：当前锁定值与某个分支的 gid 完全一致就选它
            BuildOption? preSelected = null;
            if (activeGids.Count > 0)
            {
                preSelected = catalog.Builds.FirstOrDefault(b =>
                    b.Gids.Count == activeGids.Count &&
                    b.Gids.All(kv => activeGids.TryGetValue(kv.Key, out var g) && g == kv.Value));
            }

            foreach (var option in catalog.Builds)
            {
                var title = new TextBlock
                {
                    Text = option.Title,
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                };
                var sub = new TextBlock
                {
                    Text = option.SubTitle,
                    FontSize = 11,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                };
                var rowContent = new StackPanel { Spacing = 2 };
                rowContent.Children.Add(title);
                rowContent.Children.Add(sub);

                var rb = new RadioButton
                {
                    GroupName = radioGroup,
                    Content = rowContent,
                    IsEnabled = !keepLatest.IsOn,
                    Tag = option,
                };
                rb.Checked += (s, _) =>
                {
                    if (s is not RadioButton { Tag: BuildOption opt }) return;
                    selected = opt;
                    dialog.IsPrimaryButtonEnabled = true;
                    UpdateMissingHint(opt);
                };

                if (preSelected is not null && ReferenceEquals(option, preSelected))
                {
                    rb.IsChecked = true;
                    selected = option;
                    dialog.IsPrimaryButtonEnabled = true;
                    UpdateMissingHint(option);
                }

                radioButtons.Add(rb);
                listPanel.Children.Add(rb);
            }

            if (preSelected is null && activeGids.Count > 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = "当前锁定值不属于任何分支（可能被手改过），重新选择后会被覆盖。",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                });
            }
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (keepLatest.IsOn)
                {
                    var (ok, message) = await luaService.KeepLatestAsync(item.AppId);
                    if (!ok)
                    {
                        args.Cancel = true;
                        (App.MainWindow as MainWindow)?.Notify("切换失败", message);
                        return;
                    }
                    (App.MainWindow as MainWindow)?.Notify("已切回自动更新", message);
                }
                else
                {
                    if (selected is null)
                    {
                        args.Cancel = true;
                        return;
                    }
                    var (ok, message, _) = await luaService.ApplyBuildAsync(item.AppId, selected.Gids);
                    if (!ok)
                    {
                        args.Cancel = true;
                        (App.MainWindow as MainWindow)?.Notify("锁定失败", message);
                        return;
                    }
                    (App.MainWindow as MainWindow)?.Notify($"已锁定到 {selected.Title}", message);
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        Helpers.PopupTheme.Apply(dialog);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) await RefreshLibraryAfterDialogAsync();
    }


    /// <summary>弹窗关掉之后再刷新列表：避免把重扫塞进按钮 deferral 里导致黑一帧（同 Lua 编辑器那次）。</summary>
    private async Task RefreshLibraryAfterDialogAsync()
    {
        if (VM.LoadLibraryCommand.CanExecute(null))
            await VM.LoadLibraryCommand.ExecuteAsync(null);
    }

    /// <summary>更多菜单里的「删除」（红色）。菜单打开时 LastRightClickedItem 已由 More_Click 设好</summary>
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (VM.LastRightClickedItem is { } item)
            VM.DeleteItemCommand.Execute(item);
    }

    private void Info_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LibraryItem item)
        {
            ShowInstallInfoDialog(item);
        }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LibraryItem item)
        {
            VM.LastRightClickedItem = item;
            var menu = Resources["MoreMenu"].As<MenuFlyout>();
            menu.ShowAt(btn, new Windows.Foundation.Point(0, btn.ActualHeight));
        }
    }

    /// <summary>
    /// 「编辑 Lua」：打开应用内编辑器（替代原来的记事本）。
    /// 保存走 LuaConfigService 的原子写；内核 LuaFileWatcher 会热重载，无需重启 Steam。
    /// </summary>
    private async void EditLua_Click(object sender, RoutedEventArgs e)
    {
        var item = VM.LastRightClickedItem;
        if (item is null) return;

        var luaService = App.Services.GetRequiredService<LuaConfigService>();
        var editor = new Views.LuaEditorView(luaService, item.AppId, item.GameName);

        var (loaded, loadMessage) = await editor.LoadAsync();
        if (!loaded)
        {
            (App.MainWindow as MainWindow)?.Notify("无法编辑", loadMessage);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Content = editor,
            Title = "编辑 Lua",
            PrimaryButtonText = "保存",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };

        // ⚠️ 宽度只能这样给：ContentDialog 模板里的 BackgroundElement 用的是**主题资源**
        // ContentDialogMinWidth/MaxWidth（默认 320/548），所以
        //   · 给对话框自己设 MinWidth/MaxWidth 没用，还会把模板根（含遮罩）夹窄、弹窗被顶到左边；
        //   · 正解是在对话框的 Resources 里覆盖这两个主题资源，内容再按需定 MinWidth。
        dialog.Resources["ContentDialogMinWidth"] = 560.0;
        dialog.Resources["ContentDialogMaxWidth"] = 1000.0;

        // 只有"内容与打开那一刻不同"时才让「保存」可点；「关闭」永远直接关掉、不写盘。
        dialog.IsPrimaryButtonEnabled = false;
        editor.DirtyStateChanged += (_, _) => dialog.IsPrimaryButtonEnabled = editor.HasUnsavedChanges;

        // 保存成功只置标记；**列表刷新一定要等弹窗真正关掉之后再做**——
        // 以前是在 PrimaryButtonClick 的 deferral 里 await 全量重扫（几十个条目 + 封面），
        // 弹窗在这段时间被挡住不关，popup 拆掉时整窗会黑一帧（用户实测"点保存后界面黑一下"）。
        bool saved = false;

        // Ctrl+S：走同一条保存路径（没改动就什么都不做）
        editor.SaveRequested += async (_, _) =>
        {
            if (!editor.HasUnsavedChanges) return;
            var (ok, message) = await editor.SaveAsync();
            if (!ok)
            {
                (App.MainWindow as MainWindow)?.Notify("保存失败", message);
                return;
            }
            saved = true;
            (App.MainWindow as MainWindow)?.Notify("已保存", $"{item.AppId}.lua 已写入，内核将自动热重载");
            dialog.Hide();
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var (ok, message) = await editor.SaveAsync();
                if (!ok)
                {
                    args.Cancel = true;
                    (App.MainWindow as MainWindow)?.Notify("保存失败", message);
                    return;
                }
                saved = true;
                (App.MainWindow as MainWindow)?.Notify("已保存", $"{item.AppId}.lua 已写入，内核将自动热重载");
            }
            finally
            {
                deferral.Complete();
            }
        };

        Helpers.PopupTheme.Apply(dialog);
        await dialog.ShowAsync();

        // 关窗之后再刷新：此时遮罩已拆、界面已稳定，重扫不会再遮挡渲染
        if (saved) await VM.LoadLibraryCommand.ExecuteAsync(null);
    }

    private void CopyAppId_Click(object sender, RoutedEventArgs e)
    {
        VM.CopyAppIdCommand.Execute(null);
    }

    private void CopyGameName_Click(object sender, RoutedEventArgs e)
    {
        if (VM.LastRightClickedItem != null)
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(VM.LastRightClickedItem.GameName);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            // 应用内通知（窗口顶部，见 MainWindow.Notify）——不再走系统通知中心
            (App.MainWindow as MainWindow)?.Notify("已复制", $"游戏名称：{VM.LastRightClickedItem.GameName}");
        }
    }

    private void RepairVersionConfig_Click(object sender, RoutedEventArgs e)
    {
        VM.RepairVersionConfigCommand.Execute(null);
    }

    private async void ShowInstallInfoDialog(LibraryItem item)
    {
        if (this.XamlRoot == null) return;
        // 顶部信息区：游戏名 / AppID / 版本模式，全部居中（弹窗不再单独放标题，名字就是标题）
        var headerPanel = new StackPanel { Spacing = 6, Padding = new Thickness(0, 0, 0, 12) };
        headerPanel.Children.Add(new TextBlock
        {
            Text = item.GameName,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = $"AppID: {item.AppId}",
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            Foreground = ProbeSecondary.Foreground
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = $"版本模式：{item.VersionModeText}",
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            Foreground = ProbeSecondary.Foreground
        });

        // DLC 过滤器：全部 / 已入库 / 未入库（单选按钮组）
        var allDlcRadio = new RadioButton { Content = "全部 DLC", GroupName = "DlcFilter", IsChecked = true };
        var installedRadio = new RadioButton { Content = "已入库", GroupName = "DlcFilter" };
        var uninstalledRadio = new RadioButton { Content = "未入库", GroupName = "DlcFilter" };
        var filterPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        filterPanel.Children.Add(allDlcRadio);
        filterPanel.Children.Add(installedRadio);
        filterPanel.Children.Add(uninstalledRadio);

        // DLC 列表（先显示转圈，后台加载完成后再填充）
        var dlcScroll = new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dlcPanel = new StackPanel { Spacing = 8, Padding = new Thickness(0, 8, 0, 0) };
        dlcScroll.Content = dlcPanel;

        var dlcLoading = true;
        var dlcSpinner = new ProgressRing { IsActive = true, Width = 24, Height = 24, Margin = new Thickness(0, 8, 0, 0) };

        // 画刷从本页 XAML 取样点读（{ThemeResource} 按应用主题解析）；
        // 不能再用 Application.Current.Resources[...]：那个走系统主题，浅色应用 + 深色系统会把弹窗染深
        var secondaryBrush = ProbeSecondary.Foreground;
        var cardBrush = ProbeCard.Background;
        var successBrush = ProbeSuccess.Foreground;

        void RebuildDlcPanel()
        {
            dlcPanel.Children.Clear();

            // 加载中只显示转圈，过滤按钮点击也不闪"无 DLC"
            if (dlcLoading)
            {
                dlcPanel.Children.Add(dlcSpinner);
                return;
            }

            var mode = installedRadio.IsChecked == true ? 1 : uninstalledRadio.IsChecked == true ? 2 : 0;
            IEnumerable<DlcInfo> list = item.DlcList;
            if (mode == 1) list = item.DlcList.Where(d => d.IsInstalled);
            else if (mode == 2) list = item.DlcList.Where(d => !d.IsInstalled);

            var items = list.ToList();
            if (items.Count == 0)
            {
                dlcPanel.Children.Add(new TextBlock
                {
                    Text = mode switch
                    {
                        1 => "暂无已入库的 DLC",
                        2 => "暂无未入库的 DLC",
                        _ => "无 DLC"
                    },
                    FontSize = 13,
                    Foreground = secondaryBrush
                });
                return;
            }

            foreach (var dlc in items)
            {
                var dlcBorder = new Border
                {
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(6),
                    Background = cardBrush
                };
                var dlcStack = new StackPanel { Spacing = 4 };
                dlcStack.Children.Add(new TextBlock { Text = dlc.Name, FontWeight = FontWeights.SemiBold, FontSize = 13 });
                dlcStack.Children.Add(new TextBlock { Text = $"AppID: {dlc.AppId}", FontSize = 12, Foreground = secondaryBrush });
                dlcStack.Children.Add(new TextBlock
                {
                    Text = dlc.StatusText,
                    FontSize = 11,
                    Foreground = dlc.IsInstalled ? successBrush : secondaryBrush
                });
                dlcBorder.Child = dlcStack;
                dlcPanel.Children.Add(dlcBorder);
            }
        }

        allDlcRadio.Checked += (s, e) => RebuildDlcPanel();
        installedRadio.Checked += (s, e) => RebuildDlcPanel();
        uninstalledRadio.Checked += (s, e) => RebuildDlcPanel();
        RebuildDlcPanel();

        // 后台实时拉取 DLC（不阻塞对话框打开），完成后填充
        _ = LoadDlcBackgroundAsync();
        async Task LoadDlcBackgroundAsync()
        {
            try
            {
                await VM.LoadDlcInfoAsync(item);
            }
            catch { }
            dlcLoading = false;
            RebuildDlcPanel();
        }

        // 主布局
        var rootPanel = new StackPanel { Spacing = 12, MaxHeight = 500 };
        rootPanel.Children.Add(headerPanel);
        rootPanel.Children.Add(new TextBlock { Text = "DLC", FontSize = 14, FontWeight = FontWeights.SemiBold });
        rootPanel.Children.Add(filterPanel);
        rootPanel.Children.Add(dlcScroll);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Content = rootPanel,
            CloseButtonText = "关闭",
            PrimaryButtonText = "Steam 商店",
            SecondaryButtonText = "SteamDB"
        };

        Helpers.PopupTheme.Apply(dialog);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var url = $"https://store.steampowered.com/app/{item.AppId}";
            _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(url));
        }
        else if (result == ContentDialogResult.Secondary)
        {
            var url = $"https://steamdb.info/app/{item.AppId}/";
            _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(url));
        }
    }
}