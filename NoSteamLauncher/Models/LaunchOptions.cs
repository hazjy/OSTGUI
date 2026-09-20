namespace NoSteamLauncher.Models;

public sealed class LaunchOptions
{
    public required string GameExePath { get; init; }
    public required string AppId { get; init; }
    public bool BackupOriginalExe { get; init; } = true;
    public bool DryRun { get; init; } = false;
    public bool Verbose { get; init; } = false;

    // Steamless options
    public TimeSpan SteamlessTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public bool SkipSteamless { get; init; } = false;

    // GBE options
    public bool SkipGBE { get; init; } = false;

    // SteamAPICheckBypass options (aligned with SAC SteamStubUnpacker)
    public bool EnableSteamAPICheckBypass { get; init; } = false;

    // GBE config options (aligned with SAC EMUConfig)
    public string? ForceAccountName { get; init; }
    public string? ForceSteamId { get; init; }
    public string? ForceLanguage { get; init; }
    public string? DlcContent { get; init; }
    public bool UnlockAllDlc { get; init; } = true;
    public bool OfflineMode { get; init; } = false;
    public bool DisableNetworking { get; init; } = false;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(GameExePath))
            throw new ArgumentException("GameExePath is required", nameof(GameExePath));

        if (!File.Exists(GameExePath))
            throw new FileNotFoundException($"Game EXE not found: {GameExePath}", GameExePath);

        if (string.IsNullOrWhiteSpace(AppId))
            throw new ArgumentException("AppId is required", nameof(AppId));

        if (!int.TryParse(AppId, out _))
            throw new ArgumentException("AppId must be a valid integer", nameof(AppId));

        if (SteamlessTimeout <= TimeSpan.Zero)
            throw new ArgumentException("SteamlessTimeout must be positive", nameof(SteamlessTimeout));

        if (!string.IsNullOrWhiteSpace(ForceSteamId))
        {
            if (!ulong.TryParse(ForceSteamId, out var steamId) || steamId < 76561197960265728UL)
            {
                throw new ArgumentException("ForceSteamId must be a valid SteamID64 (starting with 7656119)", nameof(ForceSteamId));
            }
        }

        if (!string.IsNullOrWhiteSpace(ForceLanguage))
        {
            var validLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "english", "schinese", "tchinese", "japanese", "korean", "french", "german",
                "spanish", "russian", "portuguese", "polish", "italian", "dutch", "turkish"
            };
            if (!validLanguages.Contains(ForceLanguage.Trim()))
            {
                throw new ArgumentException($"ForceLanguage '{ForceLanguage}' is not a recognized Steam language code", nameof(ForceLanguage));
            }
        }
    }
}

public sealed class SteamlessResult
{
    public bool Success { get; init; }
    /// <summary>文件本身没有 SteamStub 壳（或壳无法识别），并非致命错误，部署应继续。</summary>
    public bool NotPacked { get; init; }
    public string? UnpackedExePath { get; init; }
    public string? ErrorMessage { get; init; }
    public int ExitCode { get; init; }
    public string[] OutputLines { get; init; } = [];
    public TimeSpan Duration { get; init; }
}

public sealed class GBEDeployResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string[] DeployedFiles { get; init; } = [];
    public string? SteamInterfacesPath { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed class NoSteamRestoreResult
{
    public bool Success { get; init; }
    /// <summary>逐条还原动作（含跳过的说明），UI 直接打印。</summary>
    public string[] Actions { get; init; } = [];
    /// <summary>未完成的动作（含原因）与复查残留。</summary>
    public string[] Failures { get; init; } = [];
}

public sealed class LaunchResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public SteamlessResult Steamless { get; init; } = new();
    public GBEDeployResult GBEDeploy { get; init; } = new();
    public TimeSpan TotalDuration { get; init; }
}
