using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using OSTGUI.Models;
using OSTGUI.ViewModels;

namespace OSTGUI.Pages;

public sealed partial class LibraryPage : Page
{
    public LibraryViewModel VM { get; }

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
        if (args.InRecycleQueue) return;
        if (args.Item is LibraryItem item)
            _ = VM.EnsureCoverAsync(item);   // 只加载一次；已加载的直接返回
    }

    /// <summary>网格视图的懒加载钩子：GridView 也继承 ListViewBase，语义与列表完全一样</summary>
    private void LibraryGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Item is LibraryItem item)
            _ = VM.EnsureCoverAsync(item);
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

    private async void ToggleVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LibraryItem item)
        {
            VM.LastRightClickedItem = item;
            await VM.ToggleVersionCommand.ExecuteAsync(item);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LibraryItem item)
        {
            VM.LastRightClickedItem = item;
            VM.DeleteItemCommand.Execute(item);
        }
    }

    private void Info_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LibraryItem item)
        {
            ShowInfoDialog(item);
        }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LibraryItem item)
        {
            VM.LastRightClickedItem = item;
            var menu = (MenuFlyout)Resources["MoreMenu"];
            menu.ShowAt(btn, new Windows.Foundation.Point(0, btn.ActualHeight));
        }
    }

    private void EditLua_Click(object sender, RoutedEventArgs e)
    {
        VM.EditLuaCommand.Execute(null);
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
            Services.ToastService.ShowSuccess("已复制", $"游戏名称: {VM.LastRightClickedItem.GameName}");
        }
    }

    private void RepairVersionConfig_Click(object sender, RoutedEventArgs e)
    {
        VM.RepairVersionConfigCommand.Execute(null);
    }

    private async void InstallInfo_Click(object sender, RoutedEventArgs e)
    {
        if (VM.LastRightClickedItem != null)
        {
            // 立即打开对话框，DLC 列表在后台加载（转圈），不再先等 API 再弹窗
            ShowInstallInfoDialog(VM.LastRightClickedItem);
        }
    }

    private async void ShowInfoDialog(LibraryItem item)
    {
        if (this.XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = $"{item.GameName} ({item.AppId})",
            PrimaryButtonText = "Steam 商店",
            SecondaryButtonText = "SteamDB",
            CloseButtonText = "关闭",
            Content = new StackPanel { Spacing = 8, Children =
            {
                new TextBlock { Text = $"AppID: {item.AppId}" },
                new TextBlock { Text = $"版本模式: {item.VersionModeText}" }
            }}
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

    private async void ShowInstallInfoDialog(LibraryItem item)
    {
        if (this.XamlRoot == null) return;
        // 顶部信息区
        var headerPanel = new StackPanel { Spacing = 8, Padding = new Thickness(0, 0, 0, 12) };
        headerPanel.Children.Add(new TextBlock { Text = $"AppID: {item.AppId}", FontSize = 14, FontWeight = FontWeights.SemiBold });
        headerPanel.Children.Add(new TextBlock { Text = $"游戏名称: {item.GameName}", FontSize = 13 });
        headerPanel.Children.Add(new TextBlock { Text = $"版本模式: {item.VersionModeText}", FontSize = 13 });

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
                        1 => "没有已入库的 DLC",
                        2 => "没有未入库的 DLC",
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
            Title = $"入库信息 - {item.GameName}",
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
