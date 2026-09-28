using System.Collections.ObjectModel;
using System.Text;

namespace OSTGUI.Services;

/// <summary>
/// 日志服务。**单一日志流：文件与日志栏收同样的内容**（2026-09-28 按用户要求合并，
/// 原先"诊断 / 流水账两条通道"的差别作废）。<see cref="Diag"/> 与 <see cref="Event"/> 行为完全一致，
/// 保留两个名字只是让调用处读得出语义（异常与失败 vs 流水），将来若要重新分档不必回头改调用点。
///
/// 文件（<c>%LOCALAPPDATA%\OSTGUI\logs\ostgui.log</c>）：毫秒时间戳 + pid，追加写且用
/// <c>FileShare.ReadWrite</c>——监控子进程与 stats 子进程也会写同一个文件，互相不能踩；
/// **超过 <see cref="MaxLines"/> 行就直接裁剪**：重写为最后若干行，更早的真的丢掉，
/// 不留 <c>.1</c>/<c>.2</c> 备份（用户要求：设置里的行数就是硬上限，不结转）。
///
/// 日志栏：<see cref="Logs"/> 是**全量**（会话内不裁剪）；面板只显示尾部若干行（见 <see cref="GetText"/>）。
/// </summary>
public static class LogService
{
    private static readonly ObservableCollection<string> _logs = new();
    private static readonly object _lock = new();
    /// <summary>检查间隔：按上限自适应——读一次文件的代价与上限成正比，小上限就查勤点，免得长期超限</summary>
    private static int CheckEvery => Math.Clamp(MaxLines / 8, 4, 512);
    private static int _appendsSinceLineCount;

    public static ObservableCollection<string> Logs => _logs;

    /// <summary>日志文件路径（设置页可一键打开）</summary>
    public static string LogFilePath { get; private set; } = "";

    /// <summary>默认路径：<c>%LOCALAPPDATA%\OSTGUI\logs\ostgui.log</c></summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSTGUI", "logs", "ostgui.log");

    /// <summary>日志文件**每份**保留行数（超出即轮转；不影响内存视图）</summary>
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

    /// <summary>设置日志文件保留行数；不立刻动文件，下一次追加时按新上限检查并裁剪</summary>
    public static void SetMaxLines(int maxLines)
    {
        if (maxLines < 10) maxLines = 10;
        lock (_lock)
        {
            MaxLines = maxLines;
            _appendsSinceLineCount = CheckEvery;   // 让下次追加立刻查（用户调小上限后马上生效）
        }
    }

    /// <summary>写一条日志：**文件与日志栏都收**（两个名字行为一致，见类型注释）</summary>
    public static void Event(string message) => Write(message);

    /// <inheritdoc cref="Event"/>
    public static void Diag(string message) => Write(message);

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

    /// <summary>
    /// 取日志文本。<paramref name="maxLines"/> ≤ 0 取**全部**（"复制全部"用）；
    /// 给正数则只拼**尾部**该行数（日志栏显示用）——
    /// 逐索引取，代价只与要的段长成正比，不会因为总量大而变慢（2026-09-28）。
    /// </summary>
    public static string GetText(int maxLines = 0)
    {
        lock (_lock)
        {
            var count = _logs.Count;
            var start = maxLines > 0 && count > maxLines ? count - maxLines : 0;
            var sb = new StringBuilder();
            for (var i = start; i < count; i++)
            {
                if (i > start) sb.Append('\n');
                sb.Append(_logs[i]);
            }
            return sb.ToString();
        }
    }

    private static void Write(string message)
    {
        var now = DateTime.Now;
        AppendFile($"[{now:yyyy-MM-dd HH:mm:ss.fff}] [p{Environment.ProcessId}] [D] {message}");
        AddToView($"[{now:HH:mm:ss}] {message}");
    }

    private static void AddToView(string line) => RunOnUiThread(() =>
    {
        lock (_lock) _logs.Add(line);
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
                    TrimIfNeeded();
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

    /// <summary>
    /// 当前文件超过行数上限就**直接裁剪**：重写为最后 <see cref="MaxLines"/> 行，更早的丢掉。
    /// 只在追加路径里按节流调用；重写代价与上限行数成正比（几百到一万行，可忽略）。
    /// </summary>
    private static void TrimIfNeeded()
    {
        // 读一次文件才能知道行数，所以隔若干次追加才真查一次（这期间最多多出 CheckEvery 行）
        if (++_appendsSinceLineCount < CheckEvery) return;
        _appendsSinceLineCount = 0;

        var lines = ReadAllLinesShared(LogFilePath);
        if (lines.Count <= MaxLines) return;

        try
        {
            using (var stream = new FileStream(LogFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                for (var i = lines.Count - MaxLines; i < lines.Count; i++)
                    writer.WriteLine(lines[i]);
            }
        }
        catch
        {
            return;   // 被别的进程占着就这轮不裁，下次追加再试
        }

        // 旧版按大小轮转留下的备份：现在只有一个日志文件，顺手清掉（免得误以为还在轮转）
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(LogFilePath);
            for (var i = 1; i <= 2; i++)
            {
                var backup = Path.Combine(dir, $"{stem}.{i}.log");
                if (File.Exists(backup)) File.Delete(backup);
            }
        }
        catch { }
    }

    /// <summary>读全部行（多进程共享：只读且允许并发写）</summary>
    private static List<string> ReadAllLinesShared(string path)
    {
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line) lines.Add(line);
        }
        catch { }
        return lines;
    }
}
