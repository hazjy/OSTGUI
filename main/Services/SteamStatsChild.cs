using System.Runtime.InteropServices;
using System.Text.Json;

namespace OSTGUI.Services;

/// <summary>成就子进程的执行结果（父子两侧共用这一形状）</summary>
public sealed class StatsChildResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string SteamId { get; set; } = "";
    public bool StatsReady { get; set; }
    public string Warning { get; set; } = "";
    public int Changed { get; set; }
    public List<AchievementRecord> Achievements { get; set; } = new();
}

/// <summary>
/// 成就读写（子进程侧）：加载 steamclient64.dll → 建 pipe → ISteamUserStats013。
///
/// 必须在**独立短命子进程**里跑：`SteamAppId` 要在 steamclient 首次加载之前设定，且一个进程只能锁一个
/// appid；另外加载 steamclient 的进程会被 Steam 当成该 appid 的游戏进程（「停止游戏」会连坐）——
/// 与 Services/SteamTicketExtractor.cs 同一个理由。
/// </summary>
internal static class SteamStatsChild
{
    // ISteamUserStats013 的 vtable 索引，顺序取自 SAM（Steam Achievement Manager）的接口声明
    // —— Fluent-Steam-Lua 用同一套在本机跑通过，直接照用。
    // ⚠️ 别再加"探测偏移"那类代码：探测只能靠调用别的函数，而 GetAchievementName(index) 之类拿到
    // 垃圾下标就会越界（2026-09-23 实测：正是那个探测把子进程打成 0xC0000005 = 访问违例）。
    // 布局对不对改由**行为**校验：RequestUserStats 之后 GetNumAchievements() 应等于 schema 条数。
    private const int IdxSetAchievement = 6;
    private const int IdxGetAchievementAndUnlockTime = 8;
    private const int IdxStoreStats = 9;
    private const int IdxGetNumAchievements = 13;
    private const int IdxRequestUserStats = 15;

    private const int IdxUtilsGetAppId = 9;    // ISteamUtils::GetAppID（与 SteamTicketExtractor 用的 11/13 同表）
    private const int IdxUserGetSteamId = 2;   // ISteamUser::GetSteamID
    private const int UserStatsReceived = 1101; // k_iSteamUserStatsCallbacks + 1
    private const int WaitStatsMs = 15000;

    // ── 子进程入口：--stats-dump / --stats-apply / --stats-schema-dump ──────────
    public static int Run(string[] args)
    {
        var mode = args[1];
        var appId = args[2];
        var apply = mode.Equals("--stats-apply", StringComparison.OrdinalIgnoreCase);

        if (mode.Equals("--stats-schema-dump", StringComparison.OrdinalIgnoreCase))
        {
            var defs = SteamStatsSchema.Load(args[3], appId);
            var text = defs == null
                ? "schema 缺失或无法解析：" + SteamStatsSchema.PathFor(args[3], appId)
                : string.Join(Environment.NewLine,
                    defs.Select((d, i) => $"{i,3}  {(d.Hidden ? "[隐]" : "    ")}  {d.Name}  =  {d.DisplayName}"));
            File.WriteAllText(args[4], text);
            LogService.AddAppLog($"schema dump appid={appId} 条数={defs?.Count ?? -1}");
            return defs == null ? 2 : 0;
        }

        var outFile = apply ? args[5] : args[4];

        // 先占位：子进程要是在下面崩掉（原生调用越界是进程级死亡，catch 拦不住），
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

        var defs = SteamStatsSchema.Load(steamPath, appId);
        if (defs == null)
        {
            res.Message = "未找到成就定义（缺少 schema 文件）：" + SteamStatsSchema.PathFor(steamPath, appId);
            return res;
        }
        LogService.AddAppLog($"stats[{appId}] begin defs={defs.Count} changes={changes?.Count.ToString() ?? "-"}");

        // 加载 steamclient 之前设定身份；退出前清掉（否则 Steam 长时间把本进程认成该游戏）
        Environment.SetEnvironmentVariable("SteamAppId", appId);
        Environment.SetEnvironmentVariable("SteamGameId", appId);

        IntPtr module = IntPtr.Zero, client = IntPtr.Zero;
        BReleaseSteamPipeFn? releasePipe = null;
        int pipe = 0;
        try
        {
            module = LoadSteamClient(steamPath);
            if (module == IntPtr.Zero) { res.Message = "加载 steamclient64.dll 失败"; return res; }

            var createInterfacePtr = GetProcAddress(module, "CreateInterface");
            var getCallbackPtr = GetProcAddress(module, "Steam_BGetCallback");
            var freeCallbackPtr = GetProcAddress(module, "Steam_FreeLastCallback");
            if (createInterfacePtr == IntPtr.Zero || getCallbackPtr == IntPtr.Zero || freeCallbackPtr == IntPtr.Zero)
            {
                res.Message = "steamclient64.dll 缺少所需导出（CreateInterface / Steam_BGetCallback / Steam_FreeLastCallback）";
                return res;
            }

            var createInterface = Marshal.GetDelegateForFunctionPointer<CreateInterfaceFn>(createInterfacePtr);
            client = createInterface("SteamClient018", IntPtr.Zero);
            if (client == IntPtr.Zero) { res.Message = "CreateInterface(SteamClient018) 失败"; return res; }

            var vt = Marshal.ReadIntPtr(client);
            var createPipe = GetDelegate<CreateSteamPipeFn>(vt, 0);
            releasePipe = GetDelegate<BReleaseSteamPipeFn>(vt, 1);
            var connectGlobal = GetDelegate<ConnectToGlobalUserFn>(vt, 2);
            var getUser = GetDelegate<GetISteamUserFn>(vt, 5);
            var getUtils = GetDelegate<GetISteamUtilsFn>(vt, 9);
            var getGeneric = GetDelegate<GetISteamGenericInterfaceFn>(vt, 12);
            var getCallback = Marshal.GetDelegateForFunctionPointer<BGetCallbackFn>(getCallbackPtr);
            var freeCallback = Marshal.GetDelegateForFunctionPointer<FreeLastCallbackFn>(freeCallbackPtr);

            pipe = createPipe(client);
            if (pipe == 0) { res.Message = "CreateSteamPipe 失败（Steam 没在运行？）"; return res; }

            var user = connectGlobal(client, pipe);
            if (user == 0) { res.Message = "ConnectToGlobalUser 失败（Steam 未登录？）"; return res; }
            LogService.AddAppLog($"stats[{appId}] pipe ok user={user}");

            // SteamAppId 是否真的生效（不致命，只记警告）
            var utils = CallWithAnsi("SteamUtils004", p => getUtils(client, pipe, p));
            if (utils != IntPtr.Zero)
            {
                var got = GetDelegate<GetAppIdFn>(Marshal.ReadIntPtr(utils), IdxUtilsGetAppId)(utils);
                if (got.ToString() != appId) res.Warning = $"SteamUtils.GetAppID={got}（请求 {appId}）";
            }

            var steamUser = CallWithAnsi("SteamUser012", p => getUser(client, user, pipe, p));
            var steamId = steamUser == IntPtr.Zero
                ? 0UL
                : GetDelegate<GetSteamIdFn>(Marshal.ReadIntPtr(steamUser), IdxUserGetSteamId)(steamUser);
            res.SteamId = steamId.ToString();
            LogService.AddAppLog($"stats[{appId}] identity steamId={steamId}");

            var stats = CallWithAnsi("STEAMUSERSTATS_INTERFACE_VERSION013", p => getGeneric(client, user, pipe, p));
            if (stats == IntPtr.Zero) { res.Message = "拿不到 ISteamUserStats013 接口"; return res; }
            LogService.AddAppLog($"stats[{appId}] iface ok");

            var statsVt = Marshal.ReadIntPtr(stats);
            var getAchieved = GetDelegate<GetAchievementAndUnlockTimeFn>(statsVt, IdxGetAchievementAndUnlockTime);

            List<AchievementRecord> ReadAll()
            {
                var list = new List<AchievementRecord>(defs.Count);
                foreach (var d in defs)
                {
                    var rec = new AchievementRecord { Name = d.Name };
                    var np = Marshal.StringToCoTaskMemAnsi(d.Name);
                    try
                    {
                        if (getAchieved(stats, np, out var achieved, out var unlockTime) != 0)
                        {
                            rec.Achieved = achieved != 0;
                            rec.UnlockTime = unlockTime;
                        }
                    }
                    finally { Marshal.FreeCoTaskMem(np); }
                    list.Add(rec);
                }
                return list;
            }

            // 先把客户端**当前缓存**读一遍：入库游戏的成就服务端本来就是空的，而 RequestUserStats 会重新拉
            // 一次（内核还会把 819 里的 stats 清掉）——那会把客户端里已有的状态（比如别处工具刚写的）抹平。
            // 所以只有"一条已解锁都没有"才去请求。
            var needsRequest = changes != null;
            if (!needsRequest)
            {
                var cached = ReadAll();
                if (cached.Any(r => r.Achieved))
                {
                    res.Achievements = cached;
                    LogService.AddAppLog($"stats[{appId}] read(cached) {cached.Count(r => r.Achieved)}/{cached.Count} unlocked");
                    res.Ok = true;
                    return res;
                }
                needsRequest = true;
            }

            if (needsRequest && steamId != 0)
            {
                var request = GetDelegate<RequestUserStatsFn>(statsVt, IdxRequestUserStats);
                if (request(stats, steamId) == 0) Append(ref res, "请求成就数据失败");
            }

            res.StatsReady = PumpCallbacks(pipe, getCallback, freeCallback);
            if (!res.StatsReady) Append(ref res, "未收到 UserStatsReceived（成就状态可能仍是旧值）");

            // 行为校验：接口布局对的话，客户端报的成就条数应与本地 schema 一致
            var clientCount = GetDelegate<GetCountFn>(statsVt, IdxGetNumAchievements)(stats);
            LogService.AddAppLog($"stats[{appId}] count={clientCount} schema={defs.Count} ready={res.StatsReady}");
            if (clientCount != defs.Count)
                Append(ref res, $"客户端成就数 {clientCount} 与 schema {defs.Count} 不一致（接口布局可能不匹配）");

            if (changes != null)
            {
                var setAchievement = GetDelegate<SetAchievementFn>(statsVt, IdxSetAchievement);
                var store = GetDelegate<StoreStatsFn>(statsVt, IdxStoreStats);

                var failed = 0;
                foreach (var c in changes)
                {
                    var np = Marshal.StringToCoTaskMemAnsi(c.Name);
                    try { if (setAchievement(stats, np) == 0) failed++; }
                    finally { Marshal.FreeCoTaskMem(np); }
                }
                res.Changed = changes.Count - failed;
                if (store(stats) == 0) Append(ref res, "StoreStats 失败（未拥有的游戏服务端不认，属预期）");
                if (failed > 0) res.Message = $"{failed} 项设置失败";
                LogService.AddAppLog($"stats[{appId}] applied={res.Changed}/{changes.Count} failed={failed}");
            }

            res.Achievements = ReadAll();
            LogService.AddAppLog($"stats[{appId}] read {res.Achievements.Count(a => a.Achieved)}/{res.Achievements.Count} unlocked");

            res.Ok = true;
            return res;
        }
        finally
        {
            if (releasePipe != null && client != IntPtr.Zero && pipe != 0) releasePipe(client, pipe);
            if (module != IntPtr.Zero) FreeLibrary(module);
            Environment.SetEnvironmentVariable("SteamAppId", null);
            Environment.SetEnvironmentVariable("SteamGameId", null);
        }
    }

    /// <summary>把回调队列抽干，等到 UserStatsReceived（成就数据到位）为止</summary>
    private static bool PumpCallbacks(int pipe, BGetCallbackFn get, FreeLastCallbackFn free)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < WaitStatsMs)
        {
            var got = false;
            var any = false;
            while (get(pipe, out var msg, out _) != 0)
            {
                any = true;
                if (msg.Id == UserStatsReceived) got = true;
                free(pipe);
            }
            if (got) return true;
            Thread.Sleep(any ? 0 : 30);
        }
        return false;
    }

    private static void Append(ref StatsChildResult res, string note) =>
        res.Warning = string.IsNullOrEmpty(res.Warning) ? note : res.Warning + "；" + note;

    private static IntPtr GetProcAddress(IntPtr module, string name) => NativeGetProcAddress(module, name);

    private static T GetDelegate<T>(IntPtr vtable, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, index * IntPtr.Size));

    private static IntPtr CallWithAnsi(string text, Func<IntPtr, IntPtr> call)
    {
        var p = Marshal.StringToCoTaskMemAnsi(text);
        try { return call(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    private static IntPtr LoadSteamClient(string steamPath)
    {
        SetDllDirectory(steamPath + ";" + Path.Combine(steamPath, "bin"));
        foreach (var candidate in new[]
                 {
                     Path.Combine(steamPath, "steamclient64.dll"),
                     Path.Combine(steamPath, "bin", "steamclient64.dll"),
                 })
        {
            if (!File.Exists(candidate)) continue;
            var m = LoadLibraryEx(candidate, IntPtr.Zero, 0x8 /* LoadWithAlteredSearchPath */);
            if (m != IntPtr.Zero) return m;
        }
        return IntPtr.Zero;
    }

    // ── native ──────────────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcAddress")]
    private static extern IntPtr NativeGetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll")]
    private static extern int FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int SetDllDirectory(string path);

    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackMsg
    {
        public int User;
        public int Id;
        public IntPtr ParamPointer;
        public int ParamSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateInterfaceFn(string name, IntPtr returnCode);

    private delegate int CreateSteamPipeFn(IntPtr self);
    private delegate int BReleaseSteamPipeFn(IntPtr self, int pipe);
    private delegate int ConnectToGlobalUserFn(IntPtr self, int pipe);
    private delegate IntPtr GetISteamUtilsFn(IntPtr self, int pipe, IntPtr version);
    private delegate IntPtr GetISteamUserFn(IntPtr self, int user, int pipe, IntPtr version);
    private delegate IntPtr GetISteamGenericInterfaceFn(IntPtr self, int user, int pipe, IntPtr version);
    private delegate uint GetAppIdFn(IntPtr self);
    private delegate ulong GetSteamIdFn(IntPtr self);
    private delegate uint GetCountFn(IntPtr self);
    private delegate int RequestUserStatsFn(IntPtr self, ulong steamId);
    private delegate int GetAchievementAndUnlockTimeFn(IntPtr self, IntPtr name, out byte achieved, out uint unlockTime);
    private delegate int SetAchievementFn(IntPtr self, IntPtr name);
    private delegate int StoreStatsFn(IntPtr self);
    private delegate int BGetCallbackFn(int pipe, out CallbackMsg msg, out int call);
    private delegate int FreeLastCallbackFn(int pipe);
}
