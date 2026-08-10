using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.ViewModels;

namespace OSTGUI.Pages;

public sealed partial class DenuvoPage : Page
{
    public DenuvoViewModel VM { get; }

    public DenuvoPage(DenuvoViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        this.DataContext = vm;
    }

    private void DenuvoSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 控件初始化阶段会提前触发一次，此时命名元素尚未就绪
        if (AccountsPanel == null || TransferPanel == null) return;

        var index = DenuvoSegmented.SelectedIndex;
        AccountsPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        TransferPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
    }
}
