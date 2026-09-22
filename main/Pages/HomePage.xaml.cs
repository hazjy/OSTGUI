using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.ViewModels;
using OSTGUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OSTGUI.Pages;

public sealed partial class HomePage : Page
{
    public MainViewModel VM { get; }

    /// <summary>无参构造：`Frame.Navigate` 需要它</summary>
    public HomePage() : this(App.Services.GetRequiredService<MainViewModel>()) { }

    public HomePage(MainViewModel mainVM)
    {
        this.InitializeComponent();
        VM = mainVM;
        this.DataContext = VM;

        Loaded += (s, e) => VM.RefreshOstStatus();
    }

    private void GoToSearch_Click(object sender, RoutedEventArgs e)
    {
        NavigateMain("search");
    }

    private void GoToLibrary_Click(object sender, RoutedEventArgs e)
    {
        NavigateMain("library");
    }

    private void FreeVip_Click(object sender, RoutedEventArgs e)
    {
        // 你被骗了 🎵 Never gonna give you up 🎵
        var url = "https://www.bilibili.com/video/BV1GJ411x7h7";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    private void SteamPP_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender,
                               Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://steampp.net/",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    private void NavigateMain(string tag)
    {
        // 统一走 MainWindow 的入口：那里用的 `Frame.Navigate` 才会播原生 page-refresh 过渡
        if (App.MainWindow is MainWindow mainWin) mainWin.GoToPage(tag);

        // 同步更新侧边栏选中状态
        SyncNavigationSelection(tag);
    }

    private static void SyncNavigationSelection(string tag)
    {
        if (App.MainWindow is not MainWindow mainWin) return;
        var nav = mainWin.NavView;

        foreach (var item in nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag?.ToString() == tag)
            {
                nav.SelectedItem = item;
                return;
            }
        }
        foreach (var item in nav.FooterMenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag?.ToString() == tag)
            {
                nav.SelectedItem = item;
                return;
            }
        }
    }
}
