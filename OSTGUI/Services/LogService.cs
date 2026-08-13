using System.Collections.ObjectModel;
using System.Text;

namespace OSTGUI.Services;

/// <summary>
/// 日志服务 - 内存显示 + 落盘保存，超过保留行数时自动裁剪
/// </summary>
public class LogService
{
    private static readonly ObservableCollection<string> _logs = new();
    private static readonly object _lock = new();
    private static int _addCount;

    public static ObservableCollection<string> Logs => _logs;

    /// <summary>日志文件路径（设置页可一键打开）</summary>
    public static string LogFilePath { get; private set; } = "";

    /// <summary>日志保留行数（内存与文件一致）</summary>
    public static int MaxLines { get; private set; } = 1000;

    /// <summary>
    /// 初始化：指定日志文件路径
    /// </summary>
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

    /// <summary>
    /// 设置保留行数并立即裁剪
    /// </summary>
    public static void SetMaxLines(int maxLines)
    {
        if (maxLines < 10) maxLines = 10;
        lock (_lock)
        {
            MaxLines = maxLines;
            while (_logs.Count > MaxLines)
                _logs.RemoveAt(0);
            TrimFileToMaxLines();
        }
    }

    /// <summary>
    /// 运行时日志（仅内存，设置页日志栏显示，可清空）
    /// </summary>
    public static void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        lock (_lock)
        {
            _logs.Add($"[{timestamp}] {message}");
            while (_logs.Count > MaxLines)
                _logs.RemoveAt(0);
        }
    }

    /// <summary>
    /// 应用级日志（写入日志文件，保留 MaxLines 行，不受运行时清空影响）
    /// </summary>
    public static void AddAppLog(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        lock (_lock)
        {
            if (string.IsNullOrEmpty(LogFilePath)) return;
            try
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
                // 每 50 条检查一次文件行数，超出保留行数时裁剪尾部
                if (++_addCount % 50 == 0)
                    TrimFileToMaxLines();
            }
            catch { }
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _logs.Clear();
        }
    }

    private static void TrimFileToMaxLines()
    {
        if (string.IsNullOrEmpty(LogFilePath) || !File.Exists(LogFilePath)) return;
        try
        {
            var lines = File.ReadAllLines(LogFilePath);
            if (lines.Length <= MaxLines) return;
            File.WriteAllLines(LogFilePath, lines.Skip(lines.Length - MaxLines), Encoding.UTF8);
        }
        catch { }
    }
}
