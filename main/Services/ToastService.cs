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
}
