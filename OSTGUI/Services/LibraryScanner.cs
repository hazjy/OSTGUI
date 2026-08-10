using System.Text.RegularExpressions;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 入库游戏扫描服务 - 扫描 Lua 目录、解析条目、检测 Lua/清单错误
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
    /// 检查单个游戏 Lua 的入库状态（ok / error / manifest），并附具体原因
    /// </summary>

    public (string status, string detail, string versionMode) GetLuaStatus(string appId)
    {
        var luaDir = _steamService.GetLuaConfigDir();
        if (string.IsNullOrEmpty(luaDir))
            return ("error", "未找到 Lua 配置目录", "auto");

        var path = Path.Combine(luaDir, $"{appId}.lua");
        if (!File.Exists(path))
            return ("error", "Lua 文件不存在", "auto");

        var content = File.ReadAllText(path);
        var versionMode = SetManifestIdRegex.IsMatch(content) ? "fixed" : "auto";
        if (!AddAppIdRegex.IsMatch(content))
            return ("error", "未找到有效的 addappid 指令", versionMode);

        var (integrityOk, integrityReason) = CheckLuaIntegrity(content);
        if (!integrityOk)
            return ("error", integrityReason, versionMode);

        var pairs = SetManifestIdRegex.Matches(content)
            .Select(m => (depotId: m.Groups[1].Value, gid: m.Groups[2].Value))
            .ToList();
        if (pairs.Count > 0)
        {
            var missing = pairs
                .Where(p => !ManifestFileExists(p.depotId, p.gid))
                .Select(p => $"{p.depotId}_{p.gid}.manifest")
                .ToList();
            if (missing.Count > 0)
                return ("manifest", $"缺少 {missing.Count} 个清单文件: {string.Join(", ", missing)}", versionMode);
        }

        return ("ok", "", versionMode);
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
        {
                // Lua 文件存在但没有有效的 addappid → 标记 Lua 错误
                var badAppId = Path.GetFileNameWithoutExtension(fileName);
                if (string.IsNullOrEmpty(badAppId) || !badAppId.All(char.IsDigit)) return;

                items.Add(new LibraryItem
                {
                    AppId = badAppId,
                    GameName = $"AppID {badAppId}",
                    FileName = fileName,
                    UnlockerType = "ost",
                    Status = "error",
                    StatusDetail = "未找到有效的 addappid 指令",
                    VersionMode = "auto",
                    LastModified = File.GetLastWriteTime(filePath)
                });
                return;
            }

            var (integrityOk, integrityReason) = CheckLuaIntegrity(content);
            if (!integrityOk)
            {
                var brokenAppId = appIdMatch.Groups[1].Value;
                items.Add(new LibraryItem
                {
                    AppId = brokenAppId,
                    GameName = $"AppID {brokenAppId}",
                    FileName = fileName,
                    UnlockerType = "ost",
                    Status = "error",
                    StatusDetail = integrityReason,
                    VersionMode = "auto",
                    LastModified = File.GetLastWriteTime(filePath)
                });
                return;
            }

            var appId = appIdMatch.Groups[1].Value;
            if (seenAppIds.Contains(appId)) return;
            seenAppIds.Add(appId);

            var hasDepotKey = appIdMatch.Groups[3].Success && !string.IsNullOrEmpty(appIdMatch.Groups[3].Value);
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

            // 提取 manifest (depotId, gid) 对
            var manifestPairs = SetManifestIdRegex.Matches(content)
                .Select(m => (depotId: m.Groups[1].Value, gid: m.Groups[2].Value))
                .ToList();
            var manifestIds = manifestPairs.Select(p => p.gid).ToList();

            // Lua 中实际出现的所有 AppID（排除主游戏，用于判断 DLC 是否已入库）
            var installedIds = AddAppIdRegex.Matches(content)
                .Select(m => m.Groups[1].Value)
                .Where(id => id != appId)
                .Distinct()
                .ToList();

            // 检查 depotcache 中是否存在对应 manifest 文件
            var status = "ok";
            var statusDetail = "";
            if (manifestPairs.Count > 0)
            {
                var missing = manifestPairs
                    .Where(p => !ManifestFileExists(p.depotId, p.gid))
                    .Select(p => $"{p.depotId}_{p.gid}.manifest")
                    .ToList();
                if (missing.Count > 0)
                {
                    status = "manifest";
                    statusDetail = $"缺少 {missing.Count} 个清单文件: {string.Join(", ", missing)}";
                }
            }

            var item = new LibraryItem
            {
                AppId = appId,
                GameName = $"AppID {appId}",
                FileName = fileName,
                UnlockerType = "ost",
                Status = status,
                StatusDetail = statusDetail,
                InstalledAppIds = installedIds,
                VersionMode = versionMode,
                DepotKeySet = hasDepotKey ? "已设置" : "",
                ManifestIds = manifestIds,
                LastModified = File.GetLastWriteTime(filePath)
            };

            items.Add(item);
        }
        catch { }
    }

    /// <summary>
    /// 检查 Lua 内容完整性：忽略行注释后统计括号与引号是否配对，
    /// 用于发现截断残留等语法残缺（如孤立的 ", 15380)" 片段）
    /// </summary>

    private static (bool ok, string reason) CheckLuaIntegrity(string content)
    {
        var parenBalance = 0;
        var quoteCount = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;
            var commentIdx = line.IndexOf("--", StringComparison.Ordinal);
            if (commentIdx >= 0)
                line = line.Substring(0, commentIdx);

            parenBalance += line.Count(c => c == '(');
            parenBalance -= line.Count(c => c == ')');
            quoteCount += line.Count(c => c == '"');
        }

        if (parenBalance != 0)
            return (false, "Lua 内容不完整（括号不匹配），可能存在截断残留");
        if (quoteCount % 2 != 0)
            return (false, "Lua 内容不完整（引号未闭合）");
        return (true, "");
    }

    /// <summary>
    /// 检查 depotcache 目录中是否存在 {depotId}_{gid}.manifest
    /// </summary>

    public bool ManifestFileExists(string depotId, string gid)
    {
        var dirs = new[]
        {
            _steamService.GetConfigDepotCacheDir(),
            _steamService.GetDepotCacheDir()
        };
        var name = $"{depotId}_{gid}.manifest";
        foreach (var dir in dirs)
        {
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name)))
                return true;
        }
        return false;
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
                Status = "ok",
                VersionMode = "auto",
                LastModified = File.GetLastWriteTime(filePath)
            });

            // 扫描所有 addappid 引用
            var luaDir = Path.GetDirectoryName(filePath) ?? "";
            foreach (Match m in AddAppIdRegex.Matches(content))
            {
                var appId = m.Groups[1].Value;
                if (seenAppIds.Contains(appId)) continue;
                seenAppIds.Add(appId);

                var luaExists = !string.IsNullOrEmpty(luaDir) && File.Exists(Path.Combine(luaDir, $"{appId}.lua"));

                items.Add(new LibraryItem
                {
                    AppId = appId,
                    GameName = $"AppID {appId}",
                    FileName = $"{appId}.lua",
                    UnlockerType = "ost",
                    Status = luaExists ? "ok" : "error",
                    StatusDetail = luaExists ? "" : "steamtools.lua 有引用但缺少对应 Lua 文件",
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
