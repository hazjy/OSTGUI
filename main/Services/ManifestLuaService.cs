using System.Text;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 生成/写入 <c>&lt;lua 目录&gt;\manifest.lua</c>——内核侧「请求码源级联」的唯一落地形态。
///
/// <para><b>为什么走 manifest.lua 而不是内核配置</b>：内核的取码优先级是
/// <c>fetch_manifest_code_ex</c> → <c>fetch_manifest_code</c> → 内置 provider 表，而 Lua 层
/// 天然支持"多源级联 + 每源自定义 UA"（<c>http_get(url, headers)</c>），内置表只能选一个源且
/// 没有 UA 字段。走这条路不需要改内核、不需要用户换 DLL。</para>
///
/// <para><b>三条硬约束（改动前先读）</b>：</para>
/// <list type="number">
/// <item>内核只在 <c>app_id</c> 与 <c>depot_id</c> 都非 0 时才调 <c>fetch_manifest_code_ex</c>，
/// 所以两个函数都要定义：<c>_ex</c> 走完整级联（含需要 depot 的源），<c>fetch_manifest_code</c>
/// 只带 gid、自动跳过需要 depot 的源。</item>
/// <item>返回 <b>nil</b> = 这家不行（内核继续回落内置源）；返回 <b>"0"</b> 也算"取到了码"，
/// 会挡住后面所有回落——短路版正是靠这一点阻断第三方码注入，别在级联里误返回它。</item>
/// <item>内核只等 30 秒（<c>kMaxWaitSeconds</c>），而 Lua 侧没法给 <c>http_get</c> 传超时，
/// 所以生成的脚本自带 <see cref="BudgetSeconds"/> 秒预算，超了就不再试后面的源。</item>
/// </list>
/// </summary>
public class ManifestLuaService
{
    /// <summary>我们生成的文件的身份标记（第一行开头）</summary>
    public const string ManagedMarker = "-- OSTGUI-managed";

    private const string FileName = "manifest.lua";

    /// <summary>级联预算（秒）。内核等 30 秒，这里留足余量</summary>
    public const int BudgetSeconds = 20;

    /// <summary>「测活」用的真实配对（481 = Spacewar）。⚠️ 不能拿编造的 gid 探活：
    /// manifestdex 对不存在的 gid 也回 200 + 一个数字，会假阳性。</summary>
    public const string ProbeDepotId = "481";

    /// <inheritdoc cref="ProbeDepotId"/>
    public const string ProbeGid = "3183503801510301321";

    private readonly SteamService _steamService;
    private readonly HttpClient _httpClient;

    public ManifestLuaService(SteamService steamService, HttpClient httpClient)
    {
        _steamService = steamService;
        _httpClient = httpClient;
    }

    public enum WriteOutcome
    {
        /// <summary>已写入（内容有变，覆盖了原文件）</summary>
        Written,
        /// <summary>内容与现有文件一致，没动它</summary>
        Unchanged,
        /// <summary>没有 Steam 路径，无法定位 lua 目录</summary>
        NoLuaDir,
        /// <summary>写盘失败</summary>
        Failed
    }

    /// <summary>manifest.lua 的完整路径（不创建目录）；Steam 路径未知时为 null</summary>
    public string? GetLuaFilePath()
    {
        var dir = _steamService.GetEffectiveLuaDir();
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, FileName);
    }

    /// <summary>只读检查状态（进设置页时用，<b>不写盘</b>）：<c>manifest.lua：&lt;启用的源 / 全部禁用&gt;</c></summary>
    public string Inspect(IEnumerable<RequestCodeSource> sources)
    {
        if (GetLuaFilePath() == null) return $"{FileName}：未检测到 Steam 路径";

        return Status(Ordered(sources));
    }

    /// <summary>按当前启用集合同步 manifest.lua（勾选变化即写）</summary>
    public (WriteOutcome outcome, string message) Sync(IEnumerable<RequestCodeSource> sources)
    {
        var path = GetLuaFilePath();
        if (path == null) return (WriteOutcome.NoLuaDir, $"{FileName}：未检测到 Steam 路径");

        return SyncTo(path, Ordered(sources));
    }

    /// <summary>
    /// 渲染并写到指定路径（从 <see cref="Sync"/> 拆出来，纯粹是为了让自检能直接跑文件语义——
    /// 覆盖/幂等这段是唯一可能毁用户文件的地方，必须真跑过）。直接覆盖，不留备份。
    /// </summary>
    public static (WriteOutcome outcome, string message) SyncTo(
        string path, IReadOnlyList<RequestCodeSource> sources)
    {
        var enabled = Ordered(sources);
        var lua = BuildLua(enabled);

        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            if (existing == lua) return (WriteOutcome.Unchanged, Status(enabled));

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, lua, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);

            return (WriteOutcome.Written, Status(enabled));
        }
        catch (Exception ex)
        {
            return (WriteOutcome.Failed, $"写入 {Path.GetFileName(path)} 失败：{ex.Message}");
        }
    }

    /// <summary>启用中的源，按级联顺序</summary>
    private static List<RequestCodeSource> Ordered(IEnumerable<RequestCodeSource> sources) =>
        sources.Where(s => s.IsEnabled).OrderBy(s => s.Priority).ToList();

    /// <summary>状态行：`manifest.lua：<源 1 → 源 2 …>` / `manifest.lua：全部禁用`</summary>
    private static string Status(List<RequestCodeSource> enabled) =>
        $"{FileName}：" + (enabled.Count == 0
            ? "全部禁用"
            : string.Join(" → ", enabled.Select(s => s.Name)));

    /// <summary>
    /// 渲染 manifest.lua 全文。纯函数（同输入同输出，别塞时间戳——靠内容比较决定是否重写）。
    /// 全部关闭时生成短路版。
    /// </summary>
    public static string BuildLua(IReadOnlyList<RequestCodeSource> enabledSources)
    {
        var sb = new StringBuilder();

        sb.Append(ManagedMarker).Append(" manifest.lua —— 由 OSTGUI 设置页「请求码源」生成，手改会被下一次保存整份覆盖。\n");

        if (enabledSources.Count == 0)
        {
            sb.Append("-- 请求码源全部关闭 → 短路版：恒返回 \"0\"。内核把 \"0\" 当作“已取到码”，\n");
            sb.Append("-- 因此不会回落到 opensteamtool.toml 的 [manifest] url，以此阻断第三方码注入；\n");
            sb.Append("-- 清单由 OSTGUI 的「清单按需投喂」写进 depotcache（请求码不是必需品）。\n\n");
            sb.Append("function fetch_manifest_code(gid) return \"0\" end\n");
            sb.Append("function fetch_manifest_code_ex(app_id, depot_id, gid) return \"0\" end\n");
            return sb.ToString();
        }

        sb.Append("-- 级联顺序：").Append(string.Join(" → ", enabledSources.Select(s => s.Name))).Append('\n');
        sb.Append("-- 逐源请求，第一个返回合法十进制码即用；全部失败返回 nil（内核再回落 [manifest] url）。\n");
        sb.Append("-- 预算 ").Append(BudgetSeconds).Append(" 秒：内核只等 30 秒，超预算就不再试后面的源。\n");
        sb.Append("-- ⚠️ 本文件里**所有顶层名字都必须是全局**：内核的 lua 加载器是「逐行累积、一能编译就\n");
        sb.Append("--    立刻执行、执行完把 chunk 连作用域一起丢掉」（LuaConfig.cpp:829-856），文件级的\n");
        sb.Append("--    local 在钩子被调用时早已消失 → 会变成 nil 全局 → 一调用即报错、一次网络都不发。\n");
        sb.Append("--    （2026-10-06 真踩过：attempt to call a nil value (global 'cascade')）\n\n");

        sb.Append("OSTGUI_SOURCES = {\n");
        foreach (var s in enabledSources)
        {
            sb.Append("    { name = ").Append(LuaString(s.Name))
              .Append(", url = ").Append(LuaString(s.UrlTemplate))
              .Append(", ua = ").Append(s.UserAgent.Length == 0 ? "nil" : LuaString(s.UserAgent))
              .Append(", needs_depot = ").Append(s.NeedsDepotId ? "true" : "false")
              .Append(", json = ").Append(s.JsonResponse ? "true" : "false")
              .Append(" },\n");
        }
        sb.Append("}\n");

        sb.Append(Harness);
        // 统一成 LF：源码文件的换行风格若被改成 CRLF，原始字符串会跟着变，
        // 那会让"内容比较决定是否重写"每次都判定为不同（凭空多写一次盘）
        return sb.ToString().Replace("\r\n", "\n");
    }

    /// <summary>
    /// 固定部分（不含具体源表）：渲染 URL → 逐个试 → 解析码。
    ///
    /// <para><b>为什么通篇用全局、且带 <c>ostgui_</c> 前缀</b>：内核的 lua 加载器（LuaConfig.cpp:829-856）
    /// 逐行累积文本、一能编译成完整 chunk 就立刻执行、然后丢弃该 chunk 的作用域——文件级 <c>local</c>
    /// 到钩子被调用时已经不存在了。所以辅助函数与源表必须是全局（前缀避免和各 <c>&lt;AppId&gt;.lua</c>
    /// 抢名字），函数体内的 <c>local</c> 则不受影响。</para>
    /// </summary>
    private const string Harness =
        """

        OSTGUI_BUDGET_SECONDS = 20
        OSTGUI_HAS_CLOCK = (type(os) == "table" and type(os.time) == "function")

        function ostgui_now()
            if OSTGUI_HAS_CLOCK then return os.time() end
            return 0
        end

        function ostgui_render(tpl, gid, depot_id)
            local url = tpl:gsub("{gid}", tostring(gid))
            if depot_id and depot_id ~= 0 then
                url = url:gsub("{depotid}", tostring(depot_id))
            end
            return url
        end

        function ostgui_pick_code(body, is_json)
            if type(body) ~= "string" then return nil end
            if is_json then return body:match('"content"%s*:%s*"(%d+)"') end
            return body:match("^%s*(%d+)")
        end

        function ostgui_cascade(gid, depot_id)
            if gid == nil or gid == 0 then return nil end
            local started = ostgui_now()
            for i = 1, #OSTGUI_SOURCES do
                local s = OSTGUI_SOURCES[i]
                if (not s.needs_depot) or (depot_id and depot_id ~= 0) then
                    if OSTGUI_HAS_CLOCK and (ostgui_now() - started) >= OSTGUI_BUDGET_SECONDS then break end
                    local hdrs = nil
                    if s.ua then hdrs = { ["User-Agent"] = s.ua } end
                    local body, st = http_get(ostgui_render(s.url, gid, depot_id), hdrs)
                    if st == 200 then
                        local code = ostgui_pick_code(body, s.json)
                        -- 0 不是有效请求码（CDN 只会回 401）。这里必须滤掉：内核把 "0" 当作
                        -- “已取到码”，一旦返回它就再也不会回落，等于把后面所有源全废掉。
                        if code and tonumber(code) ~= 0 then return code end
                    end
                end
            end
            return nil
        end

        function fetch_manifest_code_ex(app_id, depot_id, gid)
            return ostgui_cascade(gid, depot_id)
        end

        function fetch_manifest_code(gid)
            return ostgui_cascade(gid, nil)
        end
        """;

    /// <summary>转义成 Lua 字符串字面量（我们的模板里没有引号，但别赌）</summary>
    private static string LuaString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            if (ch is '"' or '\\') sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// 把预置源的渲染结果导出成文件（<c>OSTGUI.exe --dump-manifest-lua [路径]</c>，开发/核对用）。
    /// 存在的理由：内核的 lua 加载方式（逐行累积成 chunk、执行完丢作用域，见 <see cref="Harness"/> 注释）
    /// 与标准 Lua 不同，只有把**真实产物**交给**同样的加载方式**才能试出问题。
    /// </summary>
    public static string DumpPresetLua(string path)
    {
        var enabled = RequestCodeSource.GetPresetSources()
            .Where(s => s.IsEnabled).OrderBy(s => s.Priority).ToList();
        File.WriteAllText(path, BuildLua(enabled), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// 单源测活：拿一组**实测可用**的 depot+gid 去问这家源，200 + 能解析出码才算可用。
    /// 这是本功能唯一的诊断窗口：manifest.lua 里出错是静默的（Lua 侧没有写进 manifest.log 的通道），
    /// 内核日志只会留下一句 "returned nil"。
    /// </summary>
    public async Task<(bool ok, string text)> ProbeAsync(RequestCodeSource source, CancellationToken ct = default)
    {
        var url = source.UrlTemplate
            .Replace(RequestCodeSource.PlaceholderGid, ProbeGid)
            .Replace(RequestCodeSource.PlaceholderDepotId, ProbeDepotId);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(source.UserAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", source.UserAgent);

            using var resp = await _httpClient.SendAsync(req, timeout.Token);
            var body = await resp.Content.ReadAsStringAsync(timeout.Token);
            if ((int)resp.StatusCode != 200) return (false, $"不可用（HTTP {(int)resp.StatusCode}）");

            var code = source.JsonResponse ? ExtractJsonCode(body) : ExtractPlainCode(body);
            return code == null ? (false, "不可用（200 但没解析出码）") : (true, $"可用（{code}）");
        }
        catch (Exception ex)
        {
            return (false, $"不可用（{ex.GetType().Name}）");
        }
    }

    private static string? ExtractPlainCode(string body)
    {
        var span = body.AsSpan().TrimStart();
        var i = 0;
        while (i < span.Length && char.IsAsciiDigit(span[i])) i++;
        return i == 0 ? null : span[..i].ToString();
    }

    private static string? ExtractJsonCode(string body)
    {
        var at = body.IndexOf("\"content\"", StringComparison.Ordinal);
        if (at < 0) return null;
        var q1 = body.IndexOf('"', at + 9);
        if (q1 < 0) return null;
        var q2 = body.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        var value = body[(q1 + 1)..q2];
        return value.Length > 0 && value.All(char.IsAsciiDigit) ? value : null;
    }

    /// <summary>
    /// 自检（<c>OSTGUI.exe --trainer-selftest</c> 一并跑）：渲染产物里那些"错了会静默失效"的点
    /// （两个钩子都在、标记在、UA 在、顺序在、全关变短路版），外加写盘/覆盖/备份那段文件语义。
    /// 返回空串 = 全过。
    /// </summary>
    public static string SelfCheck()
    {
        var presets = RequestCodeSource.GetPresetSources();

        var all = BuildLua(presets);
        if (!all.StartsWith(ManagedMarker, StringComparison.Ordinal)) return "manifest.lua：缺 managed 标记";
        if (!all.Contains("function fetch_manifest_code_ex(app_id, depot_id, gid)")) return "manifest.lua：缺 fetch_manifest_code_ex";
        if (!all.Contains("function fetch_manifest_code(gid)")) return "manifest.lua：缺 fetch_manifest_code";
        if (!all.Contains("needs_depot = true")) return "manifest.lua：需要 depot 的源没标 needs_depot";
        if (!all.Contains("tonumber(code) ~= 0")) return "manifest.lua：没有滤掉无效的 0 码";
        if (!all.Contains("ManifestDeX/1.0")) return "manifest.lua：manifestdex 的 UA 没写进去";
        if (all.IndexOf("20770407", StringComparison.Ordinal) > all.IndexOf("steamrun", StringComparison.Ordinal))
            return "manifest.lua：级联顺序与预置顺序不一致";

        var off = BuildLua(Array.Empty<RequestCodeSource>());
        if (!off.Contains("return \"0\"")) return "manifest.lua：全关时不是短路版";
        if (off.Contains("http_get")) return "manifest.lua：短路版不该带 http_get";

        var one = BuildLua(presets.Where(s => s.Id == "20770407").ToList());
        if (one.Contains("steamrun")) return "manifest.lua：只启用一个源时带进了别的源";

        // 脚本里的预算常量必须跟 C# 侧一致（两处各写一份，改一处忘了另一处就白写）
        if (!all.Contains($"OSTGUI_BUDGET_SECONDS = {BudgetSeconds}")) return "manifest.lua：预算常量与 C# 侧不一致";

        // ⚠️ 文件级 local 在内核的逐行加载器下必失效（见 Harness 注释）——生成物里一个都不许有。
        // 这条是 2026-10-06 那次"请求码源完全不生效"的直接教训：真机日志报
        // attempt to call a nil value (global 'cascade')。
        foreach (var line in all.Split('\n'))
        {
            if (line.StartsWith("local ", StringComparison.Ordinal))
                return $"manifest.lua：出现文件级 local（内核加载器下会变成 nil 全局）：{line.Trim()}";
        }
        if (!all.Contains("return ostgui_cascade(gid, depot_id)")) return "manifest.lua：_ex 没走全局级联函数";
        if (!all.Contains("OSTGUI_SOURCES = {")) return "manifest.lua：源表不是全局";

        return FileSelfCheck();
    }

    /// <summary>
    /// 文件语义自检：在临时目录里真跑一遍"首次写入 / 幂等 / 覆盖外来文件 / 全关短路 / 不留 .tmp"。
    /// 覆盖边界这段一旦错就是毁用户文件，不能只靠读代码。
    /// </summary>
    private static string FileSelfCheck()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ostgui-manifest-selftest-" + Environment.ProcessId);
        var path = Path.Combine(dir, FileName);
        var presets = RequestCodeSource.GetPresetSources();

        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            // 1) 首次写入
            var r1 = SyncTo(path, presets);
            if (r1.outcome != WriteOutcome.Written) return $"manifest.lua 自检：首次写入应 Written，实为 {r1.outcome}";
            var written = File.ReadAllText(path);
            if (!written.StartsWith(ManagedMarker, StringComparison.Ordinal)) return "manifest.lua 自检：写出的文件没有标记";

            // 2) 幂等：内容一样不该重写
            var stamp = File.GetLastWriteTimeUtc(path);
            Thread.Sleep(20);
            var r2 = SyncTo(path, presets);
            if (r2.outcome != WriteOutcome.Unchanged) return $"manifest.lua 自检：重复写入应 Unchanged，实为 {r2.outcome}";
            if (File.GetLastWriteTimeUtc(path) != stamp) return "manifest.lua 自检：Unchanged 却动了文件";

            // 3) 外来文件（1.4.0 时代用户手放的短路版）：直接覆盖
            File.WriteAllText(path, "function fetch_manifest_code(gid) return \"0\" end\n");
            var r3 = SyncTo(path, presets);
            if (r3.outcome != WriteOutcome.Written) return $"manifest.lua 自检：外来文件应直接 Written，实为 {r3.outcome}";
            if (!File.ReadAllText(path).Contains("fetch_manifest_code_ex")) return "manifest.lua 自检：覆盖后不是级联版";

            // 4) 全部关闭 → 短路版
            var r4 = SyncTo(path, Array.Empty<RequestCodeSource>());
            if (r4.outcome != WriteOutcome.Written) return $"manifest.lua 自检：全关应 Written，实为 {r4.outcome}";
            var off = File.ReadAllText(path);
            if (!off.Contains("return \"0\"") || off.Contains("http_get")) return "manifest.lua 自检：全关没写成短路版";

            // 5) 临时文件必须被移走，不留 .tmp
            if (File.Exists(path + ".tmp")) return "manifest.lua 自检：留下了 .tmp";

            return "";
        }
        catch (Exception ex)
        {
            return $"manifest.lua 自检异常：{ex.Message}";
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
