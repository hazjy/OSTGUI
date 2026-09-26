using System.Text.Json;

namespace OSTGUI.Services;

/// <summary>
/// 成就页左侧列表的缓存（2026-09-26）。
///
/// 每次重开应用，成就页都要：扫 lua 目录 → 解析 appinfo.vdf → 起子进程问 Steam 拥有关系（约 1 秒）
/// → 解析 schema 取名字。这份缓存把结果留在
/// <c>%LOCALAPPDATA%\OSTGUI\cache\achievement-list.json</c>，重开直接铺列表。
///
/// 失效规则（用户定的）：
/// - **lua 档**：lua 目录 mtime 或 <c>*.lua</c> 文件数变了就作废（入库/删档会动它们）；
/// - **appinfo.vdf**：大小或 mtime 变了就作废（Steam 更新会动它）；
/// - **正版拥有集合**：不设 TTL —— 只在用户点「刷新」时重查（所以缓存命中时不会起 stats-owned 子进程）。
///
/// ponytail: 用 mtime + 文件数当失效依据是启发式——外部拷文件又不改目录 mtime 的极端情况会漏，
/// 那时点一下「刷新」即可（它走强制重扫并重写缓存）。
/// </summary>
public class AchievementListCache
{
    private const string CurrentVersion = "1";

    /// <summary>缓存内容（只存列表要用的最小字段，不塞 LibraryItem 那套带 BitmapImage 的模型）</summary>
    public sealed class Snapshot
    {
        public string Version { get; set; } = CurrentVersion;
        public DateTime SavedAt { get; set; }
        public long LuaDirTicks { get; set; }
        public int LuaFileCount { get; set; }
        public long AppInfoSize { get; set; }
        public long AppInfoTicks { get; set; }
        public List<Item> LuaGames { get; set; } = new();
        public List<Item> OwnedGames { get; set; } = new();
    }

    public sealed class Item
    {
        public string AppId { get; set; } = "";
        public string GameName { get; set; } = "";
        public string SourceTag { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string CachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI", "cache", "achievement-list.json");

    public static Snapshot? Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(CachePath), Options);
            if (snap == null || snap.Version != CurrentVersion) return null;
            return snap;
        }
        catch (Exception ex)
        {
            LogService.Diag($"成就列表缓存读取失败（当没有处理）: {ex.Message}");
            return null;
        }
    }

    public static void Save(Snapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Options));
            File.Move(temp, CachePath, overwrite: true);   // 原子替换：不会留下半截文件（半截＝下次当没有）
            LogService.Diag($"成就列表缓存已写入（lua {snapshot.LuaGames.Count} / 正版 {snapshot.OwnedGames.Count}）");
        }
        catch (Exception ex)
        {
            LogService.Diag($"成就列表缓存写入失败: {ex.Message}");
        }
    }

    public static void Delete()
    {
        try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
    }

    /// <summary>缓存是否还对得上当前文件状态（lua 目录 + appinfo.vdf 都没变才算命中）</summary>
    public static bool IsFresh(Snapshot snapshot, string luaDir, string steamPath)
    {
        var (ticks, count) = LuaStamp(luaDir);
        if (ticks != snapshot.LuaDirTicks || count != snapshot.LuaFileCount) return false;

        var (size, appTicks) = AppInfoStamp(steamPath);
        return size == snapshot.AppInfoSize && appTicks == snapshot.AppInfoTicks;
    }

    /// <summary>lua 目录指纹（不存在就全 0，等价"没变化"）</summary>
    public static (long Ticks, int Count) LuaStamp(string luaDir)
    {
        try
        {
            if (string.IsNullOrEmpty(luaDir) || !Directory.Exists(luaDir)) return (0, 0);
            return (Directory.GetLastWriteTimeUtc(luaDir).Ticks, Directory.GetFiles(luaDir, "*.lua").Length);
        }
        catch { return (0, 0); }
    }

    /// <summary>appinfo.vdf 指纹（Steam 更新会改它）</summary>
    public static (long Size, long Ticks) AppInfoStamp(string steamPath)
    {
        try
        {
            if (string.IsNullOrEmpty(steamPath)) return (0, 0);
            var info = new FileInfo(Path.Combine(steamPath, "appinfo.vdf"));
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (0, 0);
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// 自检（<c>OSTGUI.exe --trainer-selftest</c> 会一起跑）：序列化往返 + 失效判定两条。
    /// 全部通过返回空串。
    /// </summary>
    public static string SelfCheck()
    {
        var snapshot = new Snapshot
        {
            SavedAt = new DateTime(2026, 9, 26, 1, 2, 3),
            LuaDirTicks = 111,
            LuaFileCount = 3,
            AppInfoSize = 222,
            AppInfoTicks = 333,
            LuaGames = { new Item { AppId = "1", GameName = "甲", SourceTag = "lua" } },
            OwnedGames = { new Item { AppId = "2", GameName = "乙", SourceTag = "正版" } },
        };

        var round = JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(snapshot, Options), Options);
        if (round == null) return "序列化往返返回空";
        if (round.LuaGames.Count != 1 || round.LuaGames[0].GameName != "甲") return "序列化往返丢了 lua 条目";
        if (round.OwnedGames.Count != 1 || round.OwnedGames[0].SourceTag != "正版") return "序列化往返丢了正版条目";

        // 指纹不匹配必须判为失效（拿一个不可能对上的指纹去过）
        if (IsFresh(snapshot, luaDir: "", steamPath: "")) return "指纹不一致却判成命中";
        return "";
    }
}
