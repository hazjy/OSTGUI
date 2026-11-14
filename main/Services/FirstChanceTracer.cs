using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OSTGUI.Services;

/// <summary>
/// 诊断用：带 <c>--trace-exceptions</c> 启动时，把每个托管异常（含被 catch 掉的）追加到
/// <c>%LOCALAPPDATA%\OSTGUI\logs\firstchance.log</c>。
///
/// 用途：WinUI 的 stowed exception 会以 <c>FailFastWithStowedExceptions</c> 结束进程，
/// 崩溃点永远是 XAML 自己的报告器，托管侧的 UnhandledException 不会触发。把
/// FirstChanceException 记下来，就能看到 fail-fast 之前到底抛了什么、在哪个调用栈上。
/// 按「类型 + 消息」限流，避免刷爆日志。
/// </summary>
public static class FirstChanceTracer
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, int> _counts = new();
    private static bool _installed;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI", "logs", "firstchance.log");

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"===== traced session {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ====={Environment.NewLine}");
        }
        catch { }

        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            try
            {
                var ex = e.Exception;
                var key = ex.GetType().FullName + "|" + ex.Message;
                int n;
                lock (_lock)
                {
                    _counts.TryGetValue(key, out n);
                    n++;
                    _counts[key] = n;
                }
                if (n > 20) return;

                var stack = ex.StackTrace ?? "(no stack)";
                var frames = string.Join(Environment.NewLine, stack.Split('\n').Take(8));
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [t{Environment.CurrentManagedThreadId}] #{n} {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{frames}{Environment.NewLine}---{Environment.NewLine}");
            }
            catch { }
        };
    }
}
