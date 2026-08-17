using Microsoft.Extensions.Logging;
using NoSteamLauncher.Models;
using NoSteamLauncher.Services;
using System.Reflection;
using System.IO;

namespace OSTGUI.Services;

/// <summary>
/// OSTGUI 侧的 NoSteamLauncher 服务包装器。
/// 负责解压嵌入资源到临时目录，创建带资源目录的编排器，转发进度日志。
/// </summary>
public sealed class NoSteamLauncherService : IDisposable
{
    private readonly ILogger<NoSteamLauncherService> _logger;
    private readonly string _tempRoot;
    private string? _extractedResourcesDir;
    private bool _disposed;

    public NoSteamLauncherService(ILogger<NoSteamLauncherService> logger)
    {
        _logger = logger;

        // 创建专用临时目录，避免与其他进程冲突
        var assembly = Assembly.GetExecutingAssembly();
        var hash = assembly.GetName().Version?.ToString().Replace(".", "") ?? "unknown";
        _tempRoot = Path.Combine(Path.GetTempPath(), "OSTGUI_NoSteamLauncher", hash);
        Directory.CreateDirectory(_tempRoot);
    }

    /// <summary>
    /// 确保 Resources 和 Plugins 已解压到临时目录，并返回目录路径。
    /// </summary>
    private (string ResourcesDir, string PluginsDir) EnsureExtracted()
    {
        if (_extractedResourcesDir != null && _extractedPluginsDir != null)
            return (_extractedResourcesDir, _extractedPluginsDir);

        var resourcesDir = Path.Combine(_tempRoot, "Resources");
        var pluginsDir = Path.Combine(_tempRoot, "Plugins");

        if (!Directory.Exists(resourcesDir))
        {
            _logger.LogInformation("Extracting embedded Resources to {Dir}", resourcesDir);
            ExtractEmbeddedFolder("Resources", resourcesDir);
        }

        if (!Directory.Exists(pluginsDir))
        {
            _logger.LogInformation("Extracting embedded Plugins to {Dir}", pluginsDir);
            ExtractEmbeddedFolder("Plugins", pluginsDir);
        }

        _extractedResourcesDir = resourcesDir;
        _extractedPluginsDir = pluginsDir;
        return (resourcesDir, pluginsDir);
    }

    private string? _extractedPluginsDir;

    private void ExtractEmbeddedFolder(string folderName, string targetDir)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = $"{assembly.GetName().Name}.{folderName}.";
        
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = resourceName[prefix.Length..].Replace('.', Path.DirectorySeparatorChar);
            var targetPath = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var fileStream = File.Create(targetPath);
            stream.CopyTo(fileStream);
        }
        
        _logger.LogDebug("Extracted {Count} files from {Folder}", 
            Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories).Length, folderName);
    }

    /// <summary>
    /// 创建带有资源目录的编排器实例。
    /// </summary>
    private INoSteamLauncherService CreateOrchestrator(string resourcesDir, 
        ILogger<NoSteamLaunchOrchestrator> orchestratorLogger,
        ILogger<SteamlessService> steamlessLogger,
        ILogger<GBEDeploymentService> gbeLogger)
    {
        // 从解压后的 Resources 目录构建工具路径
        var steamlessExePath = Path.Combine(resourcesDir, "Steamless.CLI.exe");
        var gbeDllPath = Path.Combine(resourcesDir, "steam_api64.dll");
        var gbeDll32Path = Path.Combine(resourcesDir, "steam_api.dll");
        var genInterfacesTool64Path = Path.Combine(resourcesDir, "generate_interfaces_x64.exe");
        var genInterfacesTool32Path = Path.Combine(resourcesDir, "generate_interfaces_x86.exe");
        // steam_settings.EXAMPLE 是目录，不是文件
        var steamSettingsTemplatePath = Path.Combine(resourcesDir, "steam_settings.EXAMPLE");

        return NoSteamLaunchOrchestrator.CreateWithResourcesDir(
            steamlessExePath,
            gbeDllPath,
            gbeDll32Path,
            genInterfacesTool64Path,
            genInterfacesTool32Path,
            steamSettingsTemplatePath,
            resourcesDir,
            orchestratorLogger,
            steamlessLogger,
            gbeLogger);
    }

    /// <summary>
    /// 执行免 Steam 部署，自动处理资源解压和进度日志转发。
    /// </summary>
    public async Task<LaunchResult> ExecuteAsync(
        LaunchOptions options,
        ILogger<NoSteamLaunchOrchestrator> orchestratorLogger,
        ILogger<SteamlessService> steamlessLogger,
        ILogger<GBEDeploymentService> gbeLogger,
        CancellationToken ct = default)
    {
        var (resourcesDir, _) = EnsureExtracted();
        var orchestrator = CreateOrchestrator(resourcesDir, orchestratorLogger, steamlessLogger, gbeLogger);

        // 进度回调：转发到 LogService
        var progress = new Progress<string>(msg => 
        {
            LogService.AddLog($"[NoSteam] {msg}");
        });

        return await orchestrator.ExecuteAsync(options, progress, ct);
    }

    /// <summary>
    /// 验证选项（不执行部署）。
    /// </summary>
    public string? ValidateOptions(LaunchOptions options,
        ILogger<NoSteamLaunchOrchestrator> orchestratorLogger,
        ILogger<SteamlessService> steamlessLogger,
        ILogger<GBEDeploymentService> gbeLogger)
    {
        var (resourcesDir, _) = EnsureExtracted();
        var orchestrator = CreateOrchestrator(resourcesDir, orchestratorLogger, null!, null!);
        return orchestrator.ValidateOptions(options);
    }

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