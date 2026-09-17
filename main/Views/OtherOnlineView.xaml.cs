using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Services;
using OSTGUI.ViewModels;

namespace OSTGUI.Views;

/// <summary>其他联机方式视图（当前只有 DLL 注入 / 宿主 480）</summary>
public sealed partial class OtherOnlineView : UserControl
{
    public OtherOnlineView() => this.InitializeComponent();

    private OnlineViewModel? VM => DataContext as OnlineViewModel;

    /// <summary>切换联机方式（0 = DLL 注入，1 = 其他）</summary>
    private void OnlineModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 控件初始化阶段会提前触发一次，此时命名元素尚未就绪
        if (DllInjectPanel == null || OtherModePanel == null) return;

        var isDll = OnlineModeCombo.SelectedIndex == 0;
        DllInjectPanel.Visibility = isDll ? Visibility.Visible : Visibility.Collapsed;
        OtherModePanel.Visibility = isDll ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>查询：按游戏 AppID 定位已安装游戏的主程序，结果显示在输入框下面一行</summary>
    private void QueryDllGameExe_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        // 直接取控件文本：绑定的回写时机不确定，不能依赖 VM 已经是最新值
        VM.DllGameAppId = DllGameAppIdBox.Text.Trim();
        if (VM.DllGameAppId.Length == 0)
        {
            ToastService.ShowWarning("DLL 注入", "请先填游戏 AppID");
            return;
        }

        // 成功不打扰（路径直接显示在下面一行），只有失败才提示
        if (VM.ResolveDllGameExe() is null)
            ToastService.ShowWarning("DLL 注入", "没找到该 AppID 的已安装游戏");
    }

    private void StartDllInject_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        var (ok, msg) = VM.StartDllInject();
        if (ok)
            ToastService.ShowSuccess("DLL 注入", msg);
        else
            ToastService.ShowError("DLL 注入失败", msg);
    }

    private void StopDllInject_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        var (ok, msg) = VM.StopDllInject();
        if (ok)
            ToastService.ShowInfo("DLL 注入", msg);
        else
            ToastService.ShowWarning("DLL 注入", msg);
    }
}
