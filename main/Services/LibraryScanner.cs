using System.Text.RegularExpressions;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 入库游戏扫描服务 - 扫描 Lua 目录、解析条目
/// </summary>
public class LibraryScanner
{
    private readonly SteamService _steamService;

    // 匹配 addappid(...) 模式
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

    public LibraryScanner(SteamService steamService)
    {
        _steamService = steamService;
    }
    public async Task<List<LibraryItem>> ScanLibraryAsync()
    {
        var items = new List<LibraryItem>();
        var luaDir = _steamService.GetLuaConfigDir();
        if (string.IsNullOrEmpty(luaDir) || !Directory.Exists(luaDir))
            return items;

        var seenAppIds = new HashSet<string>();

        await Task.Run(() =>
        {
            var luaFiles = Directory.GetFiles(luaDir, "*.lua");

            // 先解析普通游戏 lua（真实状态），最后处理 steamtools.lua，
            // 否则 steamtools 引用会先登记 AppID，导致游戏 lua 被 seenAppIds 跳过、错误检测失效
            foreach (var file in luaFiles)
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.Equals("steamtools.lua", StringComparison.OrdinalIgnoreCase))
                    ParseGameLua(file, items, seenAppIds);
            }

            var steamToolsFile = luaFiles.FirstOrDefault(f =>
                Path.GetFileName(f).Equals("steamtools.lua", StringComparison.OrdinalIgnoreCase));
            if (steamToolsFile != null)
                ParseSteamToolsLua(steamToolsFile, items, seenAppIds);
        });

        // 按 AppID 排序
        items.Sort((a, b) =>
        {
            if (int.TryParse(a.AppId, out var ia) && int.TryParse(b.AppId, out var ib))
                return ib.CompareTo(ia); // 倒序
            return string.Compare(b.AppId, a.AppId, StringComparison.Ordinal);
        });

        return items;
    }

    /// <summary>
    /// 解析普通游戏 Lua 文件
    /// </summary>

    private void ParseGameLua(string filePath, List<LibraryItem> items, HashSet<string> seenAppIds)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var fileName = Path.GetFileName(filePath);

        var appIdMatch = AddAppIdRegex.Match(content);
        if (!appIdMatch.Success)
            return;

            var appId = appIdMatch.Groups[1].Value;
            if (seenAppIds.Contains(appId)) return;
            seenAppIds.Add(appId);

            var hasSetManifestId = SetManifestIdRegex.IsMatch(content);
            var hasCommentedManifest = CommentedManifestIdRegex.IsMatch(content);

            // 判断版本模式
            string versionMode;
            if (hasSetManifestId)
                versionMode = "fixed";
            else if (hasCommentedManifest)
                versionMode = "auto";
            else
                versionMode = "auto"; // 默认自动

            // Lua 中实际出现的所有 AppID（排除主游戏，用于判断 DLC 是否已入库）
            var installedIds = AddAppIdRegex.Matches(content)
                .Select(m => m.Groups[1].Value)
                .Where(id => id != appId)
                .Distinct()
                .ToList();

            var item = new LibraryItem
            {
                AppId = appId,
                GameName = $"AppID {appId}",
                FileName = fileName,
                UnlockerType = "ost",
                InstalledAppIds = installedIds,
                VersionMode = versionMode,
                LastModified = File.GetLastWriteTime(filePath)
            };

            items.Add(item);
        }
        catch { }
    }

    /// <summary>
    /// 解析 steamtools.lua（主配置文件）
    /// </summary>

    private void ParseSteamToolsLua(string filePath, List<LibraryItem> items, HashSet<string> seenAppIds)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var fileName = Path.GetFileName(filePath);

            // 添加核心文件条目
            items.Add(new LibraryItem
            {
                AppId = "N/A",
                GameName = "OpenSteamTool 核心配置",
                FileName = fileName,
                UnlockerType = "ost",
                VersionMode = "auto",
                LastModified = File.GetLastWriteTime(filePath)
            });

            // 扫描所有 addappid 引用
            foreach (Match m in AddAppIdRegex.Matches(content))
            {
                var appId = m.Groups[1].Value;
                if (seenAppIds.Contains(appId)) continue;
                seenAppIds.Add(appId);

                items.Add(new LibraryItem
                {
                    AppId = appId,
                    GameName = $"AppID {appId}",
                    FileName = $"{appId}.lua",
                    UnlockerType = "ost",
                    VersionMode = "auto",
                });
            }
        }
        catch { }
    }

    /// <summary>
    /// 为游戏生成 Lua 配置内容
    /// 格式: addappid(ID, KeyType, "DepotKey") 或 addappid(ID)
    /// </summary>


}
