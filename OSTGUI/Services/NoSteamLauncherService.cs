using Microsoft.Extensions.Logging;
using NoSteamLauncher.Models;
using NoSteamLauncher.Services;
using System.Reflection;
using System.IO;

namespace OSTGUI.Services;

/// <summary>
/// OSTGUI ��� NoSteamLauncher �����װ����
/// �����ѹǶ����Դ����ʱĿ¼����������ԴĿ¼�ı�������ת��������־��
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

        // ����ר����ʱĿ¼���������������̳�ͻ
        var assembly = Assembly.GetExecutingAssembly();
        var hash = assembly.GetName().Version?.ToString().Replace(".", "") ?? "unknown";
        _tempRoot = Path.Combine(Path.GetTempPath(), "OSTGUI_NoSteamLauncher", hash);
        Directory.CreateDirectory(_tempRoot);
    }

    /// <summary>
    /// ȷ�� Resources �� Plugins �ѽ�ѹ����ʱĿ¼��������Ŀ¼·����
    /// </summary>
    private (string ResourcesDir, string PluginsDir) EnsureExtracted()
    {
        if (_extractedResourcesDir != null && _extractedPluginsDir != null)
            return (_extractedResourcesDir, _extractedPluginsDir);

        var resourcesDir = Path.Combine(_tempRoot, "Resources");
        var pluginsDir = Path.Combine(_tempRoot, "Plugins");

        // SAC-style template: Steamless in root, GBE under emu/game_goldberg/files/
        var requiredResourceFiles = new[] 
        { 
            "Steamless.CLI.exe",
            Path.Combine("emu", "game_goldberg", "files", "steam_api.dll"),
            Path.Combine("emu", "game_goldberg", "files", "steam_api64.dll"),
            Path.Combine("emu", "game_goldberg", "files", "steam_settings", "steam_appid.txt"),
        };

        var needExtractResources = !Directory.Exists(resourcesDir) || 
            !requiredResourceFiles.All(f => File.Exists(Path.Combine(resourcesDir, f)));

        if (needExtractResources)
        {
            _logger.LogInformation("Extracting embedded Resources to {Dir}", resourcesDir);
            // ���������ܲ�������Ŀ¼
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
        // ��ȡ����Ƕ����Դ�� NoSteamLauncher ����
        var assembly = typeof(NoSteamLaunchOrchestrator).Assembly;
        
        // ��Դ����ǰ׺
        var prefixDot = $"{folderName}.";
        var prefixBackslash = $"{folderName}\\";
        
        var extracted = 0;
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            string? relativePath = null;
            
            // 1. ƥ��ָ���ļ���ǰ׺����ŷָ���Resources.xxx �� Resources.Plugins\xxx��
            if (resourceName.StartsWith(prefixDot, StringComparison.OrdinalIgnoreCase))
            {
                var afterPrefix = resourceName[prefixDot.Length..];
                
                // ��Դ���Ƹ�ʽ���ļ��� �� ��Ŀ¼\�ļ���
                // ʹ�÷�б�ָܷ�·���Σ����һ�����ļ�����������ţ�
                var segments = afterPrefix.Split('\\');
                if (segments.Length > 1)
                {
                    // ����Ŀ¼��ǰ��Ķ���Ŀ¼�����һ�����ļ���
                    var dirSegments = segments[..^1];
                    var fileName = segments[^1];
                    var dirPath = string.Join(Path.DirectorySeparatorChar.ToString(), dirSegments);
                    relativePath = Path.Combine(dirPath, fileName);
                }
                else
                {
                    // ��Ŀ¼�ļ���ֱ��ʹ���ļ�����������ţ�
                    relativePath = afterPrefix;
                }
            }
            // 2. ƥ��ָ���ļ���ǰ׺����б�ָܷ���Resources\xxx��
            else if (resourceName.StartsWith(prefixBackslash, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = resourceName[prefixBackslash.Length..].Replace('\\', Path.DirectorySeparatorChar);
            }
            // 3. ���⴦����Plugins Ŀ¼Ƕ���� Resources �£�ʵ��ǰ׺Ϊ Resources.Plugins\��
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
    /// ����������ԴĿ¼�ı�����ʵ����
    /// </summary>
    private INoSteamLauncherService CreateOrchestrator(string resourcesDir,
        ILogger<NoSteamLaunchOrchestrator> orchestratorLogger,
        ILogger<SteamlessService> steamlessLogger,
        ILogger<GBEDeploymentService> gbeLogger)
    {
        // SAC-style: only Steamless path is needed at this layer
        var steamlessExePath = Path.Combine(resourcesDir, "Steamless.CLI.exe");
        // steam_api.dll/64.dll + steam_settings template now live under emu/game_goldberg/files/
        // GBEDeploymentService handles them internally via _templateRoot

        return NoSteamLaunchOrchestrator.CreateWithResourcesDir(
            steamlessExePath,
            resourcesDir,
            orchestratorLogger,
            steamlessLogger,
            gbeLogger);
    }

    /// <summary>
    /// ִ���� Steam �����Զ�������Դ��ѹ�ͽ�����־ת����
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

        // ���Ȼص���ת���� LogService
        var progress = new Progress<string>(msg => 
        {
            LogService.AddLog($"[NoSteam] {msg}");
        });

        return await orchestrator.ExecuteAsync(options, progress, ct);
    }

    /// <summary>
    /// ��֤ѡ���ִ�в��𣩡�
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
