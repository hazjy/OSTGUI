using SAM.API;
using SAM.API.Callbacks;
using System.Text.Json;

namespace OSTGUI.Services;

/// <summary>成就子进程的执行结果（父子两侧共用这一形状）</summary>
public sealed class StatsChildResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string SteamId { get; set; } = "";
    public bool StatsReady { get; set; }
    /// <summary>成功读到状态的成就条数；0 = 客户端里根本没有这个游戏的成就数据（不是"全部未解锁"）</summary>
    public int ReadOk { get; set; }
    public string Warning { get; set; } = "";
    public int Changed { get; set; }
    /// <summary>SetAchievement 返回失败的条数（&gt;0 表示没全部写进去）</summary>
    public int Failed { get; set; }
    public List<AchievementRecord> Achievements { get; set; } = new();
}

/// <summary>
/// 成就读写（子进程侧）：用 SAM（Steam Achievement Manager）那套现成的接口封装
/// （main/SteamApi/，zlib 许可，见 THIRD-PARTY-NOTICES）走 steamclient64.dll 的 ISteamUserStats013。
///
/// 必须在**独立短命子进程**里跑：`SteamAppId` 要在 steamclient 首次加载之前设定，且一个进程只能锁一个
/// appid；另外加载 steamclient 的进程会被 Steam 当成该 appid 的游戏进程（「停止游戏」会连坐）——
/// 与 Services/SteamTicketExtractor.cs 同一个理由。
///
/// ⚠️ 别在本文件里手写 vtable 索引：2026-09-23 就因为自己按"偏移"猜 ISteamUserStats013 的函数指针
/// 把子进程打成 0xC0000005（拿垃圾下标去索引数组）。能用 SAM 的封装就别碰原生。
/// </summary>
internal static class SteamStatsChild
{
    private const int WaitStatsMs = 15000;

    // ── 子进程入口：--stats-dump / --stats-apply / --stats-schema-dump ──────────
    public static int Run(string[] args)
    {
        var mode = args[1];
        var appId = args[2];
        var apply = mode.Equals("--stats-apply", StringComparison.OrdinalIgnoreCase);

        if (mode.Equals("--stats-schema-dump", StringComparison.OrdinalIgnoreCase))
        {
            var ok = SteamStatsSchema.TryLoad(args[3], appId, out var defs, out var error);
            var text = ok
                ? string.Join(Environment.NewLine,
                    defs.Select((d, i) => $"{i,3}  {(d.Hidden ? "[隐]" : "    ")}  {d.Name}  =  {d.DisplayName}"))
                : "未发现成就定义，错误码：" + error;
            File.WriteAllText(args[4], text);
            LogService.AddAppLog($"schema dump appid={appId} 条数={(ok ? defs.Count : -1)} {error}");
            return ok ? 0 : 2;
        }

        var outFile = apply ? args[5] : args[4];

        // 先占位：子进程要是在下面崩掉（原生越界是进程级死亡，catch 拦不住），
        // 父进程至少能拿到"未完成"而不是一句"无输出"。
        try
        {
            File.WriteAllText(outFile, JsonSerializer.Serialize(
                new StatsChildResult { Ok = false, Message = "子进程未完成（崩溃或被杀）" }));
        }
        catch { }

        StatsChildResult result;
        try
        {
            var changes = apply
                ? JsonSerializer.Deserialize<List<AchievementRecord>>(File.ReadAllText(args[4])) ?? new()
                : null;
            result = Execute(args[3], appId, changes);
        }
        catch (Exception ex)
        {
            result = new StatsChildResult { Ok = false, Message = ex.ToString() };
        }

        try
        {
            File.WriteAllText(outFile, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"成就子进程写结果失败: {ex.Message}");
        }
        LogService.AddAppLog($"stats {mode} appid={appId} ok={result.Ok} {result.Message} {result.Warning}");
        return result.Ok ? 0 : 1;
    }

    private static StatsChildResult Execute(string steamPath, string appId, List<AchievementRecord>? changes)
    {
        var res = new StatsChildResult();

        if (!SteamStatsSchema.TryLoad(steamPath, appId, out var defs, out var schemaError))
        {
            res.Message = "未发现成就定义，错误码：" + schemaError;
            return res;
        }
        LogService.AddAppLog($"stats[{appId}] begin defs={defs.Count} changes={changes?.Count.ToString() ?? "-"}");

        Steam.InstallPath = steamPath;
        using var client = new Client();
        try
        {
            // 内部顺序：设 SteamAppId → 加载 steamclient64.dll → CreateSteamPipe → ConnectToGlobalUser
            // → GetAppID 校验（对不上会抛 AppIdMismatch）
            client.Initialize(uint.Parse(appId));
        }
        catch (ClientInitializeException ex)
        {
            res.Message = $"连接 Steam 失败：{ex.Message}";
            return res;
        }
        LogService.AddAppLog($"stats[{appId}] connected");

        var received = false;
        var callback = client.CreateAndRegisterCallback<UserStatsReceived>();
        callback.OnRun += _ => received = true;

        var steamId = client.SteamUser.GetSteamId();
        res.SteamId = steamId.ToString();

        List<AchievementRecord> ReadAll()
        {
            var list = new List<AchievementRecord>(defs.Count);
            var ok = 0;
            foreach (var d in defs)
            {
                var rec = new AchievementRecord { Name = d.Name };
                if (client.SteamUserStats.GetAchievementAndUnlockTime(d.Name, out var achieved, out var unlockTime))
                {
                    rec.Achieved = achieved;
                    rec.UnlockTime = unlockTime;
                    ok++;
                }
                list.Add(rec);
            }
            res.ReadOk = ok;
            return list;
        }

        // 客户端里已有一份状态时别再 RequestUserStats：那会重拉一次（入库游戏的响应还被内核清空），
        // 等于把客户端里已有的状态（比如别的工具刚写进去的）抹平。
        if (changes == null)
        {
            var cached = ReadAll();
            if (cached.Any(r => r.Achieved))
            {
                res.Achievements = cached;
                res.Ok = true;
                LogService.AddAppLog($"stats[{appId}] read(cached) {cached.Count(r => r.Achieved)}/{cached.Count} unlocked");
                return res;
            }
        }

        if (client.SteamUserStats.RequestUserStats(steamId) == CallHandle.Invalid)
            Append(ref res, "RequestUserStats 失败");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!received && sw.ElapsedMilliseconds < WaitStatsMs)
        {
            client.RunCallbacks(false);
            Thread.Sleep(20);
        }
        res.StatsReady = received;
        if (!received) Append(ref res, "未收到 UserStatsReceived（成就状态可能仍是旧值）");

        if (changes != null)
        {
            var failed = 0;
            foreach (var c in changes)
                if (!client.SteamUserStats.SetAchievement(c.Name, c.Achieved)) failed++;

            res.Changed = changes.Count - failed;
            res.Failed = failed;
            if (!client.SteamUserStats.StoreStats())
                Append(ref res, "StoreStats 失败");
            LogService.AddAppLog($"stats[{appId}] applied={res.Changed}/{changes.Count} failed={failed}");
        }

        res.Achievements = ReadAll();
        LogService.AddAppLog(
            $"stats[{appId}] read {res.Achievements.Count(a => a.Achieved)}/{res.Achievements.Count} unlocked ready={res.StatsReady} ok={res.ReadOk}");
        res.Ok = true;
        return res;
    }

    private static void Append(ref StatsChildResult res, string note) =>
        res.Warning = string.IsNullOrEmpty(res.Warning) ? note : res.Warning + "；" + note;
}
