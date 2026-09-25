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
            return Services.TrainerMonitor.Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
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
}
