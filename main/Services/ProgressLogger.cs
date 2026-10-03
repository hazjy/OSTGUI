using Microsoft.Extensions.Logging;

namespace OSTGUI.Services;

/// <summary>
/// 把免 Steam launcher 的 <see cref="ILogger"/> 输出接到部署的 <c>progress</c> 通道上（同一条路）。
///
/// 为什么必须接同一根：<c>progress</c> 与 <c>_logger</c> 原来是两条 —— 一个 <c>Post</c> 到 UI 线程（异步、晚到），
/// 一个在部署线程同步写，于是日志栏里"后发生的先出现"（2026-10-04 实测：`Running Steamless:` 跑到
/// `=== NoSteam Launch Started ===` 前面，末尾那批进度行又全挤到最后）。两条并一条之后入队顺序＝真实顺序。
///
/// 内部 logger 照旧转发一份（<c>AddDebug()</c> 的调试输出不受影响）。
/// </summary>
internal sealed class ProgressLogger<T> : ILogger<T>
{
    private readonly ILogger _inner;
    private readonly IProgress<string> _sink;

    public ProgressLogger(ILogger<T> inner, IProgress<string> sink)
    {
        _inner = inner;
        _sink = sink;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _inner.Log(logLevel, eventId, state, exception, formatter);   // 调试器那份照旧
        if (!IsEnabled(logLevel)) return;

        try
        {
            var message = formatter(state, exception);
            if (exception != null) message += $"（{exception.GetType().Name}：{exception.Message}）";
            _sink.Report(message);
        }
        catch
        {
            // 日志绝不能把部署流程搞挂
        }
    }
}
