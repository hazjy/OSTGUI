using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Services;
using OSTGUI.ViewModels;
using Windows.Storage.Pickers;

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

    // ==================== 免育碧（Phase 1） ====================

    private UbisoftDeploymentService UbisoftSvc
        => App.Services.GetRequiredService<UbisoftDeploymentService>();

    private async void BrowseUbisoftDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        UbisoftDirBox.Text = folder.Path;
        var loader = UbisoftSvc.DetectLoader(folder.Path);
        UbisoftLoaderStatus.Text = loader == null
            ? "未在目录根部找到 uplay loader（uplay_r2_loader64.dll / uplaypc_r2_loader64.dll）"
            : $"已检测到：{loader}";
    }

    private void DeployUbisoft_Click(object sender, RoutedEventArgs e)
    {
        var dir = UbisoftDirBox.Text.Trim();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Services.ToastService.ShowWarning("免育碧", "请先选择有效的游戏目录");
            return;
        }

        var loader = UbisoftSvc.DetectLoader(dir) ?? "uplay_r2_loader64.dll";
        var (ok, msg) = UbisoftSvc.Deploy(dir, loader);
        UbisoftLogBox.Text = msg;
        if (ok)
            Services.ToastService.ShowSuccess("免育碧", msg);
        else
            Services.ToastService.ShowError("免育碧部署失败", msg);
    }

    private void RestoreUbisoft_Click(object sender, RoutedEventArgs e)
    {
        var dir = UbisoftDirBox.Text.Trim();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Services.ToastService.ShowWarning("免育碧", "请先选择有效的游戏目录");
            return;
        }

        var loader = UbisoftSvc.DetectLoader(dir);
        if (loader == null)
        {
            UbisoftLogBox.Text = "未检测到 uplay loader（可能已还原或目录错误）";
            return;
        }

        var (ok, msg) = UbisoftSvc.Restore(dir, loader);
        UbisoftLogBox.Text = msg;
        if (ok)
            Services.ToastService.ShowInfo("免育碧", msg);
        else
            Services.ToastService.ShowError("免育碧还原失败", msg);
    }
}