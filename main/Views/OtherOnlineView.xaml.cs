using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OSTGUI.Services;
using OSTGUI.ViewModels;

namespace OSTGUI.Views;

/// <summary>其他联机方式视图（宿主会话 / 文件会话，共用同一套输入与启停按钮）</summary>
public sealed partial class OtherOnlineView : UserControl
{
    public OtherOnlineView() => this.InitializeComponent();

    private OnlineViewModel? VM => DataContext as OnlineViewModel;

    // 选中项存在 VM（绑定 SelectedIndex，改动即落盘），这里只读它，避免两处状态
    private bool IsFileSessionMode => VM?.OtherModeIndex == 1;

    /// <summary>查询：按游戏 AppID 定位已安装游戏的主程序，结果显示在输入框下面一行</summary>
    private void QueryHostGameExe_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        // 直接取控件文本：绑定的回写时机不确定，不能依赖 VM 已经是最新值
        VM.HostGameAppId = HostGameAppIdBox.Text.Trim();
        // 这两处校验针对的是"填的 AppID"，跟当前是哪种模式无关，所以不带模式名
        // （模式名在本视图里是动态算的 宿主会话 / 文件会话，写死必错）
        if (VM.HostGameAppId.Length == 0)
        {
            (App.MainWindow as MainWindow)?.Notify("", "请先填写游戏 AppID", InfoBarSeverity.Warning);
            return;
        }

        // 成功不打扰（路径直接显示在下面一行），只有失败才提示
        if (VM.ResolveHostGameExe() is null)
            (App.MainWindow as MainWindow)?.Notify("", "未找到该AppID的已安装游戏", InfoBarSeverity.Warning);
    }

    /// <summary>启动：按当前方式分派（宿主会话 / 文件会话）</summary>
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        var fileSession = IsFileSessionMode;
        var title = fileSession ? "文件会话" : "宿主会话";
        var (ok, msg) = fileSession ? VM.StartFileSession() : VM.StartHostSession();
        if (ok)
            ToastService.ShowSuccess(title, msg);
        else
            ToastService.ShowError($"{title}启动失败", msg);
    }

    /// <summary>停止：两种方式共用（宿主 + 它拉起的游戏一起结束）</summary>
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (VM is null) return;

        var title = IsFileSessionMode ? "文件会话" : "宿主会话";
        var (ok, msg) = VM.StopHostSession();
        if (ok)
            ToastService.ShowInfo(title, msg);
        else
            ToastService.ShowWarning(title, msg);
    }
}
