using System.Text.RegularExpressions;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// Lua 配置文件读写服务
/// 管理 OpenSteamTool 的 Lua 配置文件
/// </summary>
public class LuaConfigService
{
    private readonly SteamService _steamService;
    private readonly LibraryScanner _scanner;

    // 匹配 addappid(...) 模式
    // addappid(1361510)
    // addappid(1361511, 0, "depotkey")
    private static readonly Regex AddAppIdRegex = new(
        @"addappid\s*\(\s*(\d+)\s*(?:,\s*(\d+)\s*,\s*""([^""]*)""\s*)?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 匹配 setManifestid(...) 模式
    private static readonly Regex SetManifestIdRegex = new(
        @"^\s*setManifestid\s*\(\s*(\d+)\s*,\s*""(\d+)""\s*(?:,\s*(\d+)\s*)?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // 匹配注释掉的 setManifestid
    private static readonly Regex CommentedManifestIdRegex = new(
        @"^\s*--+\s*setManifestid\s*\(\s*(\d+)\s*,\s*""(\d+)""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // 匹配 addtoken(...) 
    private static readonly Regex AddTokenRegex = new(
        @"addtoken\s*\(\s*(\d+)\s*,\s*""([^""]*)""\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 匹配 setAppTicket(...)
    private static readonly Regex SetAppTicketRegex = new(
        @"setAppTicket\s*\(\s*(\d+)\s*,\s*""([^""]*)""\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 匹配 setETicket(...)
    private static readonly Regex SetETicketRegex = new(
        @"setETicket\s*\(\s*(\d+)\s*,\s*""([^""]*)""\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public LuaConfigService(SteamService steamService, LibraryScanner scanner)
    {
        _steamService = steamService;
        _scanner = scanner;
    }

    /// <summary>
    /// 扫描所有已入库的游戏（委托给 LibraryScanner）
    /// </summary>
    public async Task<List<LibraryItem>> ScanLibraryAsync() => await _scanner.ScanLibraryAsync();

    /// <summary>
    /// 检查单个游戏 Lua 的入库状态（委托给 LibraryScanner）
    /// </summary>
    public (string status, string detail, string versionMode) GetLuaStatus(string appId)
        => _scanner.GetLuaStatus(appId);

    /// <summary>
    /// 为游戏生成 Lua 配置内容
    /// 格式: addappid(ID, KeyType, "DepotKey") 或 addappid(ID)
    /// </summary>
    public string GenerateLuaContent(
        string appId,
        List<(string depotId, string depotKey, string manifestGid, long manifestSize)> depots = null!,
        bool fixedVersion = false,
        string? accessToken = null)
    {
        var lines = new List<string>
        {
            $"-- OpenSteamTool 入库配置 - AppID {appId}",
            $"-- 由 OSTGUI 自动生成",
            $""
        };

        // 基础入库 - 带 depot key 的格式: addappid(ID, 1, "key")
        if (depots != null && depots.Count > 0)
        {
            foreach (var depot in depots)
            {
                if (!string.IsNullOrEmpty(depot.depotKey))
                {
                    // 有密钥: addappid(DepotID, 1, "key")
                    lines.Add($"addappid({depot.depotId}, 1, \"{depot.depotKey}\")");
                }
                else
                {
                    // 无密钥: addappid(DepotID)
                    lines.Add($"addappid({depot.depotId})");
                }
            }
        }
        else
        {
            // 无 depot 信息，仅入库 AppID
            lines.Add($"addappid({appId})");
        }

        // 访问令牌
        if (!string.IsNullOrEmpty(accessToken))
        {
            lines.Add($"");
            lines.Add($"addtoken({appId}, \"{accessToken}\")");
        }

        // 固定版本（manifest 绑定）
        if (fixedVersion && depots != null)
        {
            lines.Add($"");
            lines.Add($"-- 固定版本配置");
            foreach (var depot in depots.Where(d => !string.IsNullOrEmpty(d.manifestGid)))
            {
                if (depot.manifestSize > 0)
                    lines.Add($"setManifestid({depot.depotId}, \"{depot.manifestGid}\", {depot.manifestSize})");
                else
                    lines.Add($"setManifestid({depot.depotId}, \"{depot.manifestGid}\")");
            }
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// 写入 Lua 文件
    /// </summary>
    public async Task<(bool success, string message, string filePath)> WriteLuaFileAsync(string appId, string content)
    {
        var luaDir = _steamService.GetLuaConfigDir();
        if (string.IsNullOrEmpty(luaDir))
            return (false, "未找到 Lua 配置目录，请检查 Steam 路径设置。", "");

        try
        {
            var filePath = Path.Combine(luaDir, $"{appId}.lua");
            await WriteFileAtomicallyAsync(filePath, content);

            // 同时更新 steamtools.lua 主配置（如果有的话）
            var stLuaPath = Path.Combine(luaDir, "steamtools.lua");
            if (File.Exists(stLuaPath))
            {
                var stContent = await File.ReadAllTextAsync(stLuaPath);
                var addLine = $"addappid({appId})";
                if (!stContent.Contains(addLine))
                {
                    stContent += $"\n{addLine}\n";
                    await WriteFileAtomicallyAsync(stLuaPath, stContent);
                }
            }

            return (true, $"配置文件已写入: {filePath}", filePath);
        }
        catch (Exception ex)
        {
            return (false, $"写入配置文件失败: {ex.Message}", "");
        }
    }

    /// <summary>
    /// 删除入库游戏的 Lua 配置
    /// </summary>
    public async Task<(bool success, string message)> DeleteLibraryItemAsync(LibraryItem item)
    {
        var luaDir = _steamService.GetLuaConfigDir();
        if (string.IsNullOrEmpty(luaDir))
            return (false, "未找到 Lua 配置目录。");

        var errors = new List<string>();

        try
        {
            // 删除单独的 lua 文件
            if (!string.IsNullOrEmpty(item.FileName) && !item.FileName.Contains("缺失"))
            {
                var filePath = Path.Combine(luaDir, item.FileName);
                if (File.Exists(filePath))
                {
                    await Task.Run(() => File.Delete(filePath));
                }
            }

            // 从 steamtools.lua 中移除 addappid 引用
            var stLuaPath = Path.Combine(luaDir, "steamtools.lua");
            if (File.Exists(stLuaPath))
            {
                var content = await File.ReadAllTextAsync(stLuaPath);
                var pattern = $@"addappid\s*\(\s*{item.AppId}\s*(?:,\s*\d+\s*,\s*""[^""]*""\s*)?\s*\)\s*\r?\n?";
                content = Regex.Replace(content, pattern, "", RegexOptions.IgnoreCase);
                await WriteFileAtomicallyAsync(stLuaPath, content);
            }

            return (true, $"已删除 AppID {item.AppId} 的入库配置");
        }
        catch (Exception ex)
        {
            return (false, $"删除失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 切换版本模式（固定版本 ⇄ 自动更新）
    /// </summary>
    public async Task<(bool success, string message, string newMode)> ToggleVersionModeAsync(LibraryItem item)
    {
        var luaDir = _steamService.GetLuaConfigDir();
        if (string.IsNullOrEmpty(luaDir))
            return (false, "未找到 Lua 配置目录。", "auto");

        if (item.AppId == "N/A" || string.IsNullOrEmpty(item.FileName))
            return (false, "无法修改此项目的版本模式。", item.VersionMode);

        try
        {
            var filePath = Path.Combine(luaDir, item.FileName);
            if (!File.Exists(filePath))
                return (false, "配置文件不存在。", item.VersionMode);

            var content = await File.ReadAllTextAsync(filePath);
            string newContent;
            string newMode;

            // 以 Lua 实际内容判断当前模式，避免内存状态与文件不一致导致误判
            var isActuallyFixed = SetManifestIdRegex.IsMatch(content);
            var hasCommentedConfig = CommentedManifestIdRegex.IsMatch(content);

            if (isActuallyFixed)
            {
                // 实际是固定版本 → 自动更新（注释掉 setManifestid）
                newContent = SetManifestIdRegex.Replace(content, "--$&");
                newMode = "auto";
            }
            else
            {
                // 实际是自动更新 → 固定版本：需要 Lua 中存在可用的 setManifestid 配置
                if (!hasCommentedConfig)
                    return (false, "切换失败：Lua 缺少对应清单配置", "auto");

                // 自动更新 → 固定版本（取消注释 setManifestid）
                newContent = Regex.Replace(
                    content,
                    @"^(\s*)--+\s*(setManifestid\s*\()",
                    "$1$2",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline);
                newMode = "fixed";

                // 检查 setManifestid 对应的清单文件是否齐全
                var missing = SetManifestIdRegex.Matches(newContent)
                    .Select(m => (depotId: m.Groups[1].Value, gid: m.Groups[2].Value))
                    .Where(p => !_scanner.ManifestFileExists(p.depotId, p.gid))
                    .Select(p => $"{p.depotId}_{p.gid}.manifest")
                    .ToList();
                if (missing.Count > 0)
                    return (false, $"切换失败：Lua 缺少对应清单配置（缺少 {string.Join(", ", missing)}）", "auto");
            }

            await WriteFileAtomicallyAsync(filePath, newContent);
            item.VersionMode = newMode;
            item.LastModified = DateTime.Now;

            var statusMsg = newMode == "fixed"
                ? $"AppID {item.AppId} 已锁定为固定版本（Manifest 版本已锁定，不会随 Steam 更新而变化）"
                : $"AppID {item.AppId} 已切换为自动更新（将跟随 Steam 自动获取最新版本）";

            return (true, statusMsg, newMode);
        }
        catch (Exception ex)
        {
            return (false, $"切换版本模式失败: {ex.Message}", item.VersionMode);
        }
    }

    /// <summary>
    /// 读取特定 AppID 的 Lua 文件内容
    /// </summary>
    public async Task<string?> ReadLuaContentAsync(string appId)
    {
        var luaDir = _steamService.GetLuaConfigDir();
        if (string.IsNullOrEmpty(luaDir)) return null;

        var filePath = Path.Combine(luaDir, $"{appId}.lua");
        if (!File.Exists(filePath)) return null;

        return await File.ReadAllTextAsync(filePath);
    }

    /// <summary>
    /// 获取数据库中所有 AppID 集合
    /// </summary>
    public HashSet<string> GetAllUnlockedAppIds(List<LibraryItem> libraryItems)
    {
        return libraryItems
            .Where(i => i.AppId != "N/A")
            .Select(i => i.AppId)
            .ToHashSet();
    }

    /// <summary>
    /// 原子写入文件（先写临时文件再覆盖），避免 OpenSteamTool 监视器读到半截内容；
    /// 统一使用无 BOM UTF-8
    /// </summary>
    private static async Task WriteFileAtomicallyAsync(string path, string content)
    {
        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, content, new System.Text.UTF8Encoding(false));
        File.Move(tmpPath, path, true);
    }
}
