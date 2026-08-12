using System.Text.RegularExpressions;

namespace OSTGUI.Services;

/// <summary>
/// Lua 修复服务 - 校验/重建 Lua 配置、读取 Lua 中的 AppID 条目
/// </summary>
public class LuaRepairService
{
    private readonly SteamService _steamService;
    private readonly SteamGameInfoService _gameInfoService;
    private readonly LuaBuilder _luaBuilder;

    private static readonly Regex RepairAddAppIdRegex = new(
        @"addappid\s*\(\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public LuaRepairService(SteamService steamService, SteamGameInfoService gameInfoService, LuaBuilder luaBuilder)
    {
        _steamService = steamService;
        _gameInfoService = gameInfoService;
        _luaBuilder = luaBuilder;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[LuaRepair] {message}");
    }

    /// <summary>
    /// 检查 Lua 配置有效性：文件缺失 / 无 addappid / 内容残缺时重建（按原固定意图补 setManifestid 配置）
    /// </summary>
    public async Task<bool> EnsureLuaValidAsync(string appId, bool preferFixed)
    {
        var luaDir = _steamService.GetLuaConfigDir();
        if (string.IsNullOrEmpty(luaDir)) return false;

        var luaPath = Path.Combine(luaDir, $"{appId}.lua");
        if (!File.Exists(luaPath))
            return await RebuildLuaAsync(appId, preferFixed);

        var content = await File.ReadAllTextAsync(luaPath);
        if (!RepairAddAppIdRegex.IsMatch(content) || !IsLuaContentBalanced(content))
        {
            var hadManifestConfig = content.Contains("setManifestid", StringComparison.OrdinalIgnoreCase);
            return await RebuildLuaAsync(appId, preferFixed || hadManifestConfig);
        }

        return false;
    }

    /// <summary>
    /// 重建 Lua 配置（含 depot key / token / DLC / 固定版本配置）
    /// </summary>
    public async Task<bool> RebuildLuaAsync(string appId, bool fixedVersion)
    {
        try
        {
            Log($"重建 Lua 配置: AppID {appId}");
            var gameDetails = await _gameInfoService.GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return false;

            var depots = gameDetails.Depots.Values
                .Select(d => (d.DepotId, d.Manifests.Count > 0 ? d.Manifests[0] : "", 0L))
                .ToList();

            var lua = await _luaBuilder.BuildLuaAsync(appId, "自动修复", depots, fixedVersion, true, true);
            var ok = await _luaBuilder.WriteLuaAsync(appId, lua);
            if (ok) Log("已重建 Lua 配置");
            return ok;
        }
        catch (Exception ex)
        {
            Log($"重建 Lua 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查 Lua 内容括号/引号是否配对（发现截断残留）
    /// </summary>
    public static bool IsLuaContentBalanced(string content)
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
        return parenBalance == 0 && quoteCount % 2 == 0;
    }

    /// <summary>
    /// 读取该游戏 Lua 中出现的所有 addappid（主游戏 + depot/DLC 条目），文件不存在时仅返回主 AppID
    /// </summary>
    public async Task<List<string>> GetAppIdsFromLuaAsync(string appId)
    {
        var luaDir = _steamService.GetLuaConfigDir();
        var ids = new List<string>();

        if (!string.IsNullOrEmpty(luaDir))
        {
            var luaPath = Path.Combine(luaDir, $"{appId}.lua");
            if (File.Exists(luaPath))
            {
                var content = await File.ReadAllTextAsync(luaPath);
                foreach (Match m in RepairAddAppIdRegex.Matches(content))
                {
                    var id = m.Groups[1].Value;
                    if (!ids.Contains(id))
                        ids.Add(id);
                }
            }
        }

        if (ids.Count == 0)
            ids.Add(appId);
        return ids;
    }
}
