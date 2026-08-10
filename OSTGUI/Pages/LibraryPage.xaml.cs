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

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LibraryItem item)
        {
            VM.LastRightClickedItem = item;
            await VM.RepairManifestCommand.ExecuteAsync(item);
        }
    }

    private void EditLua_Click(object sender, RoutedEventArgs e)
    {
        VM.EditLuaCommand.Execute(null);
    }

    private async void RepairVersionConfig_Click(object sender, RoutedEventArgs e)
    {
        if (VM.LastRightClickedItem != null)
            await VM.RepairVersionConfigCommand.ExecuteAsync(VM.LastRightClickedItem);
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

    private void InstallInfo_Click(object sender, RoutedEventArgs e)
    {
        if (VM.LastRightClickedItem != null)
        {
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
                new TextBlock { Text = $"版本模式: {item.VersionModeText}" },
                new TextBlock { Text = $"状态: {item.StatusText}" }
            }}
        };

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
        headerPanel.Children.Add(new TextBlock { Text = $"入库状态: {item.StatusText}", FontSize = 13 });
        headerPanel.Children.Add(new TextBlock { Text = $"版本模式: {item.VersionModeText}", FontSize = 13 });

        // DLC 过滤器：全部 / 已入库 / 未入库（单选按钮组）
        var allDlcRadio = new RadioButton { Content = "全部 DLC", GroupName = "DlcFilter", IsChecked = true };
        var installedRadio = new RadioButton { Content = "已入库", GroupName = "DlcFilter" };
        var uninstalledRadio = new RadioButton { Content = "未入库", GroupName = "DlcFilter" };
        var filterPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        filterPanel.Children.Add(allDlcRadio);
        filterPanel.Children.Add(installedRadio);
        filterPanel.Children.Add(uninstalledRadio);

        // DLC 列表
        var dlcScroll = new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dlcPanel = new StackPanel { Spacing = 8, Padding = new Thickness(0, 8, 0, 0) };
        dlcScroll.Content = dlcPanel;

        var secondaryBrush = Application.Current.Resources["TextFillColorSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush;
        var cardBrush = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush;
        var successBrush = Application.Current.Resources["SystemFillColorSuccessBrush"] as Microsoft.UI.Xaml.Media.Brush
            ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 150, 80));

        void RebuildDlcPanel()
        {
            dlcPanel.Children.Clear();

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
