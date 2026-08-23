using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NoSteamLauncher.Models;
using System.IO;

namespace NoSteamLauncher.Services;

public sealed class GBEDeploymentService
{
    private readonly string _goldbergRoot;
    private readonly string _templateConfigPath;
    private readonly ILogger<GBEDeploymentService> _logger;

    // SAC 接口名称列表（从 EMUApply.cs 复制）
    private static readonly List<string> InterfaceNames = new()
    {
        "SteamClient",
        "SteamGameServer",
        "SteamGameServerStats",
        "SteamUser",
        "SteamFriends",
        "SteamUtils",
        "SteamMatchMaking",
        "SteamMatchMakingServers",
        "STEAMUSERSTATS_INTERFACE_VERSION",
        "STEAMAPPS_INTERFACE_VERSION",
        "SteamNetworking",
        "STEAMREMOTESTORAGE_INTERFACE_VERSION",
        "STEAMSCREENSHOTS_INTERFACE_VERSION",
        "STEAMHTTP_INTERFACE_VERSION",
        "STEAMUNIFIEDMESSAGES_INTERFACE_VERSION",
        "STEAMUGC_INTERFACE_VERSION",
        "STEAMAPPLIST_INTERFACE_VERSION",
        "STEAMMUSIC_INTERFACE_VERSION",
        "STEAMMUSICREMOTE_INTERFACE_VERSION",
        "STEAMHTMLSURFACE_INTERFACE_VERSION_",
        "STEAMINVENTORY_INTERFACE_V",
        "SteamController",
        "SteamMasterServerUpdater",
        "STEAMVIDEO_INTERFACE_V"
    };

    public GBEDeploymentService(
        ILogger<GBEDeploymentService> logger,
        string? resourcesDir = null)
    {
        _logger = logger;
        var baseDir = resourcesDir ?? AppContext.BaseDirectory;
        _goldbergRoot = Path.Combine(baseDir, "emu", "game_goldberg");
        _templateConfigPath = Path.Combine(_goldbergRoot, "steam_settings");
    }

    public async Task<GBEDeployResult> DeployAsync(
        string gameExePath,
        string appId,
        string? accountName = null,
        string? steamId = null,
        string? language = null,
        bool offlineMode = false,
        bool disableNetworking = false,
        bool unlockAllDlc = true,
        string[]? dlcList = null,
        CancellationToken ct = default)
    {
        // 确保有真正的异步操作，避免 Stopwatch 计时为 0 秒
        // Stopwatch 精度约 10-15ms，需要足够延迟才能正确计时
        await Task.Delay(10, ct);

        var deployedFiles = new List<string>();
        var gameDir = Path.GetDirectoryName(gameExePath)!;

        if (!Directory.Exists(_goldbergRoot))
        {
            return new GBEDeployResult
            {
                Success = false,
                ErrorMessage = $"Goldberg template not found: {_goldbergRoot}"
            };
        }

        try
        {
            // 1. 生成 steam_settings 配置到临时目录（对齐 SAC EMUConfigGenerator）
            var tempConfigPath = Path.Combine(Path.GetTempPath(), "OSTGUI_steam_settings_" + appId);
            if (Directory.Exists(tempConfigPath))
                Directory.Delete(tempConfigPath, true);
            Directory.CreateDirectory(tempConfigPath);

            // 1a. 写入 steam_appid.txt
            File.WriteAllText(Path.Combine(tempConfigPath, "steam_appid.txt"), appId);
            deployedFiles.Add(Path.Combine(tempConfigPath, "steam_appid.txt"));
            _logger.LogInformation("Generated steam_appid.txt: {AppId}", appId);

            // 1b. 生成 configs.user.ini
            GenerateUserConfig(tempConfigPath, accountName, steamId, language);
            deployedFiles.Add(Path.Combine(tempConfigPath, "configs.user.ini"));

            // 1c. 生成 configs.main.ini
            GenerateMainConfig(tempConfigPath, offlineMode, disableNetworking);
            deployedFiles.Add(Path.Combine(tempConfigPath, "configs.main.ini"));

            // 1d. 生成 configs.app.ini（对齐 SAC，替换 DLC.txt）
            GenerateAppConfig(tempConfigPath, dlcList, unlockAllDlc: true);
            deployedFiles.Add(Path.Combine(tempConfigPath, "configs.app.ini"));

            // 1e. 生成 configs.overlay.ini（对齐 SAC）
            GenerateOverlayConfig(tempConfigPath);
            deployedFiles.Add(Path.Combine(tempConfigPath, "configs.overlay.ini"));

            // 2. 对齐 SAC ApplytoFolder：独立扫描两种 DLL，仅替换存在的
            var x86DllLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var x64DllLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(gameDir))
            {
                // 扫描 steam_api.dll (x86)
                foreach (var file in Directory.EnumerateFiles(gameDir, "steam_api.dll", SearchOption.AllDirectories))
                    x86DllLocations.Add(Path.GetDirectoryName(file)!);

                // 扫描 steam_api64.dll (x64)
                foreach (var file in Directory.EnumerateFiles(gameDir, "steam_api64.dll", SearchOption.AllDirectories))
                    x64DllLocations.Add(Path.GetDirectoryName(file)!);
            }

            if (x86DllLocations.Count == 0 && x64DllLocations.Count == 0)
            {
                _logger.LogWarning("No steam_api*.dll found, deploying to game root: {Dir}", gameDir);
                x86DllLocations.Add(gameDir);
                x64DllLocations.Add(gameDir);
            }

            // 3. 对齐 SAC CheckGoldberg：模板 DLL 缺失必须快速失败，
            // 否则会静默跳过 DLL 替换却报告部署成功，游戏仍依赖 Steam
            var templateX86 = Path.Combine(_goldbergRoot, "regular", "x86", "steam_api.dll");
            var templateX64 = Path.Combine(_goldbergRoot, "regular", "x64", "steam_api64.dll");
            var missingTemplates = new[] { templateX86, templateX64 }.Where(t => !File.Exists(t)).ToList();
            if (missingTemplates.Count > 0)
            {
                _logger.LogError("Goldberg template DLLs missing: {Missing}", string.Join(", ", missingTemplates));
                return new GBEDeployResult
                {
                    Success = false,
                    ErrorMessage = $"Goldberg 模板 DLL 缺失（内嵌资源不完整或为旧版布局），已取消部署: {string.Join("; ", missingTemplates)}"
                };
            }

            // 3a. 部署 steam_api.dll (x86) - 仅在包含 x86 DLL 的目录中部署
            foreach (var dllDir in x86DllLocations)
            {
                await DeploySingleDll(dllDir, "steam_api.dll", templateX86, deployedFiles);
            }

            // 3b. 部署 steam_api64.dll (x64) - 仅在包含 x64 DLL 的目录中部署
            foreach (var dllDir in x64DllLocations)
            {
                await DeploySingleDll(dllDir, "steam_api64.dll", templateX64, deployedFiles);
            }

            // 3c. 部署 steam_settings/ - 对每个包含 DLL 的目录
            var allDllLocations = x86DllLocations.Union(x64DllLocations);
            foreach (var dllDir in allDllLocations)
            {
                var settingsDir = Path.Combine(dllDir, "steam_settings");
                if (Directory.Exists(settingsDir))
                {
                    _logger.LogInformation("steam_settings already exists, updating config files: {Dir}", settingsDir);
                    // 强制更新配置文件（对齐 SAC 行为）
                    foreach (var fi in new DirectoryInfo(tempConfigPath).GetFiles())
                    {
                        var dest = Path.Combine(settingsDir, fi.Name);
                        fi.CopyTo(dest, true);
                        deployedFiles.Add(dest);
                    }
                }
                else
                {
                    CopyDirectory(new DirectoryInfo(tempConfigPath), new DirectoryInfo(settingsDir));
                    deployedFiles.Add(settingsDir);
                    _logger.LogInformation("Deployed steam_settings to {Dir}", settingsDir);
                }

                // 3d. 生成 steam_interfaces.txt（对齐 SAC EMUApply.GenerateInterfacesFile）
                // SAC 默认不生成此文件，只有在 ForceGenerateInterfaces=true 时才生成
                // 暂时跳过生成，避免生成错误的接口版本
            }

            // 清理临时目录
            try { Directory.Delete(tempConfigPath, true); } catch { }

            return new GBEDeployResult
            {
                Success = true,
                DeployedFiles = deployedFiles.ToArray()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GBE deployment failed");
            return new GBEDeployResult
            {
                Success = false,
                ErrorMessage = ex.ToString()
            };
        }
    }

    private void GenerateUserConfig(string configPath, string? accountName, string? steamId, string? language)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[user::general]");

        var name = string.IsNullOrWhiteSpace(accountName) ? "Goldberg" : accountName.Trim();
        sb.AppendLine($"# user account name, default=gse orca");
        sb.AppendLine($"account_name={name}");

        var sid = string.IsNullOrWhiteSpace(steamId) ? "76561197960287930" : steamId.Trim();
        sb.AppendLine($"# your account ID in Steam64 format");
        sb.AppendLine($"account_steamid={sid}");

        var lang = string.IsNullOrWhiteSpace(language) ? "english" : language.Trim();
        sb.AppendLine($"# the language reported to the app/game");
        sb.AppendLine($"language={lang}");

        File.WriteAllText(Path.Combine(configPath, "configs.user.ini"), sb.ToString());
        _logger.LogInformation("Generated configs.user.ini: name={Name}, lang={Lang}", name, lang);
    }

    private void GenerateMainConfig(string configPath, bool offlineMode, bool disableNetworking)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[main::connectivity]");
        sb.AppendLine("# 1=disable all steam networking interface functionality");
        sb.AppendLine($"disable_networking={(disableNetworking ? "1" : "0")}");
        sb.AppendLine("# change the UDP/TCP port the emulator listens on, default=47584");
        sb.AppendLine("listen_port=47584");
        sb.AppendLine("# pretend steam is running in offline mode");
        sb.AppendLine($"offline={(offlineMode ? "1" : "0")}");

        File.WriteAllText(Path.Combine(configPath, "configs.main.ini"), sb.ToString());
        _logger.LogInformation("Generated configs.main.ini: offline={Offline}, disableNet={DisableNet}",
            offlineMode, disableNetworking);
    }

    private void CopyDirectory(DirectoryInfo source, DirectoryInfo target)
    {
        Directory.CreateDirectory(target.FullName);

        foreach (var fi in source.GetFiles())
            fi.CopyTo(Path.Combine(target.FullName, fi.Name), true);

        foreach (var diSourceSubDir in source.GetDirectories())
        {
            var nextTargetSubDir = target.CreateSubdirectory(diSourceSubDir.Name);
            CopyDirectory(diSourceSubDir, nextTargetSubDir);
        }
    }

    /// <summary>
    /// 生成 steam_interfaces.txt（对齐 SAC EMUApply.GenerateInterfacesFile）
    /// 从原始 DLL 中提取 Steam API 接口名称
    /// </summary>
    private void GenerateSteamInterfacesFile(string originalDllPath, string settingsDir)
    {
        try
        {
            var interfacesPath = Path.Combine(settingsDir, "steam_interfaces.txt");
            if (File.Exists(interfacesPath))
            {
                _logger.LogDebug("steam_interfaces.txt already exists, skipping");
                return;
            }

            if (!File.Exists(originalDllPath))
            {
                _logger.LogWarning("Original DLL not found for interface generation: {Dll}", originalDllPath);
                return;
            }

            _logger.LogInformation("Generating steam_interfaces.txt from {Dll}", originalDllPath);

            // 读取 DLL 内容（作为文本搜索接口名称）
            var dllContent = File.ReadAllText(originalDllPath, Encoding.Default);
            var result = new HashSet<string>();

            // 查找所有接口名称
            foreach (var name in InterfaceNames)
            {
                FindInterfaces(ref result, dllContent, new Regex($"{name}\\d{{3}}"));
                if (!FindInterfaces(ref result, dllContent, new Regex(@"STEAMCONTROLLER_INTERFACE_VERSION\d{3}")))
                    FindInterfaces(ref result, dllContent, new Regex("STEAMCONTROLLER_INTERFACE_VERSION"));
            }

            if (result.Count > 0)
            {
                Directory.CreateDirectory(settingsDir);
                File.WriteAllLines(interfacesPath, result);
                _logger.LogInformation("Generated steam_interfaces.txt with {Count} interfaces", result.Count);
            }
            else
            {
                _logger.LogWarning("No interfaces found in {Dll}", originalDllPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate steam_interfaces.txt");
        }
    }

    private static bool FindInterfaces(ref HashSet<string> result, string dllContent, Regex regex)
    {
        var success = false;
        var matches = regex.Matches(dllContent);
        foreach (Match match in matches)
        {
            success = true;
            result.Add(match.Value);
        }
        return success;
    }

    /// <summary>
    /// 部署单个 DLL（对齐 SAC Applyx86/Applyx64 逻辑）
    /// </summary>
    private async Task DeploySingleDll(string dllDir, string dllName, string templateDll, List<string> deployedFiles)
    {
        var destDll = Path.Combine(dllDir, dllName);
        var bakDll = Path.ChangeExtension(destDll, ".dll.bak");

        if (File.Exists(bakDll))
        {
            // SAC 逻辑：备份已存在，检查 GBE DLL 是否存在
            _logger.LogInformation("Backup already exists: {Backup}", bakDll);
            if (!File.Exists(destDll))
            {
                // GBE DLL 不存在，从模板复制
                File.Copy(templateDll, destDll);
                deployedFiles.Add(destDll);
                _logger.LogInformation("GBE DLL missing, copied from template: {Dll}", dllName);
            }
            else
            {
                _logger.LogInformation("GBE DLL present, skipping: {Dll}", dllName);
            }
        }
        else if (File.Exists(destDll))
        {
            // 正常备份 + 替换
            File.Move(destDll, bakDll);
            File.Copy(templateDll, destDll);
            deployedFiles.Add(bakDll);
            deployedFiles.Add(destDll);
            _logger.LogInformation("Backed up and deployed {Dll}", dllName);

            // 老版本（2016 前）Steam SDK 游戏需要 steam_interfaces.txt，gbe_fork 会读取；
            // 从原始 DLL 提取接口名（文件已存在时内部自动跳过）
            GenerateSteamInterfacesFile(bakDll, Path.Combine(dllDir, "steam_settings"));
        }
        else
        {
            // 目标不存在，直接复制
            File.Copy(templateDll, destDll);
            deployedFiles.Add(destDll);
            _logger.LogInformation("Deployed {Dll}", dllName);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 生成 configs.app.ini（对齐 SAC EMUConfigGenerator）
    /// </summary>
    private void GenerateAppConfig(string configPath, string[]? dlcList, bool unlockAllDlc = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[app::dlcs]");

        if (unlockAllDlc)
        {
            sb.AppendLine("# 1=report all DLCs as unlocked");
            sb.AppendLine("unlock_all=1");
        }
        else
        {
            sb.AppendLine("# 0=only report DLCs listed below");
            sb.AppendLine("unlock_all=0");
        }

        if (dlcList != null && dlcList.Length > 0)
        {
            sb.AppendLine("# format: ID=name");
            foreach (var dlc in dlcList)
            {
                var parts = dlc.Split(new[] { '=', '\t' }, 2);
                if (parts.Length == 2)
                    sb.AppendLine($"{parts[0].Trim()}={parts[1].Trim()}");
                else
                    sb.AppendLine($"{dlc.Trim()}=Unknown DLC");
            }
        }

        File.WriteAllText(Path.Combine(configPath, "configs.app.ini"), sb.ToString());
        _logger.LogInformation("Generated configs.app.ini: unlockAll={UnlockAll}, dlcCount={Count}",
            unlockAllDlc, dlcList?.Length ?? 0);
    }

    /// <summary>
    /// 生成 configs.overlay.ini（对齐 SAC EMUConfigGenerator）
    /// </summary>
    private void GenerateOverlayConfig(string configPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[overlay::general]");
        sb.AppendLine("# enable the experimental overlay, might cause crashes");
        sb.AppendLine("# default=0");
        sb.AppendLine("enable_experimental_overlay=0");

        File.WriteAllText(Path.Combine(configPath, "configs.overlay.ini"), sb.ToString());
        _logger.LogInformation("Generated configs.overlay.ini");
    }

    /// <summary>
    /// 部署 SteamAPICheckBypass（对齐 SAC SteamStubUnpacker.ApplySteamAPICheckBypass）
    /// 通过 DLL 劫持拦截文件系统 API，隐藏模拟痕迹
    /// </summary>
    public void DeploySteamAPICheckBypass(string gameDir, string? targetExePath = null)
    {
        try
        {
            _logger.LogInformation("Deploying SteamAPICheckBypass to {Dir}", gameDir);

            // 1. 查找 Bypass DLL
            // 路径逻辑：_goldbergRoot = {resourcesDir}/emu/game_goldberg
            //           resourcesDir = {resourcesDir}
            //           bypassDir = {resourcesDir}/SteamAPICheckBypass
            var resourcesDir = Path.GetDirectoryName(Path.GetDirectoryName(_goldbergRoot))!;
            var bypassDir = Path.Combine(resourcesDir, "SteamAPICheckBypass");
            if (!Directory.Exists(bypassDir))
            {
                _logger.LogWarning("SteamAPICheckBypass directory not found: {Dir}", bypassDir);
                return;
            }

            // 2. 确定使用哪个 Bypass DLL（默认 winmm.dll）
            var targetDllName = "winmm.dll";
            var bypassDllNames = new[] { "winmm.dll", "version.dll", "winhttp.dll" };

            // 3. 检查是否已经部署过
            foreach (var bypassDllName in bypassDllNames)
            {
                if (File.Exists(Path.Combine(gameDir, bypassDllName)))
                {
                    _logger.LogInformation("SteamAPICheckBypass already exists: {Dll}", bypassDllName);
                    return;
                }
            }

            // 4. 确定使用 32 位还是 64 位 DLL
            string bypassDll;
            if (!string.IsNullOrEmpty(targetExePath) && File.Exists(targetExePath))
            {
                // 检查 EXE 是 32 位还是 64 位
                using var fs = new FileStream(targetExePath, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);
                fs.Seek(0x3C, SeekOrigin.Begin); // PE header offset
                var peOffset = br.ReadInt32();
                fs.Seek(peOffset + 4, SeekOrigin.Begin); // Machine type offset
                var machineType = br.ReadUInt16();
                
                if (machineType == 0x8664) // AMD64
                    bypassDll = Path.Combine(bypassDir, "SteamAPICheckBypass.dll");
                else
                    bypassDll = Path.Combine(bypassDir, "SteamAPICheckBypass_x32.dll");
            }
            else
            {
                // 默认使用 64 位
                bypassDll = Path.Combine(bypassDir, "SteamAPICheckBypass.dll");
            }

            // 5. 复制 Bypass DLL 为 winmm.dll
            if (File.Exists(bypassDll))
            {
                File.Copy(bypassDll, Path.Combine(gameDir, targetDllName));
                _logger.LogInformation("Deployed Bypass DLL as {Dll}", targetDllName);
            }
            else
            {
                _logger.LogWarning("Bypass DLL not found: {Dll}", bypassDll);
                return;
            }

            // 6. 生成 SteamAPICheckBypass.json
            GenerateBypassConfig(gameDir, targetExePath);

            _logger.LogInformation("SteamAPICheckBypass deployed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy SteamAPICheckBypass");
        }
    }

    private void GenerateBypassConfig(string gameDir, string? targetExePath)
    {
        var jsonContent = new Dictionary<string, object>();

        // 1. 添加 EXE 重定向规则（如果存在 .bak 文件）
        if (!string.IsNullOrEmpty(targetExePath) && File.Exists(targetExePath + ".bak"))
        {
            var exeName = Path.GetFileName(targetExePath);
            jsonContent[exeName] = new
            {
                mode = "file_redirect",
                to = exeName + ".bak",
                file_must_exist = true
            };
        }

        // 2. 查找所有 steam_api*.dll，添加重定向规则
        var apiDlls = Directory.GetFiles(gameDir, "steam_api.dll", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(gameDir, "steam_api64.dll", SearchOption.AllDirectories))
            .ToArray();

        foreach (var apiDll in apiDlls)
        {
            var relativePath = Path.GetRelativePath(gameDir, apiDll);
            if (File.Exists(apiDll + ".bak"))
            {
                jsonContent[relativePath] = new
                {
                    mode = "file_redirect",
                    to = relativePath + ".bak",
                    file_must_exist = true
                };
            }
        }

        // 3. 添加 steam_settings/ 隐藏规则
        var steamsettingsPaths = apiDlls
            .Select(p => Path.Combine(Path.GetDirectoryName(p) ?? string.Empty, "steam_settings"))
            .Distinct();

        foreach (var steamsettingsPath in steamsettingsPaths)
        {
            var relativePath = Path.GetRelativePath(gameDir, steamsettingsPath);
            jsonContent[relativePath] = new
            {
                mode = "file_hide"
            };
        }

        // 4. 添加 steam_settings 内配置文件的隐藏规则（对齐 SAC 完整列表）
        var steamsettingsFiles = new[]
        {
            Path.Combine("steam_settings", "achievements.json"),
            Path.Combine("steam_settings", "branches.json"),
            Path.Combine("steam_settings", "configs.app.ini"),
            Path.Combine("steam_settings", "configs.main.ini"),
            Path.Combine("steam_settings", "configs.overlay.ini"),
            Path.Combine("steam_settings", "configs.user.ini"),
            Path.Combine("steam_settings", "default_items.json"),
            Path.Combine("steam_settings", "items.json"),
            Path.Combine("steam_settings", "stats.txt"),
            Path.Combine("steam_settings", "steam_appid.txt"),
            Path.Combine("steam_settings", "supported_languages.txt"),
            Path.Combine("steam_settings", "achievement_images")
        };

        var steamsettingsFilePaths = apiDlls
            .SelectMany(p => steamsettingsFiles.Select(f => Path.Combine(Path.GetDirectoryName(p) ?? string.Empty, f)))
            .Distinct();

        foreach (var filePath in steamsettingsFilePaths)
        {
            var relativePath = Path.GetRelativePath(gameDir, filePath);
            jsonContent[relativePath] = new
            {
                mode = "file_hide",
                hook_times_mode = "not_nth_time_only",
                hook_time_n = "1"
            };
        }

        // 5. 写入 JSON 文件
        var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var jsonString = System.Text.Json.JsonSerializer.Serialize(jsonContent, jsonOptions);
        File.WriteAllText(Path.Combine(gameDir, "SteamAPICheckBypass.json"), jsonString);
        _logger.LogInformation("Generated SteamAPICheckBypass.json");
    }
}
