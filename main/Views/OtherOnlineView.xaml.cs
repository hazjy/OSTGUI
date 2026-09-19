using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Services;
using OSTGUI.ViewModels;

namespace OSTGUI.Views;

/// <summary>其他联机方式视图（DLL 注入 / AppID Changer，共用同一套输入与启停按钮）</summary>
public sealed partial class OtherOnlineView : UserControl
{
    public OtherOnlineView() => this.InitializeComponent();

    private OnlineViewModel? VM => DataContext as OnlineViewModel;

    private bool IsChangerMode => OnlineModeCombo.SelectedIndex == 1;

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

    /// <summary>启动：按当前方式分派（DLL 注入 / AppID Changer）</summary>
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        var changer = IsChangerMode;
        var title = changer ? "AppID Changer" : "DLL 注入";
        var (ok, msg) = changer ? VM.StartChanger() : VM.StartDllInject();
        if (ok)
            ToastService.ShowSuccess(title, msg);
        else
            ToastService.ShowError($"{title}失败", msg);
    }

    /// <summary>停止：两种方式共用（宿主 + 它拉起的游戏一起结束）</summary>
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        var title = IsChangerMode ? "AppID Changer" : "DLL 注入";
        var (ok, msg) = VM.StopDllInject();
        if (ok)
            ToastService.ShowInfo(title, msg);
        else
            ToastService.ShowWarning(title, msg);
    }
}
