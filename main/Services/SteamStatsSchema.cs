using System.Text;

namespace OSTGUI.Services;

/// <summary>成就定义（来自 Steam 本地 schema 缓存）</summary>
public sealed class AchievementDef
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Hidden { get; set; }
}

/// <summary>
/// 解析 Steam 本地成就 schema：&lt;Steam&gt;\appcache\stats\UserGameStatsSchema_&lt;appid&gt;.bin
/// 内容是 Valve 二进制 KeyValues（实测 343090.bin / 1901370.bin）：
///   顶层 = 子表（键为 appid）→ stats → 成就组（type == "ACHIEVEMENTS"）→ bits → 位索引 →
///   name / display.name.&lt;语言&gt; / token / hidden
/// 条目类型字节：0x00 子表开头 + 键名，0x01 字符串，0x02 int32，0x03 float，
///   0x04 指针，0x05 宽字符串，0x06 颜色，0x07 uint64，0x0A int64，0x08 本层结束。
/// 值一律紧跟键名（键名以 \0 结尾）。
/// </summary>
public static class SteamStatsSchema
{
    public sealed class Node
    {
        public string Name = "";
        public string? String;
        public int Int;
        public readonly List<Node> Children = new();

        public Node? Child(string name) =>
            Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static string PathFor(string steamPath, string appId) =>
        Path.Combine(steamPath, "appcache", "stats", $"UserGameStatsSchema_{appId}.bin");

    /// <summary>
    /// 读本地 schema。失败返回 false 并给出错误码（本项目自己的码，不是 Steam 的）：
    /// E1 文件不存在 / E2 解析失败 / E3 没有成就条目
    /// </summary>
    public static bool TryLoad(string steamPath, string appId, out List<AchievementDef> defs, out string error)
    {
        defs = new List<AchievementDef>();
        error = "";

        var path = PathFor(steamPath, appId);
        if (!File.Exists(path)) { error = "E1（schema 文件不存在）"; return false; }

        var root = ParseFile(path);
        var appNode = root?.Children.FirstOrDefault();
        var stats = appNode?.Child("stats");
        if (stats == null) { error = "E2（schema 解析失败）"; return false; }

        // 成就有可能在**多个组**里（2026-09-23 实测：无人深空 1/2 两组都是 ACHIEVEMENTS；
        // 深海迷航的 2 组是**空的**、真正的位在 5 组）。所以不能只认"第一个 ACHIEVEMENTS 组"——
        // 那样会把空组当答案，成就列表直接变空。这里扫所有组，凡是带具名 bits 的都收。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = 0;
        foreach (var group in stats.Children)
        {
            var bits = group.Child("bits");
            if (bits == null) continue;

            var used = false;
            foreach (var bit in bits.Children)
            {
                var name = bit.Child("name")?.String;
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;   // 无名位没法调 API；重名去重

                var display = bit.Child("display");
                defs.Add(new AchievementDef
                {
                    Name = name,
                    DisplayName = PickLanguage(display?.Child("name")) ?? name,
                    Description = PickLanguage(display?.Child("desc")) ?? bit.Child("description")?.String ?? "",
                    Hidden = bit.Child("hidden")?.Int == 1,
                });
                used = true;
            }
            if (used) groups++;
        }
        if (defs.Count > 4096) { defs = new List<AchievementDef>(); error = "E2（schema 解析失败）"; return false; }
        LogService.Diag($"schema {appId}: 成就 {defs.Count} 条（来自 {groups} 个 bits 组）");
        if (defs.Count == 0) { error = "E3（schema 里没有成就条目）"; return false; }
        return true;
    }

    /// <summary>中文优先，其次英文，再退第一个非空</summary>
    private static string? PickLanguage(Node? names)
    {
        if (names == null) return null;
        foreach (var lang in new[] { "schinese", "english" })
        {
            var v = names.Child(lang)?.String;
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return names.Children.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.String))?.String;
    }

    // ── 二进制 KeyValues ─────────────────────────────────────────────
    private static Node? ParseFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return ParseLevel(fs);
        }
        catch (Exception ex)
        {
            LogService.Diag($"schema 解析失败 {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>解析一层，遇到 0x08 或文件尾/未知类型即返回</summary>
    private static Node ParseLevel(Stream s)
    {
        var node = new Node();
        while (true)
        {
            int type = s.ReadByte();
            if (type < 0 || type == 0x08) break;

            var key = ReadCString(s);
            switch (type)
            {
                case 0x00:
                    var child = ParseLevel(s);
                    child.Name = key;
                    node.Children.Add(child);
                    break;
                case 0x01:
                    node.Children.Add(new Node { Name = key, String = ReadCString(s) });
                    break;
                case 0x02:
                    node.Children.Add(new Node { Name = key, Int = ReadInt32(s) });
                    break;
                case 0x03:
                case 0x04:
                case 0x06:
                    Skip(s, 4);
                    break;
                case 0x07:
                case 0x0A:
                    Skip(s, 8);
                    break;
                case 0x05:
                    while (true) { var lo = s.ReadByte(); var hi = s.ReadByte(); if (lo <= 0 && hi <= 0) break; }
                    break;
                default:
                    // 不认识的类型：停在本层，保留已解析到的内容（宁可少列也不抛）
                    return node;
            }
        }
        return node;
    }

    private static string ReadCString(Stream s)
    {
        var buf = new List<byte>(32);
        while (true)
        {
            int b = s.ReadByte();
            if (b <= 0) break;
            buf.Add((byte)b);
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    private static int ReadInt32(Stream s)
    {
        var b = new byte[4];
        if (s.Read(b, 0, 4) < 4) throw new EndOfStreamException();
        return BitConverter.ToInt32(b, 0);
    }

    private static void Skip(Stream s, int count)
    {
        for (int i = 0; i < count; i++)
            if (s.ReadByte() < 0) throw new EndOfStreamException();
    }
}
