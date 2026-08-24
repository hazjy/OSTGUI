using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Models;
using OSTGUI.Services;
using OSTGUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace OSTGUI.Pages;

public sealed partial class SearchPage : Page
{
    public SearchViewModel VM { get; }
    public SearchViewModel ViewModel => VM;

    public SearchPage(SearchViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        this.DataContext = VM;
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

    private async void ListShareButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SearchResult result)
        {
            ShowShareDialog(result);
        }
    }

    private async void ShowShareDialog(SearchResult result)
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
