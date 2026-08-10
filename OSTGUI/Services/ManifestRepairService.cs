using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;
/// <summary>
/// 清单修复服务 - 自动修复 Lua / 补齐缺失清单 / 补齐版本配置
/// </summary>
public class ManifestRepairService
{
    private readonly SteamService _steamService;
    private readonly ConfigService _configService;
    private readonly SteamGameInfoService _gameInfoService;
    private readonly LuaBuilder _luaBuilder;

    private static readonly Regex RepairAddAppIdRegex = new(
        @"addappid\s*\(\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RepairSetManifestIdRegex = new(
        @"^\s*setManifestid\s*\(\s*(\d+)\s*,\s*""(\d+)""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public ManifestRepairService(SteamService steamService, ConfigService configService,
        SteamGameInfoService gameInfoService, LuaBuilder luaBuilder)
    {
        _steamService = steamService;
        _configService = configService;
        _gameInfoService = gameInfoService;
        _luaBuilder = luaBuilder;
    }

    private void Log(string message)
    {
        LogService.AddLog(message);
        System.Diagnostics.Debug.WriteLine($"[ManifestRepair] {message}");
    }

    /// <summary>
    /// 按 Id 获取源配置
    /// </summary>
    private ManifestSource? GetSource(string id)
    {
        var config = _configService.Config;
        return config.ManifestSources?.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    /// 获取源配置的 API Key，未配置时回退旧全局字段
    /// </summary>
    private string GetSourceApiKey(string sourceId, string legacyFallback)
    {
        var source = GetSource(sourceId);
        return !string.IsNullOrEmpty(source?.ApiKey) ? source.ApiKey : legacyFallback;
    }
    public async Task<(bool success, string message)> RepairManifestAsync(string appId)
    {
        var apiKey = GetSourceApiKey("mhub", _configService.Config.ManifestHubApiKey);
        if (string.IsNullOrEmpty(apiKey))
            return (false, "未配置 ManifestHub API Key，无法自动修复清单");

        try
        {
            return await DownloadMissingManifestsAsync(appId);
        }
        catch (Exception ex)
        {
            return (false, $"自动修复异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 修复 Lua 错误：重新生成完整 Lua 配置（含 depot key / token / DLC / 固定版本），不下载清单文件
    /// </summary>

    public async Task<(bool success, string message)> RepairLuaAsync(string appId, bool fixedVersion)
    {
        try
        {
            Log($"自动修复 Lua: AppID {appId}");

            var gameDetails = await _gameInfoService.GetGameDetailsFromSteamAsync(appId);
            if (gameDetails == null || gameDetails.Depots.Count == 0)
                return (false, "无法获取游戏 Depot 信息，无法重新生成 Lua");

            var depots = gameDetails.Depots.Values
                .Select(d => (d.DepotId, d.Manifests.Count > 0 ? d.Manifests[0] : "", 0L))
                .ToList();

            var lua = await _luaBuilder.BuildLuaAsync(appId, "自动修复", depots, fixedVersion, true, true);
            var luaOk = await _luaBuilder.WriteLuaAsync(appId, lua);

            return luaOk
                ? (true, $"已重新生成 Lua 配置 (AppID {appId})")
                : (false, "Lua 文件写入失败");
        }
        catch (Exception ex)
        {
            return (false, $"修复 Lua 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 补齐版本配置：检测固定版本方面的错误并修复
    /// （Lua 损坏/无 setManifestid 配置 → 重建固定配置；清单缺失 → 下载补齐）
    /// </summary>

    public async Task<(bool success, string message)> RepairVersionConfigAsync(string appId)
    {
        var apiKey = GetSourceApiKey("mhub", _configService.Config.ManifestHubApiKey);
        if (string.IsNullOrEmpty(apiKey))
            return (false, "未配置 ManifestHub API Key，无法补齐版本配置");

        try
        {
            var notes = new List<string>();
            var luaDir = _steamService.GetLuaConfigDir();
            var luaPath = Path.Combine(luaDir ?? "", $"{appId}.lua");

            // 记录修复前的实际版本模式：修复后不自动切换（原固定版本保持固定，原自动更新保持自动）
            var wasFixed = false;
            if (!string.IsNullOrEmpty(luaDir) && File.Exists(luaPath))
            {
                var before = await File.ReadAllTextAsync(luaPath);
                wasFixed = RepairSetManifestIdRegex.IsMatch(before);
            }

            // 1. Lua 损坏（缺失/无 addappid/残缺）→ 重建为固定版本配置
            if (await EnsureLuaValidAsync(appId, true))
                notes.Add("已修复 Lua 配置");

            // 2. Lua 有效但缺少 setManifestid 配置（含注释形式）→ 重建补充
            var hasConfig = false;
            if (!string.IsNullOrEmpty(luaDir))
            {
                var currentPath = Path.Combine(luaDir, $"{appId}.lua");
                if (File.Exists(currentPath))
                {
                    var content = await File.ReadAllTextAsync(currentPath);
                    hasConfig = content.Contains("setManifestid", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (!hasConfig)
            {
                if (await RebuildLuaAsync(appId, true))
                    notes.Add("已补充固定版本配置");
                else
                    return (false, "无法生成固定版本配置（未获取到 Depot 信息）");
            }

            // 3. 原非固定版本 → 将 setManifestid 转为注释（配置就绪但保持自动更新，不自动切换）
            if (!wasFixed && !string.IsNullOrEmpty(luaDir))
            {
                var currentPath = Path.Combine(luaDir, $"{appId}.lua");
                if (File.Exists(currentPath))
                {
                    var content = await File.ReadAllTextAsync(currentPath);
                    if (RepairSetManifestIdRegex.IsMatch(content))
                    {
                        var commented = Regex.Replace(content, @"^(setManifestid\s*\()", "--$1", RegexOptions.Multiline);
                        if (commented != content)
                        {
                            await _luaBuilder.WriteLuaAsync(appId, commented);
                            notes.Add("已保持自动更新模式（版本配置就绪）");
                        }
                    }
                }
            }

            // 4. 补齐缺失清单
            var (ok, message) = await DownloadMissingManifestsAsync(appId);
            if (notes.Count > 0)
                message = string.Join("；", notes) + "；" + message;

            return (ok, message);
        }
        catch (Exception ex)
        {
            return (false, $"补齐版本配置异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 收集该游戏 Lua 中所有条目的 depot + manifest gid，下载 depotcache 缺失的清单
    /// </summary>

    private async Task<(bool ok, string message)> DownloadMissingManifestsAsync(string appId)
    {
        // 1. 收集该游戏 Lua 中出现的所有 AppID（主游戏 + depot/DLC 条目）
        var luaIds = await GetAppIdsFromLuaAsync(appId);
        Log($"自动修复: 在 Lua 中发现 {luaIds.Count} 个条目");

        // 2. 为每个条目获取 depot + manifest gid（SteamCMD 优先）
        var manifestPairs = new Dictionary<string, string>();
        var lockObj = new object();
        using var sem = new SemaphoreSlim(4);
        var tasks = luaIds.Select(async id =>
        {
            await sem.WaitAsync();
            try
            {
                var game = await _gameInfoService.GetGameDetailsFromSteamAsync(id);
                if (game == null) return;
                foreach (var depot in game.Depots.Values)
                {
                    if (depot.Manifests.Count > 0)
                    {
                        lock (lockObj)
                            manifestPairs.TryAdd(depot.DepotId, depot.Manifests[0]);
                    }
                }
            }
            finally
            {
                sem.Release();
            }
        });
        await Task.WhenAll(tasks);

        if (manifestPairs.Count == 0)
            return (false, "未能获取任何 depot/manifest 信息");

        // 3. 找出缺失的清单文件
        var missing = manifestPairs
            .Where(p => !ManifestExists(p.Key, p.Value))
            .ToList();
        if (missing.Count == 0)
            return (true, "清单已完整，无需修复");

        Log($"发现 {missing.Count} 个缺失清单，开始下载...");

        // 4. 逐个下载缺失清单
        var errors = new List<string>();
        var okCount = 0;
        foreach (var (depotId, gid) in missing)
        {
            var (ok, err) = await DownloadSingleManifestAsync(depotId, gid);
            if (ok) okCount++;
            else errors.Add($"Depot {depotId}: {err}");
        }

        if (errors.Count == 0)
            return (true, $"已补齐 {okCount} 个缺失清单");
        return (false, $"修复未完全成功: 成功 {okCount} 个，失败 {errors.Count} 个（{string.Join("; ", errors)}）");
    }

    /// <summary>
    /// 检查 Lua 配置有效性：文件缺失 / 无 addappid / 内容残缺时重建（按原固定意图补 setManifestid 配置）
    /// </summary>

    private async Task<bool> EnsureLuaValidAsync(string appId, bool preferFixed)
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

    private async Task<bool> RebuildLuaAsync(string appId, bool fixedVersion)
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

    private static bool IsLuaContentBalanced(string content)
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

    private async Task<List<string>> GetAppIdsFromLuaAsync(string appId)
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

    /// <summary>
    /// 检查 depotcache 目录中是否存在 {depotId}_{gid}.manifest
    /// </summary>

    private bool ManifestExists(string depotId, string gid)
    {
        var name = $"{depotId}_{gid}.manifest";
        var dirs = new[]
        {
            _steamService.GetConfigDepotCacheDir(),
            _steamService.GetDepotCacheDir()
        };
        foreach (var dir in dirs)
        {
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 通过 ManifestHub 下载单个缺失清单并写入 depotcache（两个目录各一份）
    /// </summary>

    private async Task<(bool ok, string err)> DownloadSingleManifestAsync(string depotId, string gid)
    {
        var apiKey = GetSourceApiKey("mhub", _configService.Config.ManifestHubApiKey);
        if (string.IsNullOrEmpty(apiKey))
            return (false, "未配置 ManifestHub API Key");

        var mhubSource = GetSource("mhub");
        var url = !string.IsNullOrEmpty(mhubSource?.BaseUrl)
            ? mhubSource!.BuildUrl(null, depotId, gid)
            : $"https://api.manifesthub2.filegear-sg.me/manifest?apikey={apiKey}&depotid={depotId}&manifestid={gid}";

        var timeoutSeconds = Math.Max(60, _configService.Config.DownloadTimeout);
        using var dlClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

        try
        {
            Log($"下载缺失清单 Depot {depotId} ...");
            var response = await dlClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode}");

            var content = await response.Content.ReadAsByteArrayAsync();
            var fileName = $"{depotId}_{gid}.manifest";
            var depotcachePaths = new[]
            {
                _steamService.GetConfigDepotCacheDir(),
                _steamService.GetDepotCacheDir()
            };
            foreach (var p in depotcachePaths)
            {
                if (!string.IsNullOrEmpty(p))
                {
                    Directory.CreateDirectory(p);
                    await File.WriteAllBytesAsync(Path.Combine(p, fileName), content);
                }
            }
            Log($"已补齐 {fileName}");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
