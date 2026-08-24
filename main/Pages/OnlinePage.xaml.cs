using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Services;
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

    private void OnlineSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 控件初始化阶段会提前触发一次，此时命名元素尚未就绪
        if (OnlineFixPanel == null || OtherPanel == null) return;

        var index = OnlineSegmented.SelectedIndex;
        OnlineFixPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        OtherPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void KernelMode_Checked(object sender, RoutedEventArgs e)
    {
        VM.UseCompatMode = false;
        if (CompatPanel != null) CompatPanel.Visibility = Visibility.Collapsed;
    }

    private void CompatMode_Checked(object sender, RoutedEventArgs e)
    {
        VM.UseCompatMode = true;
        if (CompatPanel == null) return;
        CompatPanel.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(VM.InstallDir))
            _ = VM.LoadCompatInfoAsync();
    }

    private async void RefreshCompat_Click(object sender, RoutedEventArgs e)
        => await VM.LoadCompatInfoAsync();

    private async void QueryName_Click(object sender, RoutedEventArgs e)
        => await VM.LoadGameNameAsync();

    private async void StartOnlineFix_Click(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = await VM.StartAsync();
        if (ok)
            ToastService.ShowInfo("480 联机", msg);
        else
            ToastService.ShowError("480 联机失败", msg);
    }

    private void StopOnlineFix_Click(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = VM.Stop();
        if (ok)
            ToastService.ShowInfo("480 联机", msg);
        else
            ToastService.ShowWarning("480 联机", msg);
    }
}
