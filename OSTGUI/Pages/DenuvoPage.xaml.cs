using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.ViewModels;
using Windows.Storage.Pickers;

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

    private async void ExportOst_Click(object sender, RoutedEventArgs e)
    {
        var appId = VM.ExportAppId.Trim();
        if (string.IsNullOrEmpty(appId))
        {
            Services.ToastService.ShowWarning("导出授权", "请先输入 AppID");
            return;
        }

        var confirmDialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "使用正版账号导出",
            Content = "请务必使用拥有该游戏的正版账号进行导出。\n\n如果使用非正版账号，可能提取到本地缓存的过时授权，导入后无法生效。",
            PrimaryButtonText = "继续导出",
            CloseButtonText = "取消"
        };
        var confirmResult = await confirmDialog.ShowAsync();
        if (confirmResult != ContentDialogResult.Primary)
            return;

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"{appId}.ost"
        };
        picker.FileTypeChoices.Add("OST 授权文件", new List<string> { ".ost" });
        InitializeWithWindow(picker);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        await VM.ExportAsync(file.Path);
    }

    private async void ImportOst_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".ost");
        InitializeWithWindow(picker);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        await VM.LoadOstPreviewAsync(file.Path);
    }

    private async void GoOst_Click(object sender, RoutedEventArgs e)
    {
        if (!VM.HasValidOst) return;
        await VM.ImportOstAsync(VM.SelectedOstPath, this.XamlRoot);
        VM.ClearOstSelection();
    }

    private static void InitializeWithWindow(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private void ImportantLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "important.html");
            if (!File.Exists(htmlPath)) return;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = htmlPath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }
}
