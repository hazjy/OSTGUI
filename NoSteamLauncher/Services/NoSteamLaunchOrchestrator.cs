using Microsoft.Extensions.Logging;
using NoSteamLauncher.Models;
using System.Diagnostics;
using System.IO;

namespace NoSteamLauncher.Services;

/// <summary>
    /// NoSteamLauncher 编排器，实现核心服务接口。
    /// </summary>
    public sealed class NoSteamLaunchOrchestrator : INoSteamLauncherService
    {
        private readonly SteamlessService _steamless;
        private readonly GBEDeploymentService _gbeDeploy;
        private readonly ILogger<NoSteamLaunchOrchestrator> _logger;

        public NoSteamLaunchOrchestrator(
            SteamlessService steamless,
            GBEDeploymentService gbeDeploy,
            ILogger<NoSteamLaunchOrchestrator> logger)
        {
            _steamless = steamless;
            _gbeDeploy = gbeDeploy;
            _logger = logger;
        }

        /// <summary>
        /// 创建带有自定义资源目录的编排器（用于宿主应用嵌入资源场景）。
        /// </summary>
        public static NoSteamLaunchOrchestrator CreateWithResourcesDir(
            string steamlessExePath,
            string resourcesDir,
            string? pluginsDir,
            ILogger<NoSteamLaunchOrchestrator> logger,
            ILogger<SteamlessService> steamlessLogger,
            ILogger<GBEDeploymentService> gbeLogger)
        {
            var steamless = new SteamlessService(steamlessExePath, steamlessLogger, resourcesDir, pluginsDir);
            var gbeDeploy = new GBEDeploymentService(gbeLogger, resourcesDir);
            return new NoSteamLaunchOrchestrator(steamless, gbeDeploy, logger);
        }

    public async Task<LaunchResult> ExecuteAsync(LaunchOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Report(progress, "=== NoSteam Launch Started ===");
        Report(progress, $"Game: {options.GameExePath}");
        Report(progress, $"AppID: {options.AppId}");
        Report(progress, $"SkipSteamless: {options.SkipSteamless}");
        Report(progress, $"SkipGBE: {options.SkipGBE}");

        try
        {
            // 0. 确定目录
            var gameDir = Path.GetDirectoryName(options.GameExePath)!;
            var originalExe = options.GameExePath;
            var backupExe = options.BackupOriginalExe ? originalExe + ".bak" : null;

            // 1. 备份原 EXE (always overwrite existing backup)
            if (!options.DryRun && options.BackupOriginalExe && backupExe != null)
            {
                File.Copy(originalExe, backupExe, true);
                Report(progress, $"Backed up original EXE to {backupExe}");
            }

            SteamlessResult steamlessResult;

            // 2. Steamless 脱壳（可选）
            if (!options.SkipSteamless)
            {
                Report(progress, "Step 1/3: Running Steamless to unpack SteamStub (if present)...");
                steamlessResult = await _steamless.UnpackAsync(originalExe, options.Verbose, options.SteamlessTimeout, ct, progress);

                if (steamlessResult.Success && steamlessResult.UnpackedExePath != null)
                {
                    Report(progress, $"Steamless succeeded: {steamlessResult.UnpackedExePath}");

                    // 替换原 EXE 为解包后的版本，并清理 Steamless 遗留的临时产物
                    if (!options.DryRun)
                    {
                        File.Copy(steamlessResult.UnpackedExePath, originalExe, true);
                        Report(progress, "Replaced original EXE with unpacked version");
                        try
                        {
                            File.Delete(steamlessResult.UnpackedExePath);
                        }
                        catch (Exception cleanupEx)
                        {
                            _logger.LogWarning(cleanupEx, "Failed to delete leftover unpacked temp file: {File}",
                                steamlessResult.UnpackedExePath);
                        }
                    }
                }
                else if (steamlessResult.ExitCode != 0 && !steamlessResult.NotPacked)
                {
                    // Steamless 执行失败（真错误：超时/崩溃/文件缺失），中止流程；
                    // "未加壳"(NotPacked) 不算失败，走下方继续分支（对齐 SAC）
                    Report(progress, $"Steamless failed with exit code {steamlessResult.ExitCode}: {steamlessResult.ErrorMessage}");
                    return new LaunchResult
                    {
                        Success = false,
                        ErrorMessage = $"Steamless unpacking failed: {steamlessResult.ErrorMessage}",
                        Steamless = steamlessResult,
                        TotalDuration = stopwatch.Elapsed
                    };
                }
                else
                {
                    // Steamless 成功但未检测到 SteamStub，继续执行
                    Report(progress, $"No SteamStub detected, continuing with original EXE...");
                }
            }
            else
            {
                Report(progress, "Skipping Steamless (--skip-steamless)");
                steamlessResult = new SteamlessResult { Success = true, ExitCode = 0, Duration = TimeSpan.Zero };
            }

            GBEDeployResult gbeResult;
            // 3. GBE 部署
            if (!options.DryRun && !options.SkipGBE)
            {
                Report(progress, "Step 2/3: Deploying GBE (Goldberg Steam Emulator)...");
                var gbeStopwatch = Stopwatch.StartNew();
                // SAC-style deployment: scan DLL locations, backup, deploy minimal template
                var dlcArray = string.IsNullOrWhiteSpace(options.DlcContent)
                    ? null
                    : options.DlcContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var deployResult = await _gbeDeploy.DeployAsync(
                    originalExe,
                    options.AppId,
                    accountName: options.ForceAccountName,
                    steamId: options.ForceSteamId,
                    language: options.ForceLanguage,
                    offlineMode: options.OfflineMode,
                    disableNetworking: options.DisableNetworking,
                    unlockAllDlc: options.UnlockAllDlc,
                    dlcList: dlcArray,
                    ct: ct);
                gbeStopwatch.Stop();
                
                // Add duration to result
                gbeResult = new GBEDeployResult
                {
                    Success = deployResult.Success,
                    ErrorMessage = deployResult.ErrorMessage,
                    DeployedFiles = deployResult.DeployedFiles,
                    Duration = gbeStopwatch.Elapsed
                };

                if (!gbeResult.Success)
                {
                    return new LaunchResult
                    {
                        Success = false,
                        ErrorMessage = $"GBE deployment failed: {gbeResult.ErrorMessage}",
                        Steamless = steamlessResult,
                        GBEDeploy = gbeResult,
                        TotalDuration = stopwatch.Elapsed
                    };
                }

                Report(progress, $"GBE deployed: {gbeResult.DeployedFiles.Length} files");
                gbeResult.DeployedFiles.ToList().ForEach(f => Report(progress, $"  - {f}", isDebug: true));

                // 新增：部署 SteamAPICheckBypass（对齐 SAC）
                if (options.EnableSteamAPICheckBypass)
                {
                    Report(progress, "Deploying SteamAPICheckBypass...");
                    _gbeDeploy.DeploySteamAPICheckBypass(gameDir, originalExe);
                }
            }
            else if (options.SkipGBE)
            {
                Report(progress, "Skipping GBE deployment (--skip-gbe)");
                gbeResult = new GBEDeployResult { Success = true, Duration = TimeSpan.Zero };
            }
            else
            {
                Report(progress, "Skipping GBE deployment (--dry-run)");
                gbeResult = new GBEDeployResult { Success = true, Duration = TimeSpan.Zero };
            }

            stopwatch.Stop();
            Report(progress, $"=== NoSteam Launch Completed in {stopwatch.ElapsedMilliseconds}ms ===");

            return new LaunchResult
            {
                Success = true,
                Steamless = steamlessResult,
                GBEDeploy = gbeResult,
                TotalDuration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Report(progress, $"Orchestration failed: {ex}");
            return new LaunchResult
            {
                Success = false,
                ErrorMessage = ex.ToString(),
                TotalDuration = stopwatch.Elapsed
            };
        }
    }

    public string? ValidateOptions(LaunchOptions options)
    {
        try
        {
            options.Validate();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void Report(IProgress<string>? progress, string message, bool isDebug = false)
    {
        if (isDebug) return; // Debug messages not reported to UI progress
        progress?.Report(message);
    }
}