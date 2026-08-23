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

        // Copy Plugins folder to game directory so Steamless can find its dependencies
        var targetPluginsDir = Path.Combine(workDir, "Plugins");
        progress?.Report("Copying Steamless plugins...");
        CopyPluginsDirectory(_pluginsDir, targetPluginsDir);
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
            // Clean up Plugins folder copied to game directory
            if (Directory.Exists(targetPluginsDir))
            {
                try
                {
                    Directory.Delete(targetPluginsDir, true);
                    _logger.LogDebug("Cleaned up Plugins directory: {Dir}", targetPluginsDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up Plugins directory: {Dir}", targetPluginsDir);
                }
            }
        }
    }

    private static void CopyPluginsDirectory(string sourceDir, string targetDir)
    {
        if (Directory.Exists(targetDir))
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