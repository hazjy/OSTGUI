using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NoSteamLauncher.Models;
using System.IO;

namespace NoSteamLauncher.Services;

public sealed class SteamlessService
{
    private readonly string _steamlessExePath;
    private readonly string _pluginsDir;
    private readonly ILogger<SteamlessService> _logger;

    public SteamlessService(string steamlessExePath, ILogger<SteamlessService> logger, string? resourcesDir = null, string? pluginsDir = null)
    {
        _steamlessExePath = steamlessExePath;
        _logger = logger;

        // Plugins 目录与 Resources 目录同级，不是在其内部
        var baseDir = resourcesDir ?? AppContext.BaseDirectory;
        _pluginsDir = pluginsDir ?? Path.Combine(Path.GetDirectoryName(baseDir)!, "Plugins");
    }

    public async Task<SteamlessResult> UnpackAsync(string exePath, bool verbose = false, TimeSpan? timeout = null, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        if (!File.Exists(_steamlessExePath))
        {
            return new SteamlessResult
            {
                Success = false,
                ErrorMessage = $"Steamless not found at: {_steamlessExePath}",
                ExitCode = -1
            };
        }

        if (!File.Exists(exePath))
        {
            return new SteamlessResult
            {
                Success = false,
                ErrorMessage = $"Game EXE not found: {exePath}",
                ExitCode = -1
            };
        }

        if (!Directory.Exists(_pluginsDir))
        {
            return new SteamlessResult
            {
                Success = false,
                ErrorMessage = $"Steamless Plugins directory not found: {_pluginsDir}",
                ExitCode = -1
            };
        }

        var args = $"\"{exePath}\"";
        if (!verbose) args += " --quiet";

        _logger.LogInformation("Running Steamless: {Exe} {Args}", _steamlessExePath, args);
        progress?.Report($"Running Steamless on {Path.GetFileName(exePath)}...");

        var workDir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;

        // 插件要拷到 <游戏目录>\Plugins（Steamless 按目标目录找插件），但**游戏自己也可能有这个目录**
        // （部分 Unity / 自研引擎）：旧实现无条件 Directory.Delete 再拷，游戏文件就这么被删了。
        // 现在的规矩：不是我们的目录 → 就地改名暂存（同卷原子），脱壳结束原名还原；只有确认是"我们留下的"才允许删。
        var targetPluginsDir = Path.Combine(workDir, "Plugins");
        string? displacedPluginsDir;
        bool targetPluginsIsOurs;
        try
        {
            displacedPluginsDir = DisplacePluginsDirIfNotOurs(targetPluginsDir, out targetPluginsIsOurs);
        }
        catch (Exception ex)
        {
            // 暂存失败（被占用 / 权限不足）→ 宁可中止脱壳，也不能把我们的文件混进游戏目录之后整目录删
            _logger.LogError(ex, "无法暂存游戏自带的 Plugins 目录，已中止脱壳：{Dir}", targetPluginsDir);
            return new SteamlessResult
            {
                Success = false,
                ErrorMessage = $"游戏目录下已存在 Plugins 且无法暂存（{ex.Message}）。已中止脱壳，未改动该目录。",
                ExitCode = -1
            };
        }

        progress?.Report("Copying Steamless plugins...");
        CopyPluginsDirectory(_pluginsDir, targetPluginsDir, targetPluginsIsOurs);
        progress?.Report("Plugins copied, starting unpack...");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _steamlessExePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };

            var outputLines = new List<string>();
            var errorLines = new List<string>();

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputLines.Add(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorLines.Add(e.Data); };

            var startTime = DateTime.UtcNow;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
            var waitTask = process.WaitForExitAsync(ct);
            var completed = await Task.WhenAny(waitTask, Task.Delay(effectiveTimeout, ct)) == waitTask;
            var duration = DateTime.UtcNow - startTime;

            if (!completed)
            {
                process.Kill(true);
                return new SteamlessResult
                {
                    Success = false,
                    ErrorMessage = $"Steamless timed out after {effectiveTimeout.TotalMinutes:F1} minutes",
                    ExitCode = -2,
                    Duration = duration,
                    OutputLines = outputLines.Concat(errorLines).ToArray()
                };
            }
            await waitTask; // Ensure we observe any exceptions

            var allOutput = outputLines.Concat(errorLines).ToArray();

            _logger.LogInformation("Steamless exited with code {Code} in {Duration}ms", process.ExitCode, duration.TotalMilliseconds);
            if (verbose) allOutput.ToList().ForEach(l => _logger.LogDebug("[Steamless] {Line}", l));
            progress?.Report($"Steamless completed (exit code: {process.ExitCode})");

            var unpackedPath = FindUnpackedExe(exePath, _logger);

            // Check if SteamStub was actually detected (Steamless returns 0 even for no-Stub files)
            var hasSteamStub = allOutput.Any(l => l.Contains("SteamStub", StringComparison.OrdinalIgnoreCase)
                || l.Contains("unpacked", StringComparison.OrdinalIgnoreCase)
                || l.Contains("unbind", StringComparison.OrdinalIgnoreCase));

            // Additional validation: if Steamless reports success but no unpacked file found,
            // verify by comparing file sizes (unpacked should be different from original)
            if (process.ExitCode == 0 && unpackedPath != null && !hasSteamStub)
            {
                try
                {
                    var originalSize = new FileInfo(exePath).Length;
                    var unpackedSize = new FileInfo(unpackedPath).Length;
                    if (originalSize != unpackedSize)
                    {
                        _logger.LogDebug("File size changed ({Original} -> {Unpacked}), treating as successful unpack", originalSize, unpackedSize);
                        hasSteamStub = true;
                    }
                    else
                    {
                        _logger.LogDebug("File size unchanged, likely no SteamStub present");
                    }
                }
                catch
                {
                    // Ignore size check errors
                }
            }

            string? errorMessage;
            // 对齐 SAC：脱壳器无法处理文件 = 文件没有 SteamStub 壳（或为其他保护），
            // 属于非致命情况，部署流程应继续执行模拟器部署
            var notPacked = process.ExitCode != 0 && (
                allOutput.Any(l => l.Contains("All unpackers failed to unpack file", StringComparison.OrdinalIgnoreCase)) ||
                allOutput.Any(l => l.Contains("Failed to unpack file", StringComparison.OrdinalIgnoreCase)));

            if (process.ExitCode != 0 && !notPacked)
            {
                errorMessage = string.Join("\n", allOutput.Where(l => !string.IsNullOrWhiteSpace(l)));
            }
            else if (!hasSteamStub)
            {
                errorMessage = notPacked
                    ? "No SteamStub detected (file may not be protected)"
                    : "Steamless completed but no SteamStub detected (file may not be protected)";
            }
            else
            {
                errorMessage = null;
            }

            return new SteamlessResult
            {
                Success = process.ExitCode == 0 && hasSteamStub && unpackedPath != null,
                NotPacked = notPacked,
                UnpackedExePath = unpackedPath,
                ErrorMessage = errorMessage,
                ExitCode = process.ExitCode,
                Duration = duration,
                OutputLines = allOutput
            };
        }
        finally
        {
            // 走到这里 targetPluginsDir 只可能是"我们刚建的"或"确认是我们留下的"（否则上面已中止），
            // 所以整目录删是安全的；被暂存的游戏自带目录随后改名还原。
            try
            {
                if (Directory.Exists(targetPluginsDir))
                {
                    Directory.Delete(targetPluginsDir, true);
                    _logger.LogDebug("Cleaned up Plugins directory: {Dir}", targetPluginsDir);
                }

                if (displacedPluginsDir != null)
                {
                    Directory.Move(displacedPluginsDir, targetPluginsDir);
                    // 这条与下面的失败告警都走 _logger：并进 progress 通道后日志栏里能看到（顺序与进度一致）
                    _logger.LogInformation("Detected game's own Plugins; restored it to {Dir}", targetPluginsDir);
                }
            }
            catch (Exception ex)
            {
                // 还原失败必须让用户看见：备份还在原处，可手工改名回去
                _logger.LogWarning(ex, "Plugins 清理/还原未完成，备份仍在：{Dir}", displacedPluginsDir ?? targetPluginsDir);
            }
        }
    }

    /// <summary>
    /// 目标目录是不是"我们的插件目录"：标记文件齐（Steamless.API.dll + 至少一个 Unpacker）。
    /// 用来区分"上次脱壳留下的"与"游戏自带的"——只有前者允许整目录删。
    /// </summary>
    private static bool LooksLikeOurPluginsDir(string dir) =>
        File.Exists(Path.Combine(dir, "Steamless.API.dll")) &&
        Directory.EnumerateFiles(dir, "Steamless.Unpacker.Variant*.dll").Any();

    /// <summary>
    /// 目标 <c>Plugins</c> 不是我们的就改名暂存，返回暂存路径（不存在 / 是我们的都返回 null）。
    /// 同卷 <c>Directory.Move</c> 原子；抛异常＝暂存失败，由调用方中止脱壳（绝不硬删别人的目录）。
    ///
    /// 暂存这条走 <c>_logger</c>：经 GUI 侧的 <c>ProgressLogger</c> 并进部署的 progress 通道，
    /// 最终写进日志（免 Steam 页的日志栏与日志文件都能看到）。
    /// </summary>
    private string? DisplacePluginsDirIfNotOurs(string targetDir, out bool isOurs)
    {
        isOurs = false;
        if (!Directory.Exists(targetDir)) return null;
        if (LooksLikeOurPluginsDir(targetDir))
        {
            isOurs = true;
            return null;
        }

        var aside = targetDir + ".ostgui-bak";
        if (Directory.Exists(aside)) aside = $"{targetDir}.ostgui-bak-{DateTime.Now:yyyyMMddHHmmss}";
        Directory.Move(targetDir, aside);
        _logger.LogInformation("Detected game's own Plugins; staged it as {Aside} (restored after unpack)", aside);
        return aside;
    }

    private static void CopyPluginsDirectory(string sourceDir, string targetDir, bool targetIsOurs)
    {
        // 只清"我们自己的"目录；游戏自带的已被暂存（此刻不存在），绝不能在这里删
        if (targetIsOurs && Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, true);
        }
        Directory.CreateDirectory(targetDir);

        try
        {
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, file);
                var destPath = Path.Combine(targetDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(file, destPath, true);
            }
        }
        catch
        {
            // Rollback on failure: delete partially copied directory
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
            }
            throw;
        }
    }

    private static string? FindUnpackedExe(string originalExePath, ILogger? logger = null)
    {
        var dir = Path.GetDirectoryName(originalExePath)!;
        var fullName = Path.GetFileName(originalExePath); // 含扩展名，如 "Game.exe"
        var name = Path.GetFileNameWithoutExtension(originalExePath); // "Game"
        var ext = Path.GetExtension(originalExePath); // ".exe"

        // Log all EXEs in directory for debugging
        var allExes = Directory.GetFiles(dir, "*.exe");
        if (allExes.Length > 0)
        {
            logger?.LogDebug("All EXEs in directory after Steamless: {Files}", string.Join(", ", allExes.Select(Path.GetFileName)));
        }

        // Steamless outputs various formats:
        // <name>.unbind<ext>, <name>_unpacked<ext>, <name>-unpacked<ext>, <name>.unpacked<ext>
        // Also handles: <fullname>.unpacked<ext> (e.g., Game.exe.unpacked.exe)
        var candidates = new[]
        {
            Path.Combine(dir, $"{name}.unbind{ext}"),
            Path.Combine(dir, $"{name}_unpacked{ext}"),
            Path.Combine(dir, $"{name}-unpacked{ext}"),
            Path.Combine(dir, $"{name}.unpacked{ext}"),
            Path.Combine(dir, $"{fullName}.unpacked{ext}"), // Game.exe.unpacked.exe
            Path.Combine(dir, $"{name}_unbind{ext}"),
            Path.Combine(dir, $"{name}-unbind{ext}"),
            Path.Combine(dir, $"{fullName}.unbind{ext}"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}