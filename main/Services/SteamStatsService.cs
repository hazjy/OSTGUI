using System.Diagnostics;
using System.Text.Json;

namespace OSTGUI.Services;

/// <summary>
/// 成就读写（父进程侧）：spawn 自己的 <c>--stats-dump</c> / <c>--stats-apply</c> 子进程，
/// 结果由子进程写 JSON 文件再读回来（stdio 会被 steamclient 输出污染，故不用管道）。
/// 子进程实现见 <see cref="SteamStatsChild"/>。
/// </summary>
public class SteamStatsService
{
    private readonly SteamService _steam;
    private static readonly SemaphoreSlim Gate = new(1, 1);   // 一次只跑一个子进程
    private const int TimeoutMs = 30000;

    public SteamStatsService(SteamService steam) => _steam = steam;

    public Task<StatsChildResult?> DumpAsync(string appId) => RunAsync("--stats-dump", appId, null);

    public Task<StatsChildResult?> ApplyAsync(string appId, IReadOnlyList<AchievementRecord> changes) =>
        RunAsync("--stats-apply", appId, changes);

    private async Task<StatsChildResult?> RunAsync(string mode, string appId, IReadOnlyList<AchievementRecord>? changes)
    {
        var steamPath = _steam.GetSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            return new StatsChildResult { Ok = false, Message = "未检测到 Steam 路径" };

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OSTGUI.exe");
        var outFile = Path.Combine(Path.GetTempPath(), $"ost_stats_{Guid.NewGuid():N}.json");
        string? inFile = null;

        await Gate.WaitAsync();
        try
        {
            var args = $"{mode} {appId} \"{steamPath}\" \"{outFile}\"";
            if (changes != null)
            {
                inFile = Path.Combine(Path.GetTempPath(), $"ost_stats_in_{Guid.NewGuid():N}.json");
                await File.WriteAllTextAsync(inFile, JsonSerializer.Serialize(changes));
                args = $"{mode} {appId} \"{steamPath}\" \"{inFile}\" \"{outFile}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return new StatsChildResult { Ok = false, Message = "无法启动成就子进程" };

            using var cts = new CancellationTokenSource(TimeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                LogService.AddAppLog($"成就子进程超时 {mode} appid={appId}");
                return new StatsChildResult { Ok = false, Message = $"子进程超时（{TimeoutMs / 1000}s）" };
            }

            if (!File.Exists(outFile))
                return new StatsChildResult { Ok = false, Message = $"子进程无输出（退出码 {proc.ExitCode}）" };

            var json = await File.ReadAllTextAsync(outFile);
            return JsonSerializer.Deserialize<StatsChildResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new StatsChildResult { Ok = false, Message = "子进程结果解析失败" };
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"成就子进程异常 {mode} appid={appId}: {ex.Message}");
            return new StatsChildResult { Ok = false, Message = $"子进程异常: {ex.Message}" };
        }
        finally
        {
            try { File.Delete(outFile); } catch { }
            if (inFile != null) { try { File.Delete(inFile); } catch { } }
            Gate.Release();
        }
    }
}
