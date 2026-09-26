using Microsoft.Extensions.Logging;
using NoSteamLauncher.Models;
using NoSteamLauncher.Services;
using System.Reflection;
using System.IO;

namespace OSTGUI.Services;

/// <summary>
/// OSTGUI 对 NoSteamLauncher 库的封装器。
/// 负责把嵌入资源解压到临时目录、构造带资源目录的编排器实例、转发进度与日志。
/// </summary>
public sealed class NoSteamLauncherService : IDisposable
{
    private readonly ILogger<NoSteamLauncherService> _logger;
    private readonly string _tempRoot;
    private bool _disposed;

    public NoSteamLauncherService(ILogger<NoSteamLauncherService> logger)
    {
        _logger = logger;

        // 版本专用临时目录，避免多版本解压互相冲突
        var assembly = Assembly.GetExecutingAssembly();
        var hash = assembly.GetName().Version?.ToString().Replace(".", "") ?? "unknown";
        _tempRoot = Path.Combine(Path.GetTempPath(), "OSTGUI_NoSteamLauncher", hash);
        Directory.CreateDirectory(_tempRoot);
    }

    /// <summary>
    /// 确保 Resources 与 Plugins 已解压到临时目录，返回目录路径。
    /// 每次调用都重新校验关键文件清单（杀软可能事后隔离 Steamless.CLI.exe、
    /// 清理工具可能删除临时文件），缺失即整目录重解压。
    /// 注意 Steamless.CLI 从自身所在目录的 Plugins 子目录加载脱壳插件
    /// （BaseDirectory\Plugins），因此 Plugins 必须位于 Resources 根下。
    /// </summary>
    private (string ResourcesDir, string PluginsDir) EnsureExtracted()
    {
        var resourcesDir = Path.Combine(_tempRoot, "Resources");
        var pluginsDir = Path.Combine(_tempRoot, "Plugins");

        // SAC-style template: Steamless in root, GBE under emu/game_goldberg/regular/
        var requiredResourceFiles = new[]
        {
            "Steamless.CLI.exe",
            Path.Combine("emu", "game_goldberg", "regular", "x86", "steam_api.dll"),
            Path.Combine("emu", "game_goldberg", "regular", "x64", "steam_api64.dll"),
            Path.Combine("emu", "game_goldberg", "steam_settings", "steam_appid.txt"),
            // 插件标记：缺失说明是旧版布局的解压残留，必须重新解压
            Path.Combine("Plugins", "Steamless.API.dll"),
            Path.Combine("Plugins", "Steamless.Unpacker.Variant31.x64.dll"),
            Path.Combine("Plugins", "Steamless.Unpacker.Variant10.x86.dll"),
        };

        var needExtractResources = !Directory.Exists(resourcesDir) ||
            !requiredResourceFiles.All(f => File.Exists(Path.Combine(resourcesDir, f)));

        if (needExtractResources)
        {
            _logger.LogInformation("Extracting embedded Resources to {Dir}", resourcesDir);
            if (Directory.Exists(resourcesDir))
                Directory.Delete(resourcesDir, true);
            ExtractEmbeddedFolder("Resources", resourcesDir);
        }

        if (!Directory.Exists(pluginsDir) || !File.Exists(Path.Combine(pluginsDir, "Steamless.API.dll")))
        {
            _logger.LogInformation("Extracting embedded Plugins to {Dir}", pluginsDir);
            ExtractEmbeddedFolder("Resources.Plugins", pluginsDir);
        }

        // 解压后仍缺关键文件（典型原因：杀毒软件拦截/隔离了解压出的 EXE/DLL），
        // 在这里给出可操作的错误，而不是让下游报"找不到文件"
        var missing = requiredResourceFiles.Where(f => !File.Exists(Path.Combine(resourcesDir, f))).ToList();
        if (missing.Count > 0)
        {
            var detail = string.Join("; ", missing);
            _logger.LogError("Required resources missing after extraction: {Missing}", detail);
            throw new InvalidOperationException(
                $"解压后仍缺少关键资源: {detail}。" +
                $"常见原因是杀毒软件（Windows Defender 等）拦截或隔离了 Steamless 组件。" +
                $"请将目录 {_tempRoot} 加入杀毒软件白名单后重试。");
        }

        return (resourcesDir, pluginsDir);
    }

    private void ExtractEmbeddedFolder(string resourcePrefix, string targetDir)
    {
        // 资源全部内嵌在 NoSteamLauncher 程序集中
        var assembly = typeof(NoSteamLaunchOrchestrator).Assembly;

        // 嵌入名形如 "Resources.xxx" 或 "Resources.目录\文件名"；
        // 插件以 "Resources.Plugins\xxx" 形式存在，传入前缀 "Resources.Plugins" 即可匹配
        var prefixDot = $"{resourcePrefix}.";
        var prefixBackslash = $"{resourcePrefix}\\";

        Directory.CreateDirectory(targetDir);

        var extracted = 0;
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            string? relativePath = null;

            // 1. 点分前缀：Resources.xxx 或 Resources.目录\文件名
            if (resourceName.StartsWith(prefixDot, StringComparison.OrdinalIgnoreCase))
            {
                var afterPrefix = resourceName[prefixDot.Length..];

                var segments = afterPrefix.Split('\\');
                if (segments.Length > 1)
                {
                    var dirPath = string.Join(Path.DirectorySeparatorChar.ToString(), segments[..^1]);
                    relativePath = Path.Combine(dirPath, segments[^1]);
                }
                else
                {
                    relativePath = afterPrefix;
                }
            }
            // 2. 反斜杠前缀：Resources\xxx
            else if (resourceName.StartsWith(prefixBackslash, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = resourceName[prefixBackslash.Length..].Replace('\\', Path.DirectorySeparatorChar);
            }
            else
            {
                continue;
            }

            var targetPath = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var fileStream = File.Create(targetPath);
            stream.CopyTo(fileStream);
            extracted++;
        }

        _logger.LogDebug("Extracted {Count} files with prefix {Prefix} to {Dir}", extracted, resourcePrefix, targetDir);
    }

    /// <summary>
    /// 用解压出的资源目录构造编排器实例。
    /// </summary>
    private NoSteamLaunchOrchestrator CreateOrchestrator(string resourcesDir, string pluginsDir,
        ILogger<NoSteamLaunchOrchestrator> orchestratorLogger,
        ILogger<SteamlessService> steamlessLogger,
        ILogger<GBEDeploymentService> gbeLogger)
    {
        var steamlessExePath = Path.Combine(resourcesDir, "Steamless.CLI.exe");

        return NoSteamLaunchOrchestrator.CreateWithResourcesDir(
            steamlessExePath,
            resourcesDir,
            pluginsDir,
            orchestratorLogger,
            steamlessLogger,
            gbeLogger);
    }

    /// <summary>
    /// 执行免 Steam 部署：自动解压资源、构造编排器、日志转发到 LogService。
    /// </summary>
    public async Task<LaunchResult> ExecuteAsync(
        LaunchOptions options,
        ILogger<NoSteamLaunchOrchestrator> orchestratorLogger,
        ILogger<SteamlessService> steamlessLogger,
        ILogger<GBEDeploymentService> gbeLogger,
        CancellationToken ct = default)
    {
        var (resourcesDir, pluginsDir) = EnsureExtracted();
        var orchestrator = CreateOrchestrator(resourcesDir, pluginsDir, orchestratorLogger, steamlessLogger, gbeLogger);

        var progress = new Progress<string>(msg =>
        {
            LogService.Event($"[NoSteam] {msg}");
        });

        return await orchestrator.ExecuteAsync(options, progress, ct);
    }

    /// <summary>
    /// 校验选项（不执行部署）。
    /// </summary>
    public string? ValidateOptions(LaunchOptions options,
        ILogger<NoSteamLaunchOrchestrator> orchestratorLogger,
        ILogger<SteamlessService> steamlessLogger,
        ILogger<GBEDeploymentService> gbeLogger)
    {
        var (resourcesDir, pluginsDir) = EnsureExtracted();
        var orchestrator = CreateOrchestrator(resourcesDir, pluginsDir, orchestratorLogger, steamlessLogger, gbeLogger);
        return orchestrator.ValidateOptions(options);
    }

    /// <summary>
    /// 一键还原：照抄 SAC（SteamAutoCrack）Restore 的语义，撤掉游戏目录里的模拟器产物。
    /// 只动游戏目录，不需要解压资源。
    /// </summary>
    public NoSteamRestoreResult Restore(string gameExePath, ILogger<GBEDeploymentService> gbeLogger)
        => new GBEDeploymentService(gbeLogger).Restore(gameExePath);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
                _logger.LogDebug("Cleaned up temp directory: {Dir}", _tempRoot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temp directory: {Dir}", _tempRoot);
        }
    }
}
