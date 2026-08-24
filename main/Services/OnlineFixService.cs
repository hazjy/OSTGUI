using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

        return killed > 0
            ? (true, $"已停止 {killed} 个联机游戏进程")
            : (false, "停止失败，请手动在 Steam 中结束游戏");
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
