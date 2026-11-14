namespace OSTGUI.Services;

/// <summary>
/// 打开外部链接的唯一入口。
///
/// 用 WinRT 的 <c>Launcher.LaunchUriAsync</c>：异步、不在调用线程上等待。
/// 不用 <c>new ProcessStartInfo { UseShellExecute = true } + Process.Start</c>：ShellExecute
/// 会在调用线程上同步等待 shell 与默认浏览器起来，放在 UI 线程就会卡住消息泵，
/// 表现为「点了没反应 / 界面卡死」；卡住期间再关窗口还容易踩 WinUI 的拆解崩溃。
/// </summary>
public static class UrlLauncher
{
    /// <summary>异步打开；返回 false 表示系统没有能处理它的程序。异常不抛，只记日志。</summary>
    public static async Task<bool> OpenAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                LogService.Diag($"打开链接失败：URL 无效 {url}");
                return false;
            }

            var ok = await Windows.System.Launcher.LaunchUriAsync(uri);
            if (!ok) LogService.Diag($"打开链接失败（系统里没有可处理它的程序）：{url}");
            return ok;
        }
        catch (Exception ex)
        {
            LogService.Diag($"打开链接异常：{url} -> {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>发即忘版，给事件处理器用；异常已在内部处理。</summary>
    public static void Open(string url) => _ = OpenAsync(url);
}
