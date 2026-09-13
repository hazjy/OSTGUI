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
