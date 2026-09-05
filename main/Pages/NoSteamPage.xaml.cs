using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.ViewModels;

namespace OSTGUI.Pages;

public sealed partial class NoSteamPage : Page
{
    public NoSteamViewModel VM { get; }

    public NoSteamPage(NoSteamViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        DataContext = VM;
    }

    private void NoSteamSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 控件初始化阶段会提前触发一次，此时命名元素尚未就绪
        if (NoSteamPanel == null || UbisoftPanel == null) return;

        var index = NoSteamSegmented.SelectedIndex;
        NoSteamPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        UbisoftPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
    }
}