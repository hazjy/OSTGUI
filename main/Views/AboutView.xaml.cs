using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using OSTGUI.Services;
using System.Reflection;

namespace OSTGUI.Views;

/// <summary>
/// 「关于」弹窗的内容；外壳（ContentDialog）由 <see cref="MainWindow.NavAbout_Tapped"/> 提供，
/// 关闭走 <see cref="RequestClose"/>。
/// 排版对应样张 <c>_mockups/about-dialog/about.html</c>：内容宽 324 px（外框 ≈360）。
/// ⚠️ AOT：本视图不用 <c>{Binding}</c>（源生成成员在 AOT 下绑定提供器看不到，见 REF-AOT适配 §1），
/// 版本号与图标都在 code-behind 直接赋值。
/// </summary>
public sealed partial class AboutView : UserControl
{
    private const string GitHubUrl = "https://github.com/hazjy/OSTGUI";
    private const string DeveloperUrl = "https://github.com/hazjy";

    /// <summary>请求关闭外层弹窗（✕ / Esc 触发）</summary>
    public event EventHandler? RequestClose;

    private string? _kernelVersion;

    public AboutView()
    {
        InitializeComponent();
        LoadVersions();
        _ = LoadLogoAsync();
    }

    /// <summary>两个版本号：GUI 取程序集版本（口径同原 InfoPage），内核取已部署 DLL 的版本资源</summary>
    private void LoadVersions()
    {
        GuiVersionText.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        _kernelVersion = App.Services.GetRequiredService<SteamDllService>().GetKernelVersion();
        KernelVersionText.Text = _kernelVersion ?? "未检测到";

        // 内核没装时没有可复制的版本号：置灰，免得复制到「未检测到」
        CopyKernelButton.IsEnabled = _kernelVersion != null;
    }

    private async Task LoadLogoAsync()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "logo-512.png");
            if (!File.Exists(path))
            {
                LogService.Diag($"关于弹窗：图标缺失（{path}）");
                return;
            }

            var bytes = await File.ReadAllBytesAsync(path);
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes);   // 必须活到 SetSourceAsync 完成
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            LogoImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            LogService.Diag($"关于弹窗：图标加载失败 {ex.GetType().Name}：{ex.Message}");
        }
    }

    private void CopyGuiVersion_Click(object sender, RoutedEventArgs e)
        => CopyVersion(GuiVersionText.Text, "OSTGUI");

    private void CopyKernelVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_kernelVersion == null) return;
        CopyVersion(_kernelVersion, "内核");
    }

    private static void CopyVersion(string version, string what)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(version);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            (App.MainWindow as MainWindow)?.Notify("已复制", $"{what}版本：{version}");
        }
        catch (Exception ex)
        {
            LogService.Diag($"关于弹窗：复制失败 {ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// 手动检查更新：与启动时的自动检查共用同一个服务与同一个系统通知（有新版时弹两个按钮）。
    /// 区别在"没有新版 / 失败"时手动必须有反馈 → 就地显示在按钮下方那行小字里。
    /// </summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;

        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "正在检查更新…";

        try
        {
            var result = await UpdateService.CheckAsync(manual: true);
            UpdateStatusText.Text = result.Status switch
            {
                UpdateCheckStatus.UpdateAvailable => $"发现新版本 v{result.LatestVersion}（见系统通知）",
                UpdateCheckStatus.UpToDate => $"已是最新 v{result.CurrentVersion}",
                UpdateCheckStatus.Busy => "另一次检查正在进行…",
                _ => $"无法检查更新：{result.Message}"
            };
        }
        catch (Exception ex)
        {
            LogService.Diag($"关于弹窗：检查更新异常 {ex.GetType().Name}：{ex.Message}");
            UpdateStatusText.Text = $"无法检查更新：{ex.GetType().Name}";
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }
    }

    private void DevName_Click(object sender, RoutedEventArgs e) => OpenUrl(DeveloperUrl);

    private void GitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

    /// <summary>
    /// 许可文本：打开随包的 <c>LICENSE</c>（仓库根那份的原样副本，正文一字未改）；
    /// 不在才回退到仓库里的副本（GitHub）。
    /// </summary>
    private void License_Click(object sender, RoutedEventArgs e)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        if (File.Exists(local))
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo { FileName = local, UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                LogService.Diag($"关于弹窗：打开 LICENSE 失败 {ex.GetType().Name}：{ex.Message}");
            }
        }

        LogService.Diag("关于弹窗：随包 LICENSE 不在，回退打开 GitHub 上的那份");
        OpenUrl(GitHubUrl + "/blob/main/LICENSE");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Diag($"关于弹窗：打开链接失败 {url}：{ex.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke(this, EventArgs.Empty);

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
