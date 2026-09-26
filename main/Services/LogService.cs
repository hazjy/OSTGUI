using System.Collections.ObjectModel;
using System.Text;

namespace OSTGUI.Services;

/// <summary>
/// 日志服务。**两条通道，用途分开**（2026-09-26 拆分）：
///
/// - <see cref="Diag"/>：崩溃/诊断 —— 写文件 + 进运行时日志（视图里带 <c>[诊断]</c> 前缀）。
///   启动退出、异常、外部失败、关键状态变化走这里；**别往这里塞流水账**。
/// - <see cref="Event"/>：流水账 —— 只进运行时日志（内存，设置页可看/复制/清空），不落盘。
///
/// 文件（<c>%LOCALAPPDATA%\OSTGUI\logs\ostgui.log</c>）：毫秒时间戳 + pid，追加写且用
/// <c>FileShare.ReadWrite</c>——监控子进程与 stats 子进程也会写同一个文件，互相不能踩；
/// 超过 <see cref="MaxFileBytes"/> 轮转为 <c>ostgui.1.log</c> / <c>ostgui.2.log</c>，
/// 不再"读全文件再重写"（那既抖又和别的进程抢文件）。
///
/// 视图：<see cref="Logs"/> 绑着设置页，任意线程可写（非 UI 线程封送），上限 <see cref="MaxLines"/> 行。
/// 行数上限**只管内存视图**，不影响文件。
/// </summary>
public static class LogService
{
    private static readonly ObservableCollection<string> _logs = new();
    private static readonly object _lock = new();
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int FileBackups = 2;

    public static ObservableCollection<string> Logs => _logs;

    /// <summary>日志文件路径（设置页可一键打开）</summary>
    public static string LogFilePath { get; private set; } = "";

    /// <summary>默认路径：<c>%LOCALAPPDATA%\OSTGUI\logs\ostgui.log</c></summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSTGUI", "logs", "ostgui.log");

    /// <summary>运行时日志保留行数（只作用于内存视图）</summary>
    public static int MaxLines { get; private set; } = 1000;

    public static void Initialize(string filePath)
    {
        lock (_lock)
        {
            LogFilePath = filePath;
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
        }
    }

    /// <summary>设置运行时日志保留行数并立即裁剪视图</summary>
    public static void SetMaxLines(int maxLines)
    {
        if (maxLines < 10) maxLines = 10;
        void Apply()
        {
            lock (_lock)
            {
                MaxLines = maxLines;
                while (_logs.Count > MaxLines) _logs.RemoveAt(0);
            }
        }

        RunOnUiThread(Apply);
    }

    /// <summary>流水账：**只进运行时日志**，不落盘</summary>
    public static void Event(string message) => Write(message, toFile: false);

    /// <summary>诊断：**写文件 + 进运行时日志**（启动退出、异常、外部失败、关键状态变化）</summary>
    public static void Diag(string message) => Write(message, toFile: true);

    /// <summary>
    /// 崩溃专用：文件里留**完整堆栈**（多行、带 FATAL 标记），视图里只留一行摘要。
    /// 日志本身出问题也不能再抛（否则崩在崩溃处理里）。
    /// </summary>
    public static void Fatal(string context, Exception? ex)
    {
        var now = DateTime.Now;
        try
        {
            AppendFile($"[{now:yyyy-MM-dd HH:mm:ss.fff}] [p{Environment.ProcessId}] [D] FATAL {context}" +
                       $"{Environment.NewLine}{ex?.ToString() ?? "(无异常对象)"}");
            AddToView($"[{now:HH:mm:ss}] [诊断] FATAL {context}：{ex?.GetType().Name}: {ex?.Message}");
        }
        catch { }
    }

    public static void Clear()
    {
        // 集合绑着界面，CollectionChanged(Reset) 必须在 UI 线程触发（否则订阅方在工作线程刷绑定）
        RunOnUiThread(() =>
        {
            lock (_lock) _logs.Clear();
        });
    }

    private static void Write(string message, bool toFile)
    {
        var now = DateTime.Now;
        if (toFile)
            AppendFile($"[{now:yyyy-MM-dd HH:mm:ss.fff}] [p{Environment.ProcessId}] [D] {message}");

        AddToView($"[{now:HH:mm:ss}] {(toFile ? "[诊断] " : "")}{message}");
    }

    private static void AddToView(string line) => RunOnUiThread(() =>
    {
        lock (_lock)
        {
            _logs.Add(line);
            while (_logs.Count > MaxLines) _logs.RemoveAt(0);
        }
    });

    /// <summary>任意线程可调用：非 UI 线程要封送回 UI 线程再动集合</summary>
    private static void RunOnUiThread(Action action)
    {
        var dq = App.MainWindow?.DispatcherQueue;
        if (dq == null || dq.HasThreadAccess) action();
        else dq.TryEnqueue(() => action());
    }

    /// <summary>追加一行到日志文件（多进程共享：FileShare.ReadWrite + 失败重试一次）</summary>
    private static void AppendFile(string line)
    {
        // 监控子进程走 Program.Main，不经过 App 构造函数 → 这里兜底初始化，
        // 否则它的日志会因为路径为空被静默丢掉（验收时实测踩到）
        if (LogFilePath.Length == 0) Initialize(DefaultPath);
        if (string.IsNullOrEmpty(LogFilePath)) return;

        lock (_lock)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    RollIfTooBig();
                    using var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, Encoding.UTF8);
                    writer.WriteLine(line);
                    return;
                }
                catch
                {
                    if (attempt == 0) Thread.Sleep(20);   // 另一个进程正占着，等一下再来
                }
            }
        }
    }

    /// <summary>文件超过上限就轮转：ostgui.log → .1 → .2（保留最近两份，旧的丢弃）</summary>
    private static void RollIfTooBig()
    {
        var info = new FileInfo(LogFilePath);
        if (!info.Exists || info.Length < MaxFileBytes) return;

        var dir = Path.GetDirectoryName(LogFilePath) ?? ".";
        string Backup(int i) => Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(LogFilePath)}.{i}.log");

        for (var i = FileBackups; i >= 1; i--)
        {
            var from = i == 1 ? LogFilePath : Backup(i - 1);
            if (!File.Exists(from)) continue;
            if (File.Exists(Backup(i))) File.Delete(Backup(i));
            File.Move(from, Backup(i));
        }
    }
}
