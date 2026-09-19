using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Services;
using System.Reflection;

namespace OSTGUI.Pages;

public sealed partial class InfoPage : Page
{
    public InfoPage()
    {
        this.InitializeComponent();

        var guiVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        GuiVersionText.Text = $"GUI版本：{guiVersion}";

        var kernelVersion = App.Services.GetRequiredService<SteamDllService>().GetKernelVersion();
        KernelVersionText.Text = $"内核版本：{kernelVersion ?? "未检测到"}";
    }

    // ponytail: 更新检查还没做，先占位提示；接上真实检查后替换
    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => Services.ToastService.ShowInfo("检查更新", "更新检查尚未接入");

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/hazjy/OSTGUI",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
