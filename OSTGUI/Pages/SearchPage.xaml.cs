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
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = result.Name,
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
