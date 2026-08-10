using System.Collections.ObjectModel;

namespace OSTGUI.Services;

/// <summary>
/// 简单日志服务 - 用于在 UI 中显示日志
/// </summary>
public class LogService
{
    private static readonly ObservableCollection<string> _logs = new();
    public static ObservableCollection<string> Logs => _logs;

    public static void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _logs.Add($"[{timestamp}] {message}");
    }

    public static void Clear() => _logs.Clear();
}