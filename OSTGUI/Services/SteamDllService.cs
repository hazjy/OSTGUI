namespace OSTGUI.Services;

/// <summary>
/// OST DLL 管理服务 - 注入/卸载/状态检查
/// </summary>
public class SteamDllService
{
    private readonly SteamService _steamService;

    private static readonly string[] OstDlls = { "dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll" };

    public SteamDllService(SteamService steamService)
    {
        _steamService = steamService;
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
