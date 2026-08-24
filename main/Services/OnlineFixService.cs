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

    // 兼容模式（环境变量直启）跟踪的进程 PID。
    // 该模式下游戏不经 Steam 启动管线，命令行不含 -onlinefix，
    // PEB 扫描检测不到，只能靠自持 PID 管理生命周期（应用重启后丢失，属已知限制）。
    private int? _compatPid;

    public OnlineFixService(SteamService steamService)
    {
        _steamService = steamService;
    }

    /// <summary>
    /// 通过 steam.exe -applaunch 启动游戏并附加 -onlinefix 参数
    /// </summary>
    public async Task<(bool success, string message)> StartAsync(string appId)
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
                Arguments = $"-applaunch {appId} -onlinefix",
                UseShellExecute = true
            });

            // 等待游戏进程出现（最长约 15 秒）
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                if (FindOnlineFixProcessIds().Count > 0)
                    return (true, $"已启动 AppID {appId}（480 联机模式）");
            }

            return (true, $"已请求启动 AppID {appId}（未检测到联机进程，请确认游戏已安装）");
        }
        catch (Exception ex)
        {
            return (false, $"启动失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 是否已有 480 联机游戏在运行（内核模式受内核单游戏限制；兼容模式沿用同一开关，避免状态混淆）
    /// </summary>
    public bool IsRunning()
        => FindOnlineFixProcessIds().Count > 0 || IsCompatRunning;

    /// <summary>
    /// 兼容模式游戏是否仍在运行
    /// </summary>
    public bool IsCompatRunning
    {
        get
        {
            if (_compatPid is not int pid) return false;
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch
            {
                _compatPid = null;
                return false;
            }
        }
    }

    /// <summary>
    /// 停止当前 480 联机游戏进程（覆盖内核模式与兼容模式）
    /// </summary>
    public (bool success, string message) Stop()
    {
        var pids = FindOnlineFixProcessIds();
        if (_compatPid is int compatPid && IsPidAlive(compatPid) && !pids.Contains(compatPid))
            pids.Add(compatPid);

        if (pids.Count == 0)
            return (false, "当前没有正在运行的 480 联机游戏");

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
        if (IsCompatRunning == false)
            _compatPid = null;

        return killed > 0
            ? (true, $"已停止 {killed} 个联机游戏进程")
            : (false, "停止失败，请手动在 Steam 中结束游戏");
    }

    // ── 兼容模式：环境变量直启（全一致 480 世界） ──────────────────
    //
    // 原理：设 SteamAppId/SteamGameId=480 后直接启动游戏 exe，绕开 Steam 启动管线。
    // 游戏 steam_api 以 480 初始化（人人拥有，服务端校验必过），GetAppID、好友状态、
    // 大厅空间全部一致 —— 邀请校验（invite.gameID == GetAppID()）天然通过。
    // 适用：-onlinefix 模式下邀请无法入房的游戏（内核 GetAppID 还原造成身份不一致）。
    // 代价：成就计入 Spacewar；好友看到的是 Spacewar；DLC 按 480 校验。

    /// <summary>
    /// 兼容模式启动：以 Spacewar(480) 身份直接运行游戏 exe
    /// </summary>
    public async Task<(bool success, string message)> StartCompatAsync(string appId, string exePath)
    {
        if (!File.Exists(exePath))
            return (false, $"未找到游戏程序: {exePath}");

        if (!_steamService.IsSteamRunning())
            return (false, "Steam 未运行，请先启动并登录 Steam");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "."
            };
            psi.EnvironmentVariables["SteamAppId"] = "480";
            psi.EnvironmentVariables["SteamGameId"] = "480";

            _compatPid = Process.Start(psi)?.Id;
            await Task.Delay(1500); // 给进程一点初始化时间再返回

            return (true, $"已启动 AppID {appId}（兼容模式）");
        }
        catch (Exception ex)
        {
            _compatPid = null;
            return (false, $"启动失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析游戏安装目录与候选 exe：
    /// 遍历 libraryfolders.vdf 的库目录 → 找 appmanifest_{appId}.acf → 取 installdir →
    /// 枚举顶层 exe（过滤卸载器/运行库等噪音，按体积降序——主程序通常最大）
    /// </summary>
    public (string? InstallDir, List<string> Candidates) ResolveGameInstall(string appId)
    {
        var steamPath = _steamService.GetSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            return (null, new List<string>());

        foreach (var library in EnumerateLibraries(steamPath))
        {
            var acf = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(acf)) continue;

            var installdir = ReadAcfValue(acf, "installdir");
            if (string.IsNullOrEmpty(installdir)) continue;

            var dir = Path.Combine(library, "steamapps", "common", installdir);
            if (!Directory.Exists(dir)) continue;

            return (dir, CollectExeCandidates(dir));
        }

        return (null, new List<string>());
    }

    /// <summary>Steam 根目录 + libraryfolders.vdf 中登记的全部库目录</summary>
    private static List<string> EnumerateLibraries(string steamPath)
    {
        var libraries = new List<string> { steamPath };

        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            vdf = Path.Combine(steamPath, "config", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return libraries;

        try
        {
            var content = File.ReadAllText(vdf);
            foreach (Match m in Regex.Matches(content, "\"path\"\\s+\"(.+?)\""))
            {
                var p = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(p))
                    libraries.Add(p);
            }
        }
        catch { }

        return libraries;
    }

    /// <summary>从 appmanifest_{appId}.acf 读取指定键值（仅支持顶层 AppState 内的简单键）</summary>
    private static string? ReadAcfValue(string acfPath, string key)
    {
        try
        {
            var content = File.ReadAllText(acfPath);
            var m = Regex.Match(content, $"\"{key}\"\\s+\"(.+?)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>收集顶层 exe，过滤安装器/卸载器/崩溃处理器等噪音，按文件体积降序</summary>
    private static List<string> CollectExeCandidates(string dir)
    {
        string[] noise =
        [
            "uninstall", "crashhandler", "vcredist", "dxsetup", "dotnetfx",
            "redist", "directx", "installscript", "physx", "ueprereq"
        ];

        try
        {
            return Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(p =>
                {
                    var name = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                    return !noise.Any(name.Contains);
                })
                .OrderByDescending(p => new FileInfo(p).Length)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
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
