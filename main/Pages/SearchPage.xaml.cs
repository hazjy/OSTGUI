using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OSTGUI.Helpers;
using OSTGUI.Models;
using OSTGUI.Services;
using OSTGUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.Extensions.DependencyInjection;


namespace OSTGUI.Pages;

public sealed partial class SearchPage : Page
{
    public SearchViewModel VM { get; }
    public SearchViewModel ViewModel => VM;

    /// <summary>无参构造：`Frame.Navigate` 需要它</summary>
    public SearchPage() : this(App.Services.GetRequiredService<MainViewModel>().SearchVM) { }

    public SearchPage(SearchViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        this.DataContext = VM;

        // 切换控件按上次选择回设（初始化期那次 SelectionChanged 已被 null 守卫挡掉）
        ViewSegmented.SelectedIndex = VM.IsGridView ? 1 : 0;
    }

    // ==================== 卡片悬浮微交互 ====================
    // 与入库管理同款：缩放/阴影/高亮都落在卡片本身，实现与数值见 Helpers/CardHover.cs

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

    /// <summary>视图形态切换（与入库管理同款；控件初始化期会提前触发一次 → 守卫掉）</summary>
    private void ViewSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList == null || ResultGrid == null) return;

        _ = VM.SetViewModeAsync(ViewSegmented.SelectedIndex == 1 ? "grid" : "list");
    }

    private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (sender is TextBox tb)
                VM.SearchQuery = tb.Text;
            LogService.Clear();
            _ = VM.SearchCommand.ExecuteAsync(null);
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        LogService.Clear();
        _ = VM.SearchCommand.ExecuteAsync(null);
    }

    private async void ListAddGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SearchResult result)
        {
            await VM.AddGameCommand.ExecuteAsync(result);
        }
    }

    /// <summary>取消任务（按钮只在入库进行中可见）；打断点与语义见 SearchViewModel.CancelAdd</summary>
    private void CancelAddButton_Click(object sender, RoutedEventArgs e)
    {
        VM.CancelAddCommand.Execute(null);
    }

    private async void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SearchResult result)
        {
            ShowInfoDialog(result);
        }
    }

    private async void ShowInfoDialog(SearchResult result)
    {
        // AppID 行 + 透明底复制图标（横向排列，图标随 AppID 文本长度自动右移）
        var appIdText = new TextBlock
        {
            Text = $"AppID：{result.AppId}",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        var copyButton = new Button
        {
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 0,
            MinHeight = 0,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 14 },
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(copyButton, "复制 AppID");
        copyButton.Click += (_, _) =>
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(result.AppId);
            Clipboard.SetContent(dataPackage);
        };

        var appIdRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        appIdRow.Children.Add(appIdText);
        appIdRow.Children.Add(copyButton);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = result.Name,
            Content = appIdRow,
            PrimaryButtonText = "Steam 商店",
            SecondaryButtonText = "SteamDB",
            CloseButtonText = "关闭"
        };

        Helpers.PopupTheme.Apply(dialog);
        var resultDialog = await dialog.ShowAsync();
        if (resultDialog == ContentDialogResult.Primary)
        {
            var url = $"https://store.steampowered.com/app/{result.AppId}";
            _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(url));
        }
        else if (resultDialog == ContentDialogResult.Secondary)
        {
            var url = $"https://steamdb.info/app/{result.AppId}/";
            _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(url));
        }
    }
}
