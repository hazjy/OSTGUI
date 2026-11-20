// 宿主 480 启动器。
//
// 用法: OnlineHost.exe "<游戏 exe 路径>" <会话 AppID>
//
// 做法（实测验证过的路线）：设 SteamAppId/SteamGameId 环境变量 → 宿主自己也以该身份
// 初始化一次 Steam（让 Steam 先把该 AppID 记为"正在运行"）→ 把游戏作为子进程拉起。
// 游戏继承环境变量后，它自己的 steam_api 就以同一身份初始化，于是 appid / 大厅 /
// P2P 证书 / 叠加层天然一致，不需要任何内核改写。
using System.Diagnostics;
using System.Runtime.InteropServices;

// 文件法（联机页「AppID Changer」）：OnlineHost.exe --appid-txt "<游戏 exe 路径>" <AppID>
// 写 <游戏 exe 同目录>\steam_appid.txt + 设同一套 Steam 环境变量；游戏退出后按台账还原。
// ⚠️ 2026-09-20 对照实测修正：只写文件、不设环境变量**不够**（Steam 会把这个进程登记成 480，
// 但不给它叠加层/真大厅身份）。闭源工具 CaIInstallNext 的做法是给子进程带上
// SteamAppId/SteamGameId/SteamOverlayGameId=480（+ SteamEnv/SteamAppUser/STEAM_COMPAT_*），
// 我们跟进这一套；文件保留给"只认文件"的游戏。
// 由 Steam 启动时（快捷方式）可能一个参数都没有：启动选项留空，参数改由 GUI 先写进
// 请求文件。2026-11-17 实测：只有 Steam 拉起的进程树才拿得到叠加层，所以这条是主线，
// 直接带参数的调用方式保留给旧路径与手工排查。
// 请求文件优先于命令行参数：快捷方式的启动选项是静态的，里面的游戏 exe/身份往往已经过时。
// 文件读后即删，所以只有「GUI 刚写完就启动」这一种情形能命中。
var request = TakeSessionRequest();
if (request is not null)
{
    return request.ViaAppIdFile
        ? AppIdFileMode(request.GameExe, request.SessionAppId.ToString())
        : RunHost(request.GameExe, request.SessionAppId);
}

if (args.Length == 0)
{
    Log("无参数启动，且没有 session-request.json → 退出（码 6）");
    return 6;
}

if (args.Length >= 3 && args[0] == "--appid-txt")
    return AppIdFileMode(args[1], args[2]);

if (args.Length < 2 || !uint.TryParse(args[1], out var appId) || appId == 0)
    return 2;

return RunHost(args[0], appId);

// 宿主会话主体：设环境变量 → 垫片自注册（尽力而为）→ 拉起游戏并陪到退出。
static int RunHost(string gameExeArg, uint appId)
{
    var gameExe = Path.GetFullPath(gameExeArg);
    var gameDir = Path.GetDirectoryName(gameExe);
    if (gameDir is null || !File.Exists(gameExe))
    {
        Log($"游戏 exe 不存在: {gameExe}");
        return 3;
    }

    Log($"—— 宿主会话身份 {appId}，游戏 {gameExe}");
    ApplySessionEnvironment(appId.ToString());

    // best-effort：垫片用游戏目录里自带的 steam_api*.dll（找不到/位数不符就跳过，
    // 环境变量才是关键，游戏侧自己也会初始化）。宿主保持加载状态直到游戏退出。
    var hShim = InitShim(gameDir);

    using var game = Process.Start(new ProcessStartInfo(gameExe) { WorkingDirectory = gameDir });
    if (game is null) return 4;
    game.WaitForExit();

    ShutdownShim(hShim);
    return 0;
}

// 读 GUI 写的会话请求（读后即删：同一条请求不该被启动两次）。
// 三行纯文本，不用 JSON —— 宿主是 AOT 发布，JsonSerializer<T> 走反射会直接抛异常。
static SessionRequestPayload? TakeSessionRequest()
{
    var path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI", "session-request.txt");
    try
    {
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path);
        File.Delete(path);
        if (lines.Length < 2
            || !uint.TryParse(lines[1].Trim(), out var appId) || appId == 0
            || string.IsNullOrWhiteSpace(lines[0]))
        {
            Log("会话请求内容不完整，忽略");
            return null;
        }
        var viaFile = lines.Length > 2 && lines[2].Trim() == "1";
        Log($"会话请求: 游戏 {lines[0].Trim()}，身份 {appId}，文件法 {viaFile}");
        return new SessionRequestPayload { GameExe = lines[0].Trim(), SessionAppId = appId, ViaAppIdFile = viaFile };
    }
    catch (Exception ex)
    {
        Log($"读会话请求失败: {ex.GetType().Name} {ex.Message}");
        return null;
    }
}

// 用游戏自带的 steam_api64.dll 让宿主自己先以会话身份注册一次（Steam 日志里那条
// "App ID <n> adding PID <宿主>" 就是它），闭源工具同样先做这一步再拉游戏。
//
// 日志：%LOCALAPPDATA%\OSTGUI\logs\onlinehost.log —— 宿主是独立进程、且没有 GUI，
// "联机点了没反应"时这里是唯一线索。现代 Valve 垫片只导出 SteamAPI_InitFlat / InitSafe
// （老 SteamAPI_Init 已不存在），InitFlat 会用出参给回结果码 + 一句错误串，必须打出来。
static IntPtr InitShim(string gameDir)
{
    var shim = FindShim(gameDir, out var why);
    if (shim is null)
    {
        Log($"垫片: {why} → 跳过自注册（只剩环境变量这一半）");
        return IntPtr.Zero;
    }

    Log($"垫片: {shim}");
    var h = IntPtr.Zero;
    try { h = NativeLibrary.Load(shim); }
    catch (Exception ex)
    {
        Log($"垫片加载失败: {ex.Message}");
        return IntPtr.Zero;
    }

    try
    {
        var flat = GetExport<InitFlat>(h, "SteamAPI_InitFlat");
        if (flat is not null)
        {
            // SteamErrMsg = char[1024]；传缓冲区才有失败原因（不传就只能拿到个结果码）
            var err = Marshal.AllocHGlobal(1024);
            try
            {
                Marshal.WriteByte(err, 0);
                var result = flat(err);
                var msg = Marshal.PtrToStringAnsi(err)?.Trim() ?? "";
                if (result == 0)
                {
                    Log("自注册成功: SteamAPI_InitFlat = OK");
                }
                else
                {
                    Log($"自注册失败: SteamAPI_InitFlat = {ResultText(result)}"
                        + (msg.Length > 0 ? $"（{msg}）" : ""));
                }
            }
            finally { Marshal.FreeHGlobal(err); }

            return h;   // 失败也保持加载：游戏侧自己还会初始化一次
        }

        var safe = GetExport<InitSafe>(h, "SteamAPI_InitSafe");
        if (safe is not null)
            Log(safe.Invoke() ? "自注册成功: SteamAPI_InitSafe" : "自注册失败: SteamAPI_InitSafe 返回 false");
        else
            Log("垫片里既没有 SteamAPI_InitFlat 也没有 InitSafe（老的第三方垫片？）→ 跳过自注册");
    }
    catch (Exception ex)
    {
        // 垫片内部抛 C++ 异常时这里会拿到 0xE06D7363 一类的 HRESULT
        Log($"自注册异常: {ex.GetType().Name} {ex.Message}");
    }
    return h;
}

// 会话环境变量。前三个是身份（叠加层身份必须跟着会话 AppID：不带这个，客户端拿到的是
// "真 AppID"的叠加层身份，邀请对话框会从 480 大厅降级成普通好友列表，内核 v3 那轮的教训）。
// SteamClientLaunch 是拿 Steam 自己启动游戏时的标记：2026-11-13 客户端之后，宿主自建的
// 进程不再起 gameoverlayui（只给 Steam 亲生子进程起），这里试探客户端是否按这个标记放行。
static void ApplySessionEnvironment(string appIdText)
{
    Environment.SetEnvironmentVariable("SteamAppId", appIdText);
    Environment.SetEnvironmentVariable("SteamGameId", appIdText);
    Environment.SetEnvironmentVariable("SteamOverlayGameId", appIdText);
    Environment.SetEnvironmentVariable("SteamClientLaunch", "1");
}

// ESteamAPIInitResult：0=OK 1=FailedGeneric 2=NoSteamClient 3=VersionMismatch
static string ResultText(int result) => result switch
{
    0 => "OK",
    1 => "FailedGeneric（通用失败）",
    2 => "NoSteamClient（Steam 客户端没运行或没登录）",
    3 => "VersionMismatch（垫片与客户端版本不匹配）",
    _ => $"未知({result})"
};

static void Log(string message)
{
    try
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OSTGUI", "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllText(
            Path.Combine(dir, "onlinehost.log"),
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
    }
    catch { }
}

static void ShutdownShim(IntPtr h)
{
    if (h == IntPtr.Zero) return;
    try { GetExport<Shutdown>(h, "SteamAPI_Shutdown")?.Invoke(); } catch { }
}

// 游戏目录里找垫片（广度优先 ≤4 层）。两个要点：
//   ① Unity 把 steam_api*.dll 放在 <游戏>_Data\Plugins\<arch>\ 底下，只看根目录会漏；
//   ② 位数必须与宿主一致才能 LoadLibrary —— 64 位宿主加载 32 位垫片会直接失败。
static string? FindShim(string root, out string why)
{
    var hostIs64 = Environment.Is64BitProcess;
    string? matched = null;
    string? mismatchedName = null;
    var mismatchedPath = "";

    var queue = new Queue<(string Dir, int Depth)>();
    queue.Enqueue((root, 0));
    while (queue.Count > 0 && matched is null)
    {
        var (dir, depth) = queue.Dequeue();
        foreach (var (name, is64) in new[] { ("steam_api64.dll", true), ("steam_api.dll", false) })
        {
            var hit = Path.Combine(dir, name);
            if (!File.Exists(hit)) continue;
            if (is64 == hostIs64) { matched = hit; break; }
            if (mismatchedName is null) { mismatchedName = name; mismatchedPath = hit; }
        }
        if (depth >= 4) continue;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir)) queue.Enqueue((sub, depth + 1));
        }
        catch { }
    }

    if (matched is not null) { why = ""; return matched; }
    why = mismatchedName is null
        ? "游戏目录（含 *_Data\\Plugins 子目录）里没有 steam_api*.dll"
        : $"只找到 {mismatchedName}（{(hostIs64 ? "32" : "64")} 位），宿主是 {(hostIs64 ? "64" : "32")} 位，加载不了：{mismatchedPath}";
    return null;
}

static T? GetExport<T>(IntPtr module, string name) where T : Delegate
    => NativeLibrary.TryGetExport(module, name, out var p) && p != IntPtr.Zero
        ? Marshal.GetDelegateForFunctionPointer<T>(p)
        : null;

// ── 文件法（AppID Changer） ───────────────────────────────────────

static int AppIdFileMode(string exeArg, string appIdText)
{
    var exe = Path.GetFullPath(exeArg);
    var dir = Path.GetDirectoryName(exe);
    if (dir is null || !File.Exists(exe)) return 3;

    Log($"—— 文件法会话身份 {appIdText}，游戏 {exe}");
    var target = Path.Combine(dir, "steam_appid.txt");
    var journal = AppIdJournalPath();

    string? original;
    try { original = File.Exists(target) ? File.ReadAllText(target) : null; }
    catch { return 5; }             // 原文件读不动就什么都别动

    // 先记台账再动文件：这中间被杀也能还原
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, $"{dir}\n{(original is null ? "0" : "1")}\n{original}");
        File.WriteAllText(target, appIdText);
        Log($"已写 {target}（原本{(original is null ? "无此文件" : "有，原内容已记台账")}）");
    }
    catch (Exception ex)
    {
        try { RestoreAppIdFile(target, original); } catch { }
        Log($"写 {target} 失败，已回滚并退出（游戏未启动）: {ex.Message}");
        return 5;                   // 目录只读 / 文件被占：原样退出，不启动游戏
    }

    // 环境变量才是关键（对照闭源工具的实测）：子进程带着这几个才被当成 480 的真启动；
    // 文件同时写着，给"只认 steam_appid.txt"的游戏兜底
    ApplySessionEnvironment(appIdText);

    // 宿主也先以该身份注册一次，做法与闭源工具一致（它先起一个带环境的自身副本注册，再拉游戏）
    var hShim = InitShim(dir);

    try
    {
        using var game = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir });
        if (game is null) return 4;
        game.WaitForExit();
        // ponytail: 只等这一个进程；"启动器 → 另起的 exe" 会提前还原。真遇到再上进程树 / Job 对象。
    }
    catch
    {
        return 5;
    }
    finally
    {
        // 还原失败就留着台账，由 GUI 下次启动补（不抛：宿主是后台进程，抛出去只会弹系统错误框）
        try { RestoreAppIdFile(target, original); Log("已还原 steam_appid.txt"); }
        catch (Exception ex) { Log($"还原失败，台账留在 %LOCALAPPDATA%\\OSTGUI\\appid-changer.txt: {ex.Message}"); }
        ShutdownShim(hShim);
    }
    return 0;
}

// 还原原文件状态；文件操作成功后才销账（失败即保留台账，供 GUI 补还原）
static void RestoreAppIdFile(string target, string? original)
{
    if (original is null)
    {
        if (File.Exists(target)) File.Delete(target);
    }
    else
    {
        File.WriteAllText(target, original);
    }
    File.Delete(AppIdJournalPath());
}

// 台账三行：游戏目录 / 1=原本有这个文件 0=原本没有 / 原内容（原样）
static string AppIdJournalPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "OSTGUI", "appid-changer.txt");

internal delegate int InitFlat(IntPtr outErrMsg);
internal delegate bool InitSafe();
internal delegate void Shutdown();
