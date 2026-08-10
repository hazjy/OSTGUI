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

    /// <summary>
    /// 获取 Lua 配置目录
    /// </summary>
    public string? GetLuaConfigDir()
    {
        var steamPath = _steamPath;
        if (string.IsNullOrEmpty(steamPath)) return null;
        var dir = Path.Combine(steamPath, "config", "lua");
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
    /// 获取 OST 配置目录
    /// </summary>
    public string? GetOSTDir()
    {
        var steamPath = _steamPath;
        if (string.IsNullOrEmpty(steamPath)) return null;
        var dir = Path.Combine(steamPath, "opensteamtool");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 检查 OST DLL 是否已注入
    /// </summary>
    public bool IsOSTDllInjected()
    {
        var steamPath = _steamPath;
        if (string.IsNullOrEmpty(steamPath)) return false;

        var dlls = new[] { "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll" };
        foreach (var dll in dlls)
        {
            var path = Path.Combine(steamPath, dll);
            if (!File.Exists(path)) return false;
        }
        return true;
    }

    /// <summary>
    /// 注入 OST DLL（从指定的源目录复制到 Steam 根目录）
    /// </summary>
    public async Task<(bool success, string message)> InjectOstDllAsync(string sourceDir)
    {
        var steamPath = _steamPath;
        if (string.IsNullOrEmpty(steamPath))
            return (false, "Steam 路径未设置，请先在设置中配置 Steam 路径。");

        if (!Directory.Exists(sourceDir))
            return (false, $"源目录不存在: {sourceDir}");

        var dlls = new[] { "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll" };
        var copied = new List<string>();
        var errors = new List<string>();

        foreach (var dll in dlls)
        {
            try
            {
                var src = Path.Combine(sourceDir, dll);
                var dest = Path.Combine(steamPath, dll);

                if (!File.Exists(src))
                {
                    errors.Add($"缺少文件: {dll}");
                    continue;
                }

                // 备份现有文件
                if (File.Exists(dest))
                {
                    var bak = dest + ".ostgui_bak";
                    File.Copy(dest, bak, true);
                }

                await Task.Run(() => File.Copy(src, dest, true));
                copied.Add(dll);
            }
            catch (Exception ex)
            {
                errors.Add($"复制 {dll} 失败: {ex.Message}");
            }
        }

        if (errors.Count > 0 && copied.Count == 0)
            return (false, $"注入失败:\n{string.Join("\n", errors)}");

        if (errors.Count > 0)
            return (true, $"部分成功: 已注入 {string.Join(", ", copied)}\n警告:\n{string.Join("\n", errors)}");

        return (true, $"OST DLL 已全部注入到 Steam 目录:\n{steamPath}\n\n注入文件: {string.Join(", ", copied)}");
    }

    /// <summary>
    /// 卸载 OST DLL（从 Steam 根目录删除）
    /// </summary>
    public async Task<(bool success, string message)> UnloadOstDllAsync()
    {
        var steamPath = _steamPath;
        if (string.IsNullOrEmpty(steamPath))
            return (false, "Steam 路径未设置。");

        var dlls = new[] { "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll" };
        var removed = new List<string>();
        var errors = new List<string>();

        foreach (var dll in dlls)
        {
            try
            {
                var path = Path.Combine(steamPath, dll);
                if (File.Exists(path))
                {
                    await Task.Run(() => File.Delete(path));
                    removed.Add(dll);
                }

                // 恢复备份
                var bak = path + ".ostgui_bak";
                if (File.Exists(bak))
                {
                    File.Move(bak, path);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"删除 {dll} 失败: {ex.Message}");
            }
        }

        if (errors.Count > 0 && removed.Count == 0)
            return (false, $"卸载失败:\n{string.Join("\n", errors)}");

        var msg = removed.Count > 0
            ? $"已从 Steam 目录移除: {string.Join(", ", removed)}"
            : "未发现 OST DLL 文件。";

        return (true, msg);
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
    /// 获取游戏的 Depot ID 列表
    /// </summary>
    public async Task<List<string>> GetDepotIdsAsync(string appId)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return new();

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appId, out var appData) ||
                !appData.TryGetProperty("success", out var success) || !success.GetBoolean() ||
                !appData.TryGetProperty("data", out var data))
                return new();

            var depots = new List<string>();
            if (data.TryGetProperty("depots", out var depotsObj))
            {
                foreach (var prop in depotsObj.EnumerateObject())
                {
                    if (prop.Name.All(char.IsDigit))
                        depots.Add(prop.Name);
                }
            }
            return depots;
        }
        catch
        {
            return new();
        }
    }
}
