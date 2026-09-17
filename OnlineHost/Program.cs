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

if (args.Length < 2 || !uint.TryParse(args[1], out var appId) || appId == 0)
    return 2;

var gameExe = Path.GetFullPath(args[0]);
var gameDir = Path.GetDirectoryName(gameExe);
if (gameDir is null || !File.Exists(gameExe))
    return 3;

Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
Environment.SetEnvironmentVariable("SteamGameId", appId.ToString());

// best-effort：垫片用游戏目录里自带的 steam_api64.dll（找不到/位数不符就跳过，
// 环境变量才是关键，游戏侧自己也会初始化）。宿主保持加载状态直到游戏退出。
var shim = FindShim(gameDir);
var hShim = IntPtr.Zero;
if (shim is not null)
{
    try { hShim = NativeLibrary.Load(shim); } catch { }
    if (hShim != IntPtr.Zero)
    {
        try
        {
            var flat = GetExport<InitFlat>(hShim, "SteamAPI_InitFlat");
            var safe = GetExport<InitSafe>(hShim, "SteamAPI_InitSafe");
            if (flat is not null) flat(IntPtr.Zero);
            else safe?.Invoke();
        }
        catch { }
    }
}

using var game = Process.Start(new ProcessStartInfo(gameExe) { WorkingDirectory = gameDir });
if (game is null) return 4;
game.WaitForExit();

if (hShim != IntPtr.Zero)
{
    try { GetExport<Shutdown>(hShim, "SteamAPI_Shutdown")?.Invoke(); } catch { }
}
return 0;

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

internal delegate int InitFlat(IntPtr outErrMsg);
internal delegate bool InitSafe();
internal delegate void Shutdown();
