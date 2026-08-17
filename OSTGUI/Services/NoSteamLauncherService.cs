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

        // 需要验证的关键文件（generate_interfaces_x86.exe 不存在，移除）
        var requiredResourceFiles = new[] 
        { 
            "steam_api.dll", 
            "steam_api64.dll", 
            "Steamless.CLI.exe",
            "generate_interfaces_x64.exe",
        };

        var needExtractResources = !Directory.Exists(resourcesDir) || 
            !requiredResourceFiles.All(f => File.Exists(Path.Combine(resourcesDir, f)));

        if (needExtractResources)
        {
            _logger.LogInformation("Extracting embedded Resources to {Dir}", resourcesDir);
            // 先清理可能不完整的目录
            if (Directory.Exists(resourcesDir))
                Directory.Delete(resourcesDir, true);
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
        // 获取包含嵌入资源的 NoSteamLauncher 程序集
        var assembly = typeof(NoSteamLaunchOrchestrator).Assembly;
        
        // 资源名称前缀
        var prefixDot = $"{folderName}.";
        var prefixBackslash = $"{folderName}\\";
        
        var extracted = 0;
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            string? relativePath = null;
            
            // 1. 匹配指定文件夹前缀（点号分隔：Resources.xxx 或 Resources.Plugins\xxx）
            if (resourceName.StartsWith(prefixDot, StringComparison.OrdinalIgnoreCase))
            {
                var afterPrefix = resourceName[prefixDot.Length..];
                
                // 资源名称格式：文件名 或 子目录\文件名
                // 使用反斜杠分割路径段，最后一段是文件名（保留点号）
                var segments = afterPrefix.Split('\\');
                if (segments.Length > 1)
                {
                    // 有子目录：前面的段是目录，最后一段是文件名
                    var dirSegments = segments[..^1];
                    var fileName = segments[^1];
                    var dirPath = string.Join(Path.DirectorySeparatorChar.ToString(), dirSegments);
                    relativePath = Path.Combine(dirPath, fileName);
                }
                else
                {
                    // 根目录文件：直接使用文件名（保留点号）
                    relativePath = afterPrefix;
                }
            }
            // 2. 匹配指定文件夹前缀（反斜杠分隔：Resources\xxx）
            else if (resourceName.StartsWith(prefixBackslash, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = resourceName[prefixBackslash.Length..].Replace('\\', Path.DirectorySeparatorChar);
            }
            // 3. 特殊处理：Plugins 目录嵌套在 Resources 下（实际前缀为 Resources.Plugins\）
            else if (folderName == "Plugins")
            {
                const string nestedPrefix = "Resources.Plugins\\";
                
                if (resourceName.StartsWith(nestedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = resourceName[nestedPrefix.Length..].Replace('\\', Path.DirectorySeparatorChar);
                }
                else
                {
                    continue;
                }
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
        
        _logger.LogDebug("Extracted {Count} files from {Folder}", extracted, folderName);
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
