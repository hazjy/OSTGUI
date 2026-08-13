using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;
/// <summary>
/// Lua 配置生成服务 - 生成完整解锁 Lua（depot key / token / DLC / 固定版本）并原子写入
/// </summary>
public class LuaBuilder
{
    private readonly SteamService _steamService;
    private readonly SteamGameInfoService _gameInfoService;
    private readonly SudamaKeyCache _sudamaCache;

    public LuaBuilder(SteamService steamService, SteamGameInfoService gameInfoService, SudamaKeyCache sudamaCache)
    {
        _steamService = steamService;
        _gameInfoService = gameInfoService;
        _sudamaCache = sudamaCache;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[LuaBuilder] {message}");
    }
    public async Task<(string lua, List<string> missingKeyDepots)> BuildLuaAsync(
        string appId,
        string sourceName,
        List<(string depotId, string manifestGid, long manifestSize)> depots,
        bool fixedVersion,
        bool patchDepotKey,
        bool addAllDlc)
    {
        // 密钥与令牌获取失败不阻断，尽力而为
        var keys = patchDepotKey ? await _sudamaCache.GetSudamaKeysAsync() : new Dictionary<string, string>();
        var tokens = await _sudamaCache.GetAccessTokensAsync();
        var missingKeyDepots = new List<string>();

        // 补全全部 depot（SteamCMD 列表），避免缺失 depot 下载时无密钥报"内容加密"
        var allDepots = await MergeAllDepotsAsync(appId, depots);

        var lines = new List<string>
        {
            $"-- OpenSteamTool 入库配置 - AppID {appId}",
            $"-- 来源: {sourceName}",
            ""
        };

        lines.Add($"addappid({appId})");
        lines.Add("");

        foreach (var (depotId, _, _) in allDepots)
        {
            // OpenSteamTool 只接受恰好 64 字符的 depot key
            var hasKey = keys.TryGetValue(depotId, out var key) && key.Length == 64;
            if (patchDepotKey && !hasKey)
                missingKeyDepots.Add(depotId);
            lines.Add(hasKey ? $"addappid({depotId}, 1, \"{key}\")" : $"addappid({depotId})");
        }
        if (patchDepotKey && missingKeyDepots.Count > 0)
        {
            Log($"警告: 以下 depot 未找到解密密钥: {string.Join(", ", missingKeyDepots)}" +
                "（Steam depot 内容均为 AES-256 加密，缺少密钥将无法解密下载）");
        }

        // 添加所有 DLC（可选）：获取 DLC 列表，跳过已在 depots 中的，逐个 addappid
        if (addAllDlc)
        {
            var existingIds = new HashSet<string> { appId };
            foreach (var (depotId, _, _) in allDepots)
                existingIds.Add(depotId);

            var dlcIds = await _gameInfoService.GetDlcIdsAsync(appId);
            var newDlcs = dlcIds.Where(d => !existingIds.Contains(d)).ToList();
            if (newDlcs.Count > 0)
            {
                lines.Add("");
                lines.Add("-- 所有 DLC");
                foreach (var dlcId in newDlcs)
                    lines.Add($"addappid({dlcId})");
                Log($"已添加 {newDlcs.Count} 个 DLC");
            }
        }

        if (fixedVersion)
        {
            var fixedLines = allDepots
                .Where(d => !string.IsNullOrEmpty(d.manifestGid))
                .Select(d => d.manifestSize > 0
                    ? $"setManifestid({d.depotId}, \"{d.manifestGid}\", {d.manifestSize})"
                    : $"setManifestid({d.depotId}, \"{d.manifestGid}\")")
                .ToList();

            if (fixedLines.Count > 0)
            {
                lines.Add("");
                lines.Add("-- 固定版本配置");
                lines.AddRange(fixedLines);
            }
        }

        if (tokens.TryGetValue(appId, out var token) && !string.IsNullOrEmpty(token))
        {
            lines.Add("");
            lines.Add($"addtoken({appId}, \"{token}\")");
        }

        return (string.Join("\n", lines) + "\n", missingKeyDepots);
    }

    /// <summary>
    /// 合并 SteamCMD 返回的全部 depot，补全缺失项。
    /// 没有清单 GID 的 depot 不固定版本，下载时由 OST 内核自动获取 manifest 请求码。
    /// </summary>
    private async Task<List<(string depotId, string manifestGid, long manifestSize)>> MergeAllDepotsAsync(
        string appId,
        List<(string depotId, string manifestGid, long manifestSize)> known)
    {
        var merged = new Dictionary<string, (string gid, long size)>();
        foreach (var (id, gid, size) in known)
            merged[id] = (gid, size);

        try
        {
            var game = await _gameInfoService.GetGameDetailsFromSteamAsync(appId);
            if (game != null)
            {
                foreach (var depot in game.Depots.Values)
                {
                    if (merged.ContainsKey(depot.DepotId)) continue;
                    var gid = depot.Manifests.Count > 0 ? depot.Manifests[0] : "";
                    merged[depot.DepotId] = (gid, depot.MaxSize);
                    Log($"补全缺失 depot: {depot.DepotId}" +
                        (string.IsNullOrEmpty(gid) ? "（无清单 GID，由内核自动获取）" : ""));
                }
            }
        }
        catch (Exception ex)
        {
            Log($"补全 depot 失败: {ex.Message}");
        }

        return merged
            .Select(kv => (kv.Key, kv.Value.gid, kv.Value.size))
            .ToList();
    }

    /// <summary>
    /// 获取游戏的 DLC AppID 列表（SteamCMD API 优先，Steam 官方 API 兜底）
    /// </summary>

    public async Task<bool> WriteLuaAsync(string appId, string content)
    {
        try
        {
            var luaDir = _steamService.GetLuaConfigDir();
            if (string.IsNullOrEmpty(luaDir))
            {
                Log("警告: 未找到 Lua 配置目录");
                return false;
            }

            Directory.CreateDirectory(luaDir);
            var luaFilePath = Path.Combine(luaDir, $"{appId}.lua");
            await WriteFileAtomicallyAsync(luaFilePath, content);
            Log($"已生成 Lua 文件: {luaFilePath}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"写入 Lua 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 原子写入文件（先写临时文件再覆盖），避免文件监视器读到半截内容；
    /// 统一使用无 BOM UTF-8，兼容 OpenSteamTool 的 Lua 解析
    /// </summary>

    private static async Task WriteFileAtomicallyAsync(string path, string content)
    {
        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, content, new System.Text.UTF8Encoding(false));
        File.Move(tmpPath, path, true);
    }

    /// <summary>
    /// 复制 manifest 文件到 Steam 的 depotcache 目录（config/depotcache 与 depotcache 各一份）
    /// </summary>


}
