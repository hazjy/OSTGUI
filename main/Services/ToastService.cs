using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System.Collections.Concurrent;

namespace OSTGUI.Services;

public enum ToastType
{
    Success,
    Error,
    Warning,
    Info
}

/// <summary>
/// Toast 通知服务 - 使用 Windows 系统通知（支持队列和实时显示）。
///
/// 2026-09-27：从已弃用的 <c>Microsoft.Toolkit.Uwp.Notifications</c> 换成 WinAppSDK 内置的
/// <c>Microsoft.Windows.AppNotifications</c>（同包内，零新增依赖）。旧实现在 Native AOT 下被 ILC
/// 编成"必抛"，toast 必坏；注册在 <c>Program.Main</c>（Application.Start 之前）。
/// 另注：**管理员权限进程不能收发通知**（系统限制），提权运行时 Show 会抛 —— 那类失败现在进日志。
/// </summary>
public static class ToastService
{
    private static readonly ConcurrentQueue<(string title, string content)> _pendingToasts = new();
    private static bool _isShowing;

    public static void Show(string title, string content, ToastType type = ToastType.Info)
    {
        _pendingToasts.Enqueue((title, content));
        _ = ProcessQueueAsync();
    }

    private static async Task ProcessQueueAsync()
    {
        if (_isShowing) return;
        _isShowing = true;

        try
        {
            while (_pendingToasts.TryDequeue(out var toast))
            {
                try
                {
                    ShowToast(toast.title, toast.content);
                    await Task.Delay(500); // 间隔显示，避免堆积
                }
                catch (Exception ex)
                {
                    // 原来是 catch { }：提权运行时这里抛了也看不见（"点了没反应、日志全无"）
                    LogService.Diag($"系统通知发送失败（提权运行的进程不能收发通知）：{ex.GetType().Name}：{ex.Message}");
                }
            }
        }
        finally
        {
            _isShowing = false;
        }
    }

    private static void ShowToast(string title, string content)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("action", "viewDetail")
            .AddText(title);
        if (!string.IsNullOrEmpty(content)) builder.AddText(content);   // 只有标题的通知不留空行
        AppNotificationManager.Default.Show(builder.BuildNotification());
    }

    public static void ShowSuccess(string title, string content) => Show(title, content, ToastType.Success);
    public static void ShowError(string title, string content) => Show(title, content, ToastType.Error);
    public static void ShowWarning(string title, string content) => Show(title, content, ToastType.Warning);
    public static void ShowInfo(string title, string content) => Show(title, content, ToastType.Info);

    /// <summary>
    /// 更新提示：**带两个按钮**的系统通知（手动检查与自动检查共用同一个）。
    /// 按钮回调走通知参数而不是委托：App 在运行中时 <c>Program</c> 的 NotificationInvoked 能直接收到，
    /// 由 <see cref="UpdateService.HandleNotificationArgument"/> 按 <c>action</c> 分发。
    /// 不进队列：这是"用户可能要立刻点"的通知，排队延迟反而不合适。
    /// </summary>
    public static void ShowUpdateAvailable(string currentVersion, string latestVersion)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText("发现新版本")
                .AddText($"当前版本 v{currentVersion}，最新 v{latestVersion}")
                .AddButton(new AppNotificationButton("前往发布页").AddArgument("action", "update-open"))
                .AddButton(new AppNotificationButton("暂不更新").AddArgument("action", "update-skip"));
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            // 提权运行时系统通知必抛（系统限制）→ 手动查的反馈还有「关于」弹窗里那行小字兜着
            LogService.Diag($"更新通知发送失败（提权运行的进程不能收发通知）：{ex.GetType().Name}：{ex.Message}");
        }
    }
}
