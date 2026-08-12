using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OSTGUI.Pages;

public sealed partial class OnlinePage : Page
{
    public OnlinePage()
    {
        this.InitializeComponent();
    }

    private void OnlineSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 控件初始化阶段会提前触发一次，此时命名元素尚未就绪
        if (OnlineFixPanel == null || OtherPanel == null) return;

        var index = OnlineSegmented.SelectedIndex;
        OnlineFixPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        OtherPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
    }
}
