using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Services;
using OSTGUI.ViewModels;

namespace OSTGUI.Views;

/// <summary>480 联机（内核 -onlinefix）视图</summary>
public sealed partial class OnlineFixView : UserControl
{
    public OnlineFixView() => this.InitializeComponent();

    private OnlineViewModel? VM => DataContext as OnlineViewModel;

    private async void QueryName_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;
        await VM.LoadGameNameAsync();
    }

    private async void StartOnlineFix_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        var (ok, msg) = await VM.StartAsync();
        if (ok)
            ToastService.ShowInfo("480 联机", msg);
        else
            ToastService.ShowError("480 联机失败", msg);
    }

    private void StopOnlineFix_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        var (ok, msg) = VM.Stop();
        if (ok)
            ToastService.ShowInfo("480 联机", msg);
        else
            ToastService.ShowWarning("480 联机", msg);
    }

    /// <summary>使用说明：弹出说明窗口（弹窗定义在 XAML 里，见 OnlineGuideDialog）</summary>
    private async void ViewOnlineGuide_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            OnlineGuideDialog.XamlRoot = this.XamlRoot;   // WinUI 3 必须显式给 XamlRoot
            await OnlineGuideDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            ToastService.ShowError("打开使用说明失败", ex.Message);
        }
    }

    /// <summary>复制 480 安装命令</summary>
    private void CopyInstallCmd_Click(object sender, RoutedEventArgs e)
    {
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText("steam://install/480");
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        ToastService.ShowInfo("已复制", "steam://install/480");
    }
}
