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
if (args.Length >= 3 && args[0] == "--appid-txt")
    return AppIdFileMode(args[1], args[2]);

if (args.Length < 2 || !uint.TryParse(args[1], out var appId) || appId == 0)
    return 2;

var gameExe = Path.GetFullPath(args[0]);
var gameDir = Path.GetDirectoryName(gameExe);
if (gameDir is null || !File.Exists(gameExe))
    return 3;

Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
Environment.SetEnvironmentVariable("SteamGameId", appId.ToString());
// 叠加层身份必须跟着会话 AppID：不带这个，客户端拿到的是"真 AppID"的叠加层身份，
// 邀请对话框会从 480 大厅降级成普通好友列表（内核 v3 那轮的教训；闭源工具也带这一项）
Environment.SetEnvironmentVariable("SteamOverlayGameId", appId.ToString());

// best-effort：垫片用游戏目录里自带的 steam_api64.dll（找不到/位数不符就跳过，
// 环境变量才是关键，游戏侧自己也会初始化）。宿主保持加载状态直到游戏退出。
var hShim = InitShim(gameDir);

using var game = Process.Start(new ProcessStartInfo(gameExe) { WorkingDirectory = gameDir });
if (game is null) return 4;
game.WaitForExit();

ShutdownShim(hShim);
return 0;

// 用游戏自带的 steam_api64.dll 让宿主自己先以会话身份注册一次（Steam 日志里那条
// "App ID <n> adding PID <宿主>" 就是它），闭源工具同样先做这一步再拉游戏
static IntPtr InitShim(string gameDir)
{
    var shim = FindShim(gameDir);
    if (shim is null) return IntPtr.Zero;

    var h = IntPtr.Zero;
    try { h = NativeLibrary.Load(shim); } catch { }
    if (h == IntPtr.Zero) return IntPtr.Zero;

    try
    {
        var flat = GetExport<InitFlat>(h, "SteamAPI_InitFlat");
        var safe = GetExport<InitSafe>(h, "SteamAPI_InitSafe");
        if (flat is not null) flat(IntPtr.Zero);
        else safe?.Invoke();
    }
    catch { }
    return h;
}

static void ShutdownShim(IntPtr h)
{
    if (h == IntPtr.Zero) return;
    try { GetExport<Shutdown>(h, "SteamAPI_Shutdown")?.Invoke(); } catch { }
}

// 游戏目录里找 steam_api64.dll（广度优先 ≤3 层，够覆盖 Unity 的 Data\Plugins\x86_64）
static string? FindShim(string root)
{
    var queue = new Queue<(string Dir, int Depth)>();
    queue.Enqueue((root, 0));
    while (queue.Count > 0)
    {
        var (dir, depth) = queue.Dequeue();
        var hit = Path.Combine(dir, "steam_api64.dll");
        if (File.Exists(hit)) return hit;
        if (depth >= 3) continue;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir)) queue.Enqueue((sub, depth + 1));
        }
        catch { }
    }
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
    }
    catch
    {
        try { RestoreAppIdFile(target, original); } catch { }
        return 5;                   // 目录只读 / 文件被占：原样退出，不启动游戏
    }

    // 环境变量才是关键（对照闭源工具的实测）：子进程带着这三个才被当成 480 的真启动；
    // 文件同时写着，给"只认 steam_appid.txt"的游戏兜底
    Environment.SetEnvironmentVariable("SteamAppId", appIdText);
    Environment.SetEnvironmentVariable("SteamGameId", appIdText);
    Environment.SetEnvironmentVariable("SteamOverlayGameId", appIdText);

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
        // 还原失败就留着台账，由 GUI 下次启动补（不抛：宿主没有日志，抛出去只会弹系统错误框）
        try { RestoreAppIdFile(target, original); } catch { }
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
