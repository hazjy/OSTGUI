namespace OSTGUI.Services;

/// <summary>
/// OST DLL 管理服务 - 注入/卸载/状态检查
/// </summary>
public class SteamDllService
{
    private readonly SteamService _steamService;

    private static readonly string[] OstDlls = { "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll" };

    /// <summary>内核配置文件（与三 DLL 同处 Steam 根目录）</summary>
    public const string ConfigFileName = "opensteamtool.toml";

    private const string DefaultDenuvoMode = "normal";

    public SteamDllService(SteamService steamService)
    {
        _steamService = steamService;
    }

    /// <summary>内核配置文件完整路径；Steam 路径未设置时为 null</summary>
    public string? GetConfigPath()
    {
        var steamPath = _steamService.GetSteamPath();
        return string.IsNullOrEmpty(steamPath) ? null : Path.Combine(steamPath, ConfigFileName);
    }

    /// <summary>
    /// 读取内核 [denuvo] mode（normal / compat）。
    /// 文件、段、键任一缺失或读取失败时按内核默认值 normal 处理。
    /// </summary>
    public string GetDenuvoMode()
    {
        var path = GetConfigPath();
        if (path == null || !File.Exists(path)) return DefaultDenuvoMode;

        try
        {
            var lines = File.ReadAllLines(path).ToList();
            var (start, end) = SectionRange(lines, "denuvo");
            if (start < 0) return DefaultDenuvoMode;

            for (var i = start + 1; i < end; i++)
            {
                if (TryParseModeLine(lines[i], out var mode)) return mode;
            }
        }
        catch { }
        return DefaultDenuvoMode;
    }

    /// <summary>
    /// 写入内核 [denuvo] mode。保留文件其余内容与注释，只改 mode 行；段/键缺失时按需追加。
    /// 内核 ConfigFileWatcher 监听整文件变更并热重载，因此无需重启 Steam。
    /// </summary>
    public (bool success, string message) SetDenuvoMode(string mode)
    {
        if (mode != "normal" && mode != "compat")
            return (false, $"未知的 D 加密模式: {mode}");

        var path = GetConfigPath();
        if (path == null)
            return (false, "Steam 路径未设置，无法定位 opensteamtool.toml");

        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Length == 0 ? new List<string>() : text.Split(newline).ToList();
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);

            var (start, end) = SectionRange(lines, "denuvo");
            if (start < 0)
            {
                lines.Add("[denuvo]");
                lines.Add($"mode = \"{mode}\"");
            }
            else
            {
                var replaced = false;
                for (var i = start + 1; i < end && !replaced; i++)
                {
                    if (TryParseModeLine(lines[i], out _))
                    {
                        lines[i] = $"mode = \"{mode}\"";
                        replaced = true;
                    }
                }
                if (!replaced) lines.Insert(start + 1, $"mode = \"{mode}\"");
            }

            File.WriteAllText(path, string.Join(newline, lines) + newline);
            return (true, $"已写入 {ConfigFileName}：mode = \"{mode}\"（内核立即热重载）");
        }
        catch (Exception ex)
        {
            return (false, $"写入 {ConfigFileName} 失败: {ex.Message}");
        }
    }

    /// <summary>定位 TOML 段行范围：段头下一行起、至下一个段头之前；不存在返回 (-1, -1)</summary>
    private static (int start, int end) SectionRange(List<string> lines, string section)
    {
        var start = lines.FindIndex(l => l.Trim() == $"[{section}]");
        if (start < 0) return (-1, -1);

        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith("["));
        return (start, end < 0 ? lines.Count : end);
    }

    /// <summary>
    /// 读取内核 [lua] paths 的首项 = 内核实际扫描的 Lua 目录。
    /// 未配置（缺文件 / 缺段 / 缺键 / 数组为空 / 整行注释掉）返回 null，由内核默认值兜底。
    /// </summary>
    public string? GetLuaPath()
    {
        var path = GetConfigPath();
        if (path == null || !File.Exists(path)) return null;

        try
        {
            var lines = File.ReadAllLines(path).ToList();
            var (start, end) = SectionRange(lines, "lua");
            if (start < 0) return null;

            for (var i = start + 1; i < end; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith('#')) continue;          // 注释掉的示例行不算生效值
                if (line.StartsWith("paths") && FirstQuoted(line) is { } v) return v;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 把内核 [lua] paths 写成唯一一项：内核与 GUI 共用一个 Lua 目录。
    /// 路径等于 GUI 默认目录时改为注释行（内核回落默认值），否则写入该行；内核热重载，无需重启 Steam。
    /// </summary>
    public (bool success, string message) SetLuaPath(string? path, string defaultPath, string steamPath = "")
    {
        var configPath = GetConfigPath();
        if (configPath == null)
            return (false, "Steam 路径未设置，无法定位 opensteamtool.toml");

        // TOML 基本字符串里反斜杠要转义，正斜杠内核与 Windows 都认；Steam 目录内的
        // 目录写成相对路径（内核按 Steam 目录解析），换机器/换盘也能用
        var value = ToTomlPath(path, steamPath);
        var custom = value.Length > 0 && !PathsEqual(path, defaultPath);
        var line = custom ? $"paths = [\"{value}\"]" : "# paths = []";

        try
        {
            var text = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Length == 0 ? new List<string>() : text.Split(newline).ToList();
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);

            var (start, end) = SectionRange(lines, "lua");
            var at = -1;
            if (start >= 0)
            {
                for (var i = start + 1; i < end; i++)
                {
                    if (lines[i].TrimStart().TrimStart('#').TrimStart().StartsWith("paths"))
                    {
                        at = i;
                        break;
                    }
                }
            }

            if (at >= 0) lines[at] = line;
            else if (start >= 0) lines.Insert(start + 1, line);
            else { lines.Add("[lua]"); lines.Add(line); }

            File.WriteAllText(configPath, string.Join(newline, lines) + newline);
            return (true, custom
                ? $"已写入 {ConfigFileName}：[lua] paths = [\"{path}\"]（内核立即热重载）"
                : $"未设置自定义目录，内核使用默认 {defaultPath}");
        }
        catch (Exception ex)
        {
            return (false, $"写入 {ConfigFileName} 失败: {ex.Message}");
        }
    }

    /// <summary>取一行文本里的第一个引号字符串（容忍 TOML 的 " 与 '；反转义 \\）</summary>
    private static string? FirstQuoted(string line)
    {
        var eq = line.IndexOf('=');
        if (eq < 0) return null;

        var i = line.IndexOfAny(new[] { '"', '\'' }, eq);
        if (i < 0) return null;
        var close = line.IndexOf(line[i], i + 1);
        var value = close < 0 ? line[(i + 1)..] : line[(i + 1)..close];
        value = value.Replace("\\\\", "\\");   // TOML 基本字符串里反斜杠成对出现
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// TOML 里写的路径形式：Steam 目录内 → 相对路径（config/lua），盘外 → 绝对路径。
    /// 一律用正斜杠（TOML 基本字符串里反斜杠要转义）。
    /// </summary>
    private static string ToTomlPath(string? path, string steamPath)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var full = Path.GetFullPath(path.Trim());
        if (!string.IsNullOrWhiteSpace(steamPath) && Directory.Exists(steamPath))
            full = Path.GetRelativePath(Path.GetFullPath(steamPath), full);
        return full.Replace('\\', '/');
    }

    /// <summary>判断路径是否指向同一目录（大小写与结尾分隔符不敏感）</summary>
    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.Trim().Replace('\\', '/').TrimEnd('/'),
            b.Trim().Replace('\\', '/').TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>匹配段内的 mode = "normal|compat" 行（容忍行内注释与空白）</summary>
    private static bool TryParseModeLine(string line, out string mode)
    {
        mode = "";
        var trimmed = line.Trim();
        if (trimmed.StartsWith("#") || !trimmed.StartsWith("mode")) return false;

        var eq = trimmed.IndexOf('=');
        if (eq < 0) return false;

        var value = trimmed[(eq + 1)..];
        var hash = value.IndexOf('#');
        if (hash >= 0) value = value[..hash];
        value = value.Trim().Trim('"');

        if (value != "normal" && value != "compat") return false;
        mode = value;
        return true;
    }

    /// <summary>
    /// 已部署内核 DLL 的版本号（读 DLL 自带的 Windows 版本资源，如 1.1.3）。
    /// 取不到（未部署 / 内核没带版本资源）返回 null。
    /// </summary>
    public string? GetKernelVersion()
    {
        var steamPath = _steamService.GetSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return null;

        var path = Path.Combine(steamPath, "OpenSteamTool.dll");
        if (!File.Exists(path)) return null;

        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (string.IsNullOrEmpty(version)) return null;

        var parts = version.Split('.');
        return parts.Length >= 3 ? $"{parts[0]}.{parts[1]}.{parts[2]}" : version;
    }

    /// <summary>
    /// 检查 OST DLL 是否已注入
    /// </summary>
    public bool IsOSTDllInjected()
    {
        var steamPath = _steamService.GetSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return false;

        foreach (var dll in OstDlls)
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
        var steamPath = _steamService.GetSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            return (false, "Steam 路径未设置，请先在设置中配置 Steam 路径。");

        if (!Directory.Exists(sourceDir))
            return (false, $"源目录不存在: {sourceDir}");

        var copied = new List<string>();
        var errors = new List<string>();

        foreach (var dll in OstDlls)
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
    /// 卸载 OST DLL（从 Steam 根目录删除并恢复备份）
    /// </summary>
    public async Task<(bool success, string message)> UnloadOstDllAsync()
    {
        var steamPath = _steamService.GetSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            return (false, "Steam 路径未设置。");

        var removed = new List<string>();
        var errors = new List<string>();

        foreach (var dll in OstDlls)
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
}
