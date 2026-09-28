using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OSTGUI.Services;

/// <summary>
/// 480 联机（OST -onlinefix）服务：启动、检测、停止
/// 游戏进程以 Spacewar(480) 身份运行时，其环境变量 SteamAppId/SteamGameId 为 480，
/// 命令行包含 -onlinefix；通过读取进程 PEB 中的命令行来识别并管理。
/// </summary>
public class OnlineFixService
{
    private readonly SteamService _steamService;

    public OnlineFixService(SteamService steamService)
    {
        _steamService = steamService;
    }

    /// <summary>
    /// 通过 steam.exe -applaunch 启动游戏并附加 -onlinefix 参数。
    /// sessionAppId = 会话身份（默认 480），内核按 -onlinefix=&lt;appid&gt; 解析。
    /// </summary>
    public async Task<(bool success, string message)> StartAsync(string appId, string sessionAppId)
    {
        var steamPath = _steamService.GetSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            return (false, "Steam 路径未设置，请先在设置中配置");

        var steamExe = Path.Combine(steamPath, "steam.exe");
        if (!File.Exists(steamExe))
            return (false, $"未找到 steam.exe: {steamExe}");

        if (!_steamService.IsSteamRunning())
            return (false, "Steam 未运行，请先启动并登录 Steam（需在线模式）");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                Arguments = $"-applaunch {appId} -onlinefix={sessionAppId}",
                UseShellExecute = true
            });

            // 等待游戏进程出现（最长约 15 秒）
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                if (FindOnlineFixProcessIds().Count > 0)
                    return (true, $"已启动 AppID {appId}（会话身份 {sessionAppId}）");
            }

            return (true, $"已请求启动 AppID {appId}（未检测到联机进程，请确认游戏已安装）");
        }
        catch (Exception ex)
        {
            return (false, $"启动失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 是否已有 480 联机游戏在运行（内核限制同一时间只能一个）
    /// </summary>
    public bool IsRunning()
        => FindOnlineFixProcessIds().Count > 0;

    /// <summary>
    /// 停止当前 480 联机游戏进程
    /// </summary>
    public (bool success, string message) Stop()
    {
        var pids = FindOnlineFixProcessIds();
        if (pids.Count == 0)
            return (false, "当前无正在运行的 480 联机游戏");

        var killed = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill();
                killed++;
            }
            catch { }
        }

        return killed > 0
            ? (true, $"已停止 {killed} 个联机游戏进程")
            : (false, "停止失败，请手动在 Steam 中结束游戏");
    }

    /// <summary>
    /// 宿主启动（不依赖内核 -onlinefix）：由 OnlineHost.exe 以会话身份初始化 Steam，
    /// 再把游戏作为子进程拉起，游戏自身的 appid / 大厅 / P2P 证书天然一致。
    /// viaAppIdFile = true 时走文件法（AppID Changer）：宿主写游戏 exe 同目录的
    /// steam_appid.txt **并**设同一套 Steam 环境变量（SteamAppId/SteamGameId/SteamOverlayGameId）、
    /// 自己也先以该身份注册一次，游戏退出后由宿主还原原文件。
    /// （2026-09-20 对照闭源工具实测修正：只写文件不设环境变量拿不到 480 的叠加层/真大厅身份。）
    /// </summary>
    public (bool success, string message) StartViaHost(string gameExe, string sessionAppId, bool viaAppIdFile = false)
    {
        if (!_steamService.IsSteamRunning())
            return (false, "Steam 未运行，请先启动并登录 Steam（需在线模式）");

        var host = Path.Combine(AppContext.BaseDirectory, "OnlineHost.exe");
        if (!File.Exists(host))
            return (false, $"未找到 OnlineHost.exe: {host}");

        try
        {
            using var hostProc = Process.Start(new ProcessStartInfo(host)
            {
                Arguments = viaAppIdFile
                    ? $"--appid-txt \"{gameExe}\" {sessionAppId}"
                    : $"\"{gameExe}\" {sessionAppId}",
                UseShellExecute = false
            });

            // 宿主是"陪到游戏退出"的长命进程（OnlineHost/Program.cs 里 game.WaitForExit()），
            // 半秒内就退出必然是它根本没起来 —— 典型情况是发布漏了 OnlineHost.dll，只剩一个空壳
            // apphost。以前这里是即发即忘，界面照报成功，故障完全隐形（2026-09-28 实测踩到）。
            if (hostProc is null)
                return (false, "联机宿主启动失败（进程创建失败）");

            if (hostProc.WaitForExit(500))
                return (false, $"联机宿主启动后立即退出：{DescribeHostExit(hostProc.ExitCode)}。" +
                               $"详情见日志 {OnlineHostLogPath}");

            if (!viaAppIdFile)
                return (true, $"已以 {sessionAppId} 身份启动 {Path.GetFileName(gameExe)}");

            // 文件法的成败全在这个文件上：目录只读/被占时宿主会立刻失败退出，
            // 这里等最多 1.5 秒确认真的写上了，免得报了成功其实没启动
            var target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gameExe))!, "steam_appid.txt");
            for (var i = 0; i < 6; i++)
            {
                if (File.Exists(target) && File.ReadAllText(target).Trim() == sessionAppId)
                    return (true, $"已写入 AppID {sessionAppId} 并启动 {Path.GetFileName(gameExe)}（退出后自动还原）");
                Thread.Sleep(250);
            }
            return (false, $"无法写入 {target}（目录只读或被占用），游戏未启动");
        }
        catch (Exception ex)
        {
            return (false, $"启动失败：{ex.Message}");
        }
    }

    /// <summary>文件法台账（与 config.json 同目录）：三行 = 游戏目录 / 原本有无该文件 / 原内容</summary>
    public static string AppIdJournalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI", "appid-changer.txt");

    /// <summary>宿主的日志（宿主没有界面，"联机点了没反应"时它是唯一线索）</summary>
    public static string OnlineHostLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI", "logs", "onlinehost.log");

    /// <summary>把宿主的退出码翻译成能看懂的失败原因（取值见 OnlineHost/Program.cs）</summary>
    private static string DescribeHostExit(int code) => code switch
    {
        2 => "参数无效",
        3 => "未找到游戏 exe",
        4 => "游戏进程启动失败",
        _ => "联机宿主异常退出（退出码 " + code + "）。常见原因：发布缺少 OnlineHost.dll"
    };

    /// <summary>
    /// 补还原：宿主被杀 / 断电会留下台账。宿主还活着说明会话仍在（它会自己还原），跳过。
    /// </summary>
    public void RestoreAppIdFileLeftover()
    {
        if (!File.Exists(AppIdJournalPath) || IsHostRunning()) return;

        try
        {
            var lines = File.ReadAllText(AppIdJournalPath).Split('\n', 3);
            var target = Path.Combine(lines[0].TrimEnd('\r'), "steam_appid.txt");
            var original = lines.Length > 2 ? lines[2] : null;

            if (lines[1].TrimEnd('\r') == "0")
            {
                if (File.Exists(target)) File.Delete(target);
            }
            else if (original is not null)
            {
                File.WriteAllText(target, original);
            }

            File.Delete(AppIdJournalPath);
            ToastService.ShowSuccess("AppID Changer", "已恢复上次未还原的 steam_appid.txt");
        }
        catch (Exception ex)
        {
            LogService.Diag($"[AppID Changer] 补还原失败：{ex.Message}");
            ToastService.ShowError("AppID Changer", $"上次的 steam_appid.txt 还原失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按 AppID 解析已安装游戏的主程序路径：libraryfolders.vdf 找库 → appmanifest 的 installdir。
    /// </summary>
    public string? ResolveGameExe(string appId)
    {
        var steamPath = _steamService.GetSteamPath();
        if (string.IsNullOrEmpty(steamPath) || appId.Length == 0 || !appId.All(char.IsDigit))
            return null;

        var libs = new List<string> { steamPath };
        foreach (var vdf in new[]
                 {
                     Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamPath, "config", "libraryfolders.vdf")
                 })
        {
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            break;
        }

        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(acf)) continue;

            var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"(.+?)\"");
            if (!m.Success) continue;

            var dir = Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
            if (Directory.Exists(dir)) return MainExeIn(dir);
        }
        return null;
    }

    private const int MaxScanDepth = 4;     // 主程序最多下探几层（bin64\ 这类一层就够，留点余量）
    private const int MaxScanDirs = 2000;   // 最多访问多少个目录（游戏目录动辄上万文件，不能全遍历）

    /// <summary>
    /// 找主程序。按可信度分三档，而不是只看顶层 —— 目录结构千奇百怪：
    /// 红色沙漠（3321460）顶层只有 0000..0025 资源目录，主程序在 bin64\CrimsonDesert.exe，
    /// 而且目录名（Crimson Desert，带空格）与程序名（CrimsonDesert）也对不上，
    /// 旧的"顶层同名 exe → 顶层最大 exe"必然落空（2026-09-28 用户实测）。
    /// 第 2 档保留旧行为：像 wallpaper_engine → wallpaper64.exe 这种程序名跟目录名无关的，
    /// 顶层最大依然是对的（曾试过用"steam_api64.dll 同目录"当第 2 档，在那里选错成了 wallpaperui.exe）。
    /// </summary>
    private static string? MainExeIn(string dir)
    {
        var candidates = ExesWithin(dir, MaxScanDepth).Where(IsGameExe).ToList();
        if (candidates.Count == 0) return null;

        // 1) 与游戏目录同名（忽略大小写与空格/下划线等分隔符差异）—— 主程序在子目录里也照样命中
        var wanted = NormalizeName(Path.GetFileName(dir));
        var named = candidates
            .Where(f => NormalizeName(Path.GetFileNameWithoutExtension(f.Name)) == wanted)
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        if (named is not null) return named.FullName;

        // 2) 游戏目录顶层最大的那个
        var top = Path.TrimEndingDirectorySeparator(dir);
        var atTop = candidates
            .Where(f => string.Equals(f.DirectoryName, top, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        if (atTop is not null) return atTop.FullName;

        // 3) 兜底：下层里最大的（红色沙漠是这一档）
        return candidates.OrderByDescending(f => f.Length).First().FullName;
    }

    /// <summary>只抹平大小写与分隔符差异，不做缩写/拼音之类的猜测</summary>
    private static string NormalizeName(string name)
        => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// 排除崩溃处理器 / 卸载器 / 运行库安装器。路径里的 redist 也要排除
    /// （Steamworks Shared 这种目录里全是 _CommonRedist 安装器），但"crash"只在文件名上判，
    /// 免得误伤名字里就带 crash 的游戏本体。
    /// </summary>
    private static bool IsGameExe(FileInfo f)
    {
        var n = f.Name;
        if (n.Contains("crash", StringComparison.OrdinalIgnoreCase)
            || n.Contains("vcredist", StringComparison.OrdinalIgnoreCase)
            || n.Contains("redist", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
            return false;

        return !(f.DirectoryName ?? "").Contains("redist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>广度优先收集 exe，既限层数也限目录总数（宿主的 shim 查找用的是同一套路）</summary>
    private static List<FileInfo> ExesWithin(string root, int maxDepth)
    {
        var found = new List<FileInfo>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < MaxScanDirs)
        {
            var (path, depth) = queue.Dequeue();
            visited++;

            DirectoryInfo d;
            try
            {
                d = new DirectoryInfo(path);
                if (!d.Exists) continue;
                found.AddRange(d.GetFiles("*.exe"));
            }
            catch { continue; }   // 权限不足 / 目录消失：跳过这一支，不影响其它分支

            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in d.GetDirectories()) queue.Enqueue((sub.FullName, depth + 1));
            }
            catch { }
        }
        return found;
    }

    /// <summary>是否有 DLL 注入（宿主 480）联机游戏在运行</summary>
    public bool IsHostRunning() => FindHostProcessIds().Count > 0;

    /// <summary>
    /// 停止 DLL 注入联机游戏：先从宿主命令行里取出游戏 exe 结束游戏，再结束宿主。
    /// </summary>
    public (bool success, string message) StopViaHost()
    {
        var hosts = FindHostProcessIds();
        if (hosts.Count == 0)
            return (false, "当前无正在运行的 DLL 注入游戏");

        var games = 0;
        var stopped = 0;
        foreach (var pid in hosts)
        {
            var gameExe = GameExeFromHost(pid);
            if (gameExe is not null)
            {
                var name = Path.GetFileNameWithoutExtension(gameExe);
                foreach (var game in Process.GetProcessesByName(name))
                {
                    try { game.Kill(); games++; } catch { }
                    finally { game.Dispose(); }
                }
            }

            try
            {
                // 先给宿主 3 秒自己收尾（文件法要还原文件、宿主路线要 SteamAPI_Shutdown），赖着不走才强杀
                using var host = Process.GetProcessById(pid);
                if (!host.WaitForExit(3000)) host.Kill();
                stopped++;
            }
            catch { }
        }

        return stopped > 0
            ? (true, $"已停止游戏 {games} 个、启动器 {stopped} 个")
            : (false, "停止失败，请手动结束进程");
    }

    private static List<int> FindHostProcessIds()
    {
        var result = new List<int>();
        foreach (var proc in Process.GetProcessesByName("OnlineHost"))
        {
            result.Add(proc.Id);
            proc.Dispose();
        }
        return result;
    }

    /// <summary>
    /// 宿主命令行形如: OnlineHost.exe "&lt;游戏 exe&gt;" &lt;appid&gt;
    /// 取最后一个带引号的 .exe —— 第一个可能是宿主自己（argv[0] 不一定带引号）。
    /// </summary>
    private static string? GameExeFromHost(int pid)
    {
        var matches = Regex.Matches(ReadCommandLine(pid) ?? "", "\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase);
        return matches.Count > 0 ? matches[matches.Count - 1].Groups[1].Value : null;
    }

    /// <summary>
    /// 枚举命令行包含 -onlinefix 的进程（排除 steam.exe 自身）
    /// </summary>
    private static List<int> FindOnlineFixProcessIds()
    {
        var result = new List<int>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (string.Equals(proc.ProcessName, "steam", StringComparison.OrdinalIgnoreCase))
                    continue;

                var cmdLine = ReadCommandLine(proc.Id);
                if (!string.IsNullOrEmpty(cmdLine) &&
                    cmdLine.Contains("-onlinefix", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(proc.Id);
                }
            }
            catch { }
        }
        return result;
    }

    // ── 通过 PEB 读取进程命令行（x64） ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr h, int cls, out ProcessBasicInformation info, int len, out int retLen);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static bool ReadPtr(IntPtr h, IntPtr addr, out IntPtr val)
    {
        var buf = new byte[IntPtr.Size];
        if (!ReadProcessMemory(h, addr, buf, buf.Length, out _))
        {
            val = IntPtr.Zero;
            return false;
        }
        val = (IntPtr)BitConverter.ToInt64(buf, 0);
        return true;
    }

    private static string? ReadCommandLine(int pid)
    {
        const uint processQueryInfo = 0x0400;
        const uint processVmRead = 0x0010;

        var h = OpenProcess(processQueryInfo | processVmRead, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (NtQueryInformationProcess(h, 0, out var pbi,
                    Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return null;

            // PEB 在本机始终为 64 位原生布局（WOW64 进程同样如此）
            // PEB + 0x20 -> RTL_USER_PROCESS_PARAMETERS
            if (!ReadPtr(h, pbi.PebBaseAddress + 0x20, out var pp))
                return null;

            // ProcessParameters + 0x70 -> CommandLine (UNICODE_STRING)
            // Buffer 指针位于 +8（8 字节）
            var cmdLinePtr = pp + 0x70;
            var header = new byte[16];
            if (!ReadProcessMemory(h, cmdLinePtr, header, header.Length, out _))
                return null;

            var length = BitConverter.ToUInt16(header, 0);
            if (length == 0 || length > 8192) return null;

            var bufferPtr = (IntPtr)BitConverter.ToInt64(header, 8);

            var data = new byte[length];
            if (!ReadProcessMemory(h, bufferPtr, data, data.Length, out _))
                return null;

            return Encoding.Unicode.GetString(data);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
