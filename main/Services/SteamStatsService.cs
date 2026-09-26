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

    /// <summary>批量问"这些 appid 里哪些是拥有的"。失败/Steam 没跑 → 返回空集合（调用方按"没有正版"处理）。</summary>
    public async Task<HashSet<string>> OwnedAppsAsync(IReadOnlyList<string> appIds)
    {
        var empty = new HashSet<string>();
        if (appIds.Count == 0) return empty;

        var steamPath = _steam.GetSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return empty;

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OSTGUI.exe");
        var inFile = Path.Combine(Path.GetTempPath(), $"ost_owned_in_{Guid.NewGuid():N}.json");
        var outFile = Path.Combine(Path.GetTempPath(), $"ost_owned_out_{Guid.NewGuid():N}.json");

        await Gate.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(inFile, JsonSerializer.Serialize(appIds));
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--stats-owned \"{steamPath}\" \"{inFile}\" \"{outFile}\"",   // 注意：这个模式第 2 个参数是 Steam 路径
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return empty;

            using var cts = new CancellationTokenSource(TimeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                LogService.Diag("stats-owned 子进程超时");
                return empty;
            }

            if (!File.Exists(outFile)) return empty;
            var list = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(outFile)) ?? new List<string>();
            return new HashSet<string>(list, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            LogService.Diag($"stats-owned 异常: {ex.Message}");
            return empty;
        }
        finally
        {
            try { File.Delete(outFile); } catch { }
            try { File.Delete(inFile); } catch { }
            Gate.Release();
        }
    }

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
                LogService.Diag($"成就子进程超时 {mode} appid={appId}");
                return new StatsChildResult { Ok = false, Message = $"子进程超时（{TimeoutMs / 1000}s）" };
            }

            if (!File.Exists(outFile))
                return new StatsChildResult { Ok = false, Message = $"子进程无输出（退出码 {proc.ExitCode}）" };

            var json = await File.ReadAllTextAsync(outFile);
            var result = JsonSerializer.Deserialize<StatsChildResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                         ?? new StatsChildResult { Ok = false, Message = "子进程结果解析失败" };

            // 占位结果 = 子进程没跑完（原生调用越界属进程级死亡，子进程自己 catch 不住）
            if (!result.Ok && result.Message.StartsWith("子进程未完成"))
                result.Message += $"（退出码 {proc.ExitCode}；0xC0000005 = 访问违例，日志里有最后一条 stats[…] 阶段行）";

            return result;
        }
        catch (Exception ex)
        {
            LogService.Diag($"成就子进程异常 {mode} appid={appId}: {ex.Message}");
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
