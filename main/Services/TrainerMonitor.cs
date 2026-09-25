using System.Diagnostics;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 进程绑定监控：每 2 秒看一次绑定的游戏进程在不在——
/// 在 → 启动对应修改器；退出 → 结束（**只结束自己启动过的那个**）。
/// 没有启用的绑定就自我退出（和 Fluent-Steam-Lua 的 SvcMonitor 同样的语义）。
///
/// 以「同 exe 子进程」方式运行（<c>OSTGUI.exe --trainer-monitor</c>，见 App.xaml.cs），
/// 这样 GUI 关掉后绑定仍然生效，也不用单独维护一个工程/服务安装。
/// </summary>
public static class TrainerMonitor
{
    private const string MutexName = @"Global\OSTGUI_TrainerMonitor";
    private const int IntervalMs = 2000;

    /// <summary>自己写 pid 文件，GUI 侧要停监控时按它来（比按命令行筛进程省事）</summary>
    private static string PidPath => Path.Combine(TrainerDownloadService.DefaultDir, "monitor.pid");

    public static int Run()
    {
        using var mutex = new Mutex(true, MutexName, out var isNew);
        if (!isNew)
        {
            LogService.AddAppLog("trainer 监控：已有实例在运行，本次退出");
            return 0;
        }

        Directory.CreateDirectory(TrainerDownloadService.DefaultDir);
        try { File.WriteAllText(PidPath, Environment.ProcessId.ToString()); } catch { }
        LogService.AddAppLog($"trainer 监控启动 pid={Environment.ProcessId}");

        var service = new TrainerBindingService();
        var bindings = new List<TrainerBinding>();
        var lastWrite = DateTime.MinValue;
        var started = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);   // 修改器路径 → 进程

        try
        {
            while (true)
            {
                // 配置热重载：只有 mtime 变了才重新读
                try
                {
                    var info = new FileInfo(TrainerBindingService.BindingsPath);
                    if (info.Exists && info.LastWriteTimeUtc > lastWrite)
                    {
                        bindings = service.Load();
                        lastWrite = info.LastWriteTimeUtc;
                    }
                }
                catch { }

                var enabled = bindings.Where(b => b.IsEnabled).ToList();
                if (enabled.Count == 0)
                {
                    LogService.AddAppLog("trainer 监控：没有启用的绑定，退出");
                    break;
                }

                foreach (var binding in enabled)
                {
                    var game = FindGameProcess(binding.GameExePath);
                    if (game != null)
                    {
                        if (!started.ContainsKey(binding.TrainerFilePath)) StartTrainer(binding, started);
                    }
                    else if (started.Remove(binding.TrainerFilePath, out var trainer))
                    {
                        Kill(trainer);
                    }
                }

                // 绑定被删/禁用 → 把已经起来的收掉
                foreach (var path in started.Keys.ToList())
                {
                    if (enabled.Any(b => string.Equals(b.TrainerFilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
                    Kill(started[path]);
                    started.Remove(path);
                }

                Thread.Sleep(IntervalMs);
            }
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 监控异常退出: {ex.Message}");
            return 1;
        }
        finally
        {
            foreach (var proc in started.Values) Kill(proc);
            try { if (File.Exists(PidPath)) File.Delete(PidPath); } catch { }
        }
        return 0;
    }

    /// <summary>按 exe 全路径找游戏进程；读不到路径（位数/权限）就退回按进程名认</summary>
    private static Process? FindGameProcess(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;

        var name = Path.GetFileNameWithoutExtension(exePath);
        foreach (var proc in Process.GetProcessesByName(name))
        {
            try
            {
                if (string.Equals(proc.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                    return proc;
            }
            catch
            {
                return proc;   // 拿不到 MainModule：名字对上了就算（与 FSL 一致的宽松判定）
            }
        }
        return null;
    }

    private static void StartTrainer(TrainerBinding binding, Dictionary<string, Process> started)
    {
        if (!File.Exists(binding.TrainerFilePath))
        {
            LogService.AddAppLog($"trainer 监控：修改器文件不存在 {binding.TrainerFilePath}");
            return;
        }

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = binding.TrainerFilePath,
                WorkingDirectory = Path.GetDirectoryName(binding.TrainerFilePath) ?? TrainerDownloadService.DefaultDir,
                UseShellExecute = true,
            });

            if (proc != null) started[binding.TrainerFilePath] = proc;
            LogService.AddAppLog($"trainer 已随游戏启动 {Path.GetFileName(binding.TrainerFilePath)}（游戏 {binding.GameName}）");
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 启动失败 {Path.GetFileName(binding.TrainerFilePath)}: {ex.Message}");
        }
    }

    private static void Kill(Process proc)
    {
        try
        {
            if (proc.HasExited) return;
            proc.Kill();
            proc.WaitForExit(2000);
        }
        catch { }
    }
}
