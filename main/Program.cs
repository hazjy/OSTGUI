namespace OSTGUI;

/// <summary>
/// 入口点。WinUI 默认会生成一个 Main（XamlCompiler 生成），这里用 csproj 里的
/// <c>DISABLE_XAML_GENERATED_MAIN</c> 把它关掉、自己写，目的只有一个：
/// **让监控子进程（<c>--trainer-monitor</c>）在 WinUI 初始化之前就返回**。
///
/// 实测（2026-09-26）：走生成 Main 时（<c>Application.Start</c> → <c>new App()</c> → 解析 App.xaml），
/// 监控进程要 **107 MB 工作集**；而监控只是"每 2 秒看一次绑定文件、按需起停修改器进程"，
/// 一行 UI 都不需要，白白把整个 WinUI/WinAppSDK 栈装进内存。
///
/// 生成 Main 的等价内容就是下面 <c>Application.Start</c> 那几行（Bootstrap 由 WindowsAppSDK
/// 的模块初始化器在 Main 之前完成，这里不需要动）。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0].Equals("--trainer-monitor", StringComparison.OrdinalIgnoreCase))
        {
            // 子进程没有 App 的异常钩子 → 自己兜住：崩了要能在日志里看见（没兜住就是"一运行就死、日志全无"）
            try
            {
                return Services.TrainerMonitor.Run();
            }
            catch (Exception ex)
            {
                Services.LogService.Fatal("监控子进程未处理异常", ex);
                return 1;
            }
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // 系统通知注册：必须早于 Application.Start（早于任何 Show），且**只在这里**——
        // 监控子进程上面已经 return；--extract-ticket / --stats 等子进程虽然会进 OnLaunched，
        // 但它们的返回点在 OnLaunched 里，放在这里注册就不会沾上它们。
        RegisterNotifications();

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            // DispatcherQueue 的同步上下文：WinUI 里 await 回来要在 UI 线程上（生成 Main 同样这两行）
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    /// <summary>
    /// 系统通知注册（WinAppSDK 内置 <c>AppNotificationManager</c>，见 Services/ToastService.cs）。
    /// 注册失败不能拖死启动（旧机器/受限环境注册可能不支持）→ 只记日志、继续开窗口。
    /// </summary>
    private static void RegisterNotifications()
    {
        try
        {
            var notifications = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
            notifications.Register();

            // 本项目原本**没有**通知点击处理逻辑（旧实现也没订阅过），所以这里只订阅 + 记一行日志，
            // 不做任何导航/动作（要做事再单独提）。
            notifications.NotificationInvoked += (_, e) =>
                Services.LogService.Diag($"系统通知被点击：{e.Argument}");
        }
        catch (Exception ex)
        {
            Services.LogService.Diag($"系统通知注册失败（该环境不支持应用通知？）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
