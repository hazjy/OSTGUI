using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace OSTGUI.Services;

/// <summary>
/// Steam 相关服务：路径检测、DLL注入/卸载、进程管理
/// </summary>
public class SteamService
{
    private string? _steamPath;

    /// <summary>
    /// 从注册表自动检测 Steam 安装路径
    /// </summary>
    public string? DetectSteamPath()
    {
        if (!string.IsNullOrEmpty(_steamPath) && Directory.Exists(_steamPath))
            return _steamPath;

        try
        {
            // 64位系统从 WOW6432Node 读取
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key != null)
            {
                _steamPath = key.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(_steamPath) && Directory.Exists(_steamPath))
                    return _steamPath;
            }
        }
        catch { }

        try
        {
            // 尝试 HKCU
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key != null)
            {
                var steamExe = key.GetValue("SteamExe") as string;
                if (!string.IsNullOrEmpty(steamExe))
                {
                    var path = Path.GetDirectoryName(steamExe);
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        _steamPath = path;
                        return _steamPath;
                    }
                }
            }
        }
        catch { }

        // 默认路径
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam");
        if (Directory.Exists(defaultPath))
        {
            _steamPath = defaultPath;
            return _steamPath;
        }

        return null;
    }

    /// <summary>
    /// 设置自定义 Steam 路径
    /// </summary>
    public void SetSteamPath(string path)
    {
        if (Directory.Exists(path))
            _steamPath = path;
    }

    /// <summary>
    /// 获取 Steam 路径
    /// </summary>
    public string? GetSteamPath() => _steamPath;

    private string? _luaPath;

    /// <summary>
    /// 设置自定义 Lua 配置目录（null / 空 = 使用默认 &lt;Steam&gt;\config\lua）
    /// </summary>
    public void SetLuaPath(string? path) =>
        _luaPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    /// <summary>
    /// 当前生效的 Lua 配置目录（不创建）
    /// </summary>
    public string? GetEffectiveLuaDir()
    {
        if (!string.IsNullOrWhiteSpace(_luaPath)) return _luaPath;
        // 兜底 <Steam>\config\lua，与内核侧一致（见 ZSteamTool/src/dllmain.cpp 同处注释）。
        // ⚠️ 别改回 config\stplug-in：09-13 两边默认目录不一致，全新机器上入库/DLC 全部"装了但没用"。
        return string.IsNullOrEmpty(_steamPath) ? null : Path.Combine(_steamPath, "config", "lua");
    }

    /// <summary>
    /// 获取 Lua 配置目录
    /// </summary>
    public string? GetLuaConfigDir()
    {
        var dir = GetEffectiveLuaDir();
        if (string.IsNullOrEmpty(dir)) return null;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 获取 depotcache 目录
    /// </summary>
    public string? GetDepotCacheDir()
    {
        var steamPath = _steamPath;
        if (string.IsNullOrEmpty(steamPath)) return null;
        var dir = Path.Combine(steamPath, "depotcache");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 获取 config/depotcache 目录（创意工坊等）
    /// </summary>
    public string? GetConfigDepotCacheDir()
    {
        var steamPath = _steamPath;
        if (string.IsNullOrEmpty(steamPath)) return null;
        var dir = Path.Combine(steamPath, "config", "depotcache");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 检查 Steam 是否正在运行
    /// </summary>
    public bool IsSteamRunning()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("steam");
            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 重启 Steam
    /// </summary>
    public async Task<(bool success, string message)> RestartSteamAsync(string? accountName = null)
    {
        var steamPath = _steamPath;
        if (string.IsNullOrEmpty(steamPath))
            return (false, "Steam 路径未设置。");

        var steamExe = Path.Combine(steamPath, "steam.exe");
        if (!File.Exists(steamExe))
            return (false, $"未找到 steam.exe: {steamExe}");

        try
        {
            // 关闭 Steam
            var processes = System.Diagnostics.Process.GetProcessesByName("steam");
            foreach (var proc in processes)
            {
                try
                {
                    proc.Kill();
                    await Task.Delay(500);
                }
                catch { }
            }

            // 等待进程退出
            await Task.Delay(2000);

            // 指定账号时写入自动登录用户，使 Steam 启动后直接登录该账号
            if (!string.IsNullOrEmpty(accountName))
                SetAutoLoginUser(accountName);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = steamExe,
                UseShellExecute = true
            });
            return (true, string.IsNullOrEmpty(accountName)
                ? "Steam 正在重启..."
                : "Steam 正在重启并登录所选账号...");
        }
        catch (Exception ex)
        {
            return (false, $"重启 Steam 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 设置 Steam 自动登录用户（HKCU\Software\Valve\Steam\AutoLoginUser），
    /// 下次启动 Steam 时直接登录该账号
    /// </summary>
    private static void SetAutoLoginUser(string accountName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true);
            key?.SetValue("AutoLoginUser", accountName, RegistryValueKind.String);
        }
        catch { }
    }

    /// <summary>
    /// 从 config\loginusers.vdf 读取已记住的 Steam 账号列表
    /// </summary>
    public List<(string AccountName, string PersonaName, bool RememberPassword)> GetSteamAccounts()
    {
        var result = new List<(string, string, bool)>();
        var steamPath = _steamPath;
        if (string.IsNullOrEmpty(steamPath)) return result;

        var vdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(vdfPath)) return result;

        try
        {
            var content = File.ReadAllText(vdfPath);
            var blockRegex = new Regex(@"""(?<sid>\d+)""\s*\{\s*(?<body>[^}]*)\}",
                RegexOptions.Singleline | RegexOptions.Compiled);

            foreach (Match m in blockRegex.Matches(content))
            {
                var body = m.Groups["body"].Value;
                var account = Regex.Match(body, @"""AccountName""\s+""([^""]+)""").Groups[1].Value;
                if (string.IsNullOrEmpty(account)) continue;

                var persona = Regex.Match(body, @"""PersonaName""\s+""([^""]+)""").Groups[1].Value;
                var remember = Regex.Match(body, @"""RememberPassword""\s+""([^""]+)""").Groups[1].Value == "1";
                result.Add((account, persona, remember));
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// 获取当前登录的 Steam 账号（ActiveProcess.ActiveUser 匹配 loginusers.vdf）
    /// </summary>
    public (string AccountName, string PersonaName)? GetCurrentSteamAccount()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var activeUserObj = key?.GetValue("ActiveUser");
            if (activeUserObj is not int activeUser || activeUser <= 0)
                return null;

            var steamId64 = 76561197960265728UL + (ulong)activeUser;
            var steamPath = _steamPath;
            if (string.IsNullOrEmpty(steamPath)) return null;

            var vdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(vdfPath)) return null;

            var content = File.ReadAllText(vdfPath);
            var blockRegex = new Regex($@"""{steamId64}""\s*\{{(?<body>[^}}]*)\}}",
                RegexOptions.Singleline | RegexOptions.Compiled);
            var match = blockRegex.Match(content);
            if (!match.Success) return null;

            var body = match.Groups["body"].Value;
            var account = Regex.Match(body, @"""AccountName""\s+""([^""]+)""").Groups[1].Value;
            var persona = Regex.Match(body, @"""PersonaName""\s+""([^""]+)""").Groups[1].Value;
            if (string.IsNullOrEmpty(account) && string.IsNullOrEmpty(persona))
                return null;
            return (account, persona);
        }
        catch
        {
            return null;
        }
    }
}
