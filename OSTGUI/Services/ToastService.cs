using Microsoft.Toolkit.Uwp.Notifications;
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
/// Toast 通知服务 - 使用 Windows 系统通知（支持队列和实时显示）
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
                catch { }
            }
        }
        finally
        {
            _isShowing = false;
        }
    }

    private static void ShowToast(string title, string content)
    {
        new ToastContentBuilder()
            .AddArgument("action", "viewDetail")
            .AddText(title)
            .AddText(content)
            .Show();
    }

    public static void ShowSuccess(string title, string content) => Show(title, content, ToastType.Success);
    public static void ShowError(string title, string content) => Show(title, content, ToastType.Error);
    public static void ShowWarning(string title, string content) => Show(title, content, ToastType.Warning);
    public static void ShowInfo(string title, string content) => Show(title, content, ToastType.Info);
}
