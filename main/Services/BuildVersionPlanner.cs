using System.Text;
using System.Text.RegularExpressions;

namespace OSTGUI.Services;

/// <summary>版本计划结果：新 lua 正文 + "在该 build 里没有清单、因此保持自动更新"的 depot。</summary>
public readonly record struct BuildPlan(string NewLua, IReadOnlyList<string> MissingDepots);

/// <summary>
/// 把"切到某个 build"翻译成 lua 文本改动（纯逻辑，可自检）。
///
/// 约定：固定版本配置只由**一段注释块 + 若干 setManifestid 行**表示，
///   · 保持最新（auto）：行全部注释 <c>--setManifestid(depot, "gid")</c>，块头写"预写备用，未启用"；
///   · 锁定某个 build（fixed）：同样的行**不注释**，块头写"已启用，锁定 build"。
/// 所以切换 build = 丢掉所有旧行、按新 build 的 depot→gid 重发一次。这样天然幂等，
/// 也不会因为"只改一部分行"留下半新半旧的残影。
/// </summary>
public static class BuildVersionPlanner
{
    /// <summary>匹配 setManifestid 行（注释与否都算），用于整段重写。</summary>
    private static readonly Regex SmidLine = new(
        @"^\s*(?:--+\s*)?setmanifestid\s*\(\s*(\d+)\s*,",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>匹配"固定版本配置"块头（旧版有"（预写备用，未启用）"等后缀）。</summary>
    private static readonly Regex BlockHeader = new(
        @"^\s*--+\s*固定版本配置",
        RegexOptions.Compiled);

    private const string HeaderAuto = "-- 固定版本配置（预写备用，未启用）";
    private const string HeaderFixed = "-- 固定版本配置（已启用，锁定 build）";

    /// <summary>
    /// 生成新 lua 文本。
    /// </summary>
    /// <param name="lua">当前 lua 全文（CRLF/LF 均可，输出沿用它的换行与结尾换行）。</param>
    /// <param name="keepLatest">true = 保持最新（注释掉）；false = 锁定到 <paramref name="gids"/> 描述的 build。</param>
    /// <param name="gids">该 build 下 depot → gid；不在里面的 depot 视为"这个 build 没有它的清单"。</param>
    /// <param name="luaDepots">lua 里声明过的 depot（不含主 AppID 与 DLC 段）。</param>
    public static BuildPlan Plan(string lua, bool keepLatest,
        IReadOnlyDictionary<string, string> gids, IReadOnlyCollection<string> luaDepots)
    {
        lua ??= string.Empty;

        var newLine = lua.Contains("\r\n") ? "\r\n" : "\n";
        var endsWithNewLine = lua.EndsWith("\n", StringComparison.Ordinal);
        var lines = lua.Replace("\r\n", "\n").Split('\n');

        // 1) 丢掉旧的所有 setManifestid 行与块头（注释与否都丢），稍后按新状态重发
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (SmidLine.IsMatch(line)) continue;
            if (BlockHeader.IsMatch(line)) continue;
            kept.Add(line);
        }

        // 去掉尾部空行（重发块之后再按需要补一个结尾换行）
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);

        // 2) 按 depot 数字序重发
        var ordered = luaDepots
            .Where(d => !string.IsNullOrEmpty(d) && d.All(char.IsDigit))
            .Distinct()
            .OrderBy(d => long.TryParse(d, out var v) ? v : long.MaxValue)
            .ToList();

        var emitted = new List<string>();
        var missing = new List<string>();
        foreach (var depot in ordered)
        {
            if (gids is not null && gids.TryGetValue(depot, out var gid) && !string.IsNullOrWhiteSpace(gid))
                emitted.Add($"setManifestid({depot}, \"{gid}\")");
            else
                missing.Add(depot);
        }

        var sb = new StringBuilder();
        sb.Append(string.Join(newLine, kept));

        if (emitted.Count > 0)
        {
            if (sb.Length > 0) sb.Append(newLine);
            sb.Append(keepLatest ? HeaderAuto : HeaderFixed);
            foreach (var line in emitted)
            {
                sb.Append(newLine);
                sb.Append(keepLatest ? "--" + line : line);
            }
        }

        if (endsWithNewLine) sb.Append(newLine);

        return new BuildPlan(sb.ToString(), missing);
    }

    /// <summary>读当前 lua 里**未注释**的 setManifestid（= 当前实际锁定到的 depot→gid）。</summary>
    public static Dictionary<string, string> ParseActiveGids(string lua)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(lua)) return result;

        foreach (var line in lua.Replace("\r\n", "\n").Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line,
                @"^\s*setmanifestid\s*\(\s*(\d+)\s*,\s*""(\d+)""",
                RegexOptions.IgnoreCase);
            if (m.Success) result[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return result;
    }

    /// <summary>自检：两种状态互转、换 build、缺 depot 不写、幂等、不误伤 addappid/addtoken。</summary>
    public static string SelfCheck()
    {
        const string baseLua =
            "addappid(730)\n" +
            "addappid(731, 1, \"aa\")\n" +
            "addappid(732, 1, \"bb\")\n" +
            "--setManifestid(731, \"111\")\n" +
            "--setManifestid(732, \"222\")\n";

        var gidsA = new Dictionary<string, string> { ["731"] = "1001", ["732"] = "1002" };
        var depots = new List<string> { "731", "732" };

        // 1) 锁定：两行都变未注释、gid 换成新的
        var fixedPlan = Plan(baseLua, keepLatest: false, gidsA, depots);
        if (!fixedPlan.NewLua.Contains("\nsetManifestid(731, \"1001\")")) return "锁定后 731 行不正确";
        if (!fixedPlan.NewLua.Contains("\nsetManifestid(732, \"1002\")")) return "锁定后 732 行不正确";
        if (fixedPlan.NewLua.Contains("--setManifestid")) return "锁定后仍留有注释形式的旧值";
        if (fixedPlan.MissingDepots.Count != 0) return "锁定（全覆盖）不该有缺失 depot";

        // 2) 回到自动：行全部注释，且不再有未注释行
        var autoPlan = Plan(fixedPlan.NewLua, keepLatest: true, gidsA, depots);
        if (autoPlan.NewLua.Contains("\nsetManifestid(")) return "回到自动后仍有未注释行";
        if (!autoPlan.NewLua.Contains("--setManifestid(731, \"1001\")")) return "回到自动后没保留注释备用值";

        // 3) 换 build：旧 gid 必须被彻底替换（不留残影）
        var gidsB = new Dictionary<string, string> { ["731"] = "2001", ["732"] = "2002" };
        var switchPlan = Plan(fixedPlan.NewLua, keepLatest: false, gidsB, depots);
        if (switchPlan.NewLua.Contains("1001") || switchPlan.NewLua.Contains("1002")) return "换 build 后残留旧 gid";
        if (!switchPlan.NewLua.Contains("setManifestid(731, \"2001\")")) return "换 build 后新 gid 未写入";

        // 4) 该 build 缺某个 depot：不写它、并报出来
        var partial = Plan(fixedPlan.NewLua, keepLatest: false,
            new Dictionary<string, string> { ["731"] = "3001" }, depots);
        if (!partial.MissingDepots.Contains("732")) return "缺失 depot 未被报告";
        if (partial.NewLua.Contains("setManifestid(732,")) return "缺失 depot 却被写入了";

        // 5) 幂等：同一计划跑两次结果一致
        var once = Plan(baseLua, false, gidsA, depots).NewLua;
        var twice = Plan(once, false, gidsA, depots).NewLua;
        if (once != twice) return "计划不幂等";

        // 6) 不误伤 addappid / addtoken
        var withToken = "addtoken(730, \"t\")\n" + baseLua;
        var afterToken = Plan(withToken, false, gidsA, depots).NewLua;
        if (!afterToken.Contains("addtoken(730, \"t\")")) return "addtoken 行被误改";
        if (afterToken.Count(c => c == 'a') < 3) return "addappid 行疑似被吞";

        // 7) 读当前锁定集合：只认未注释的行
        var active = ParseActiveGids(fixedPlan.NewLua);
        if (active.Count != 2) return $"未注释行解析数量错误：{active.Count}";
        if (!active.TryGetValue("731", out var g731) || g731 != "1001") return "未注释行解析出的 gid 错误";
        if (ParseActiveGids(autoPlan.NewLua).Count != 0) return "注释行被误判成未注释";

        return "";
    }
}
