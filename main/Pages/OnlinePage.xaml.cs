using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.ViewModels;

namespace OSTGUI.Pages;

public sealed partial class OnlinePage : Page
{
    public OnlineViewModel VM { get; }
    private DispatcherTimer? _statusTimer;

    public OnlinePage(OnlineViewModel vm)
    {
        this.InitializeComponent();
        VM = vm;
        this.DataContext = vm;

        Loaded += (s, e) =>
        {
            VM.RefreshRunningState();
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (_, _) => VM.RefreshRunningState();
            _statusTimer.Start();
        };

        Unloaded += (s, e) =>
        {
            _statusTimer?.Stop();
            _statusTimer = null;
        };
    }

    /// <summary>切换联机方式视图（0 = 480 联机，1 = 其他）</summary>
    private void OnlineSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 控件初始化阶段会提前触发一次，此时命名元素尚未就绪
        if (FixView == null || OtherView == null) return;

        var index = OnlineSegmented.SelectedIndex;
        FixView.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        OtherView.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
    }
}
