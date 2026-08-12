using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace OSTGUI.Services;

/// <summary>
/// Steam 授权票据在线提取器
/// 直接加载 steamclient64.dll 调用 ISteamAppTicket / ISteamUser 接口，
/// 从当前登录账号实时提取 AppTicket 与 ETicket（参考 OpenSteamTool extract_tickets）
/// </summary>
public class SteamTicketExtractor
{
    private const string SteamClientVersion = "SteamClient023";
    private const string SteamUserVersion = "SteamUser023";
    private const string SteamUtilsVersion = "SteamUtils010";
    private const string SteamAppTicketVersion = "STEAMAPPTICKET_INTERFACE_VERSION001";

    // EncryptedAppTicketResponse_t::k_iCallback == 100 + 54
    private const int EncryptedAppTicketCallback = 154;
    private const int EResultOK = 1;
    private const int MaxWaitMs = 15000;
    private const int PollStepMs = 50;
    private const uint LoadWithAlteredSearchPath = 0x8;

    // ── ISteamClient vtable ──────────────────────────────────────────────
    private delegate int CreateSteamPipeFn(IntPtr self);
    private delegate bool BReleaseSteamPipeFn(IntPtr self, int pipe);
    private delegate int ConnectToGlobalUserFn(IntPtr self, int pipe);
    private delegate IntPtr GetISteamUtilsFn(IntPtr self, int pipe, IntPtr version);
    private delegate IntPtr GetISteamUserFn(IntPtr self, int user, int pipe, IntPtr version);
    private delegate IntPtr GetISteamGenericInterfaceFn(IntPtr self, int user, int pipe, IntPtr version);

    // ── ISteamUtils vtable ───────────────────────────────────────────────
    private delegate bool IsAPICallCompletedFn(IntPtr self, ulong call, out bool failed);
    private delegate bool GetAPICallResultFn(IntPtr self, ulong call, IntPtr callback, int cbCallback, int expected, out bool failed);

    // ── ISteamUser vtable ────────────────────────────────────────────────
    private delegate ulong RequestEncryptedAppTicketFn(IntPtr self, IntPtr data, int cbData);
    private delegate bool GetEncryptedAppTicketFn(IntPtr self, IntPtr ticket, int cbMax, out uint cbTicket);

    // ── ISteamAppTicket vtable ───────────────────────────────────────────
    private delegate uint GetAppOwnershipTicketDataFn(
        IntPtr self, uint appId, IntPtr buffer, uint cbBuffer,
        out uint piAppId, out uint piSteamId, out uint piSignature, out uint pcbSignature);

    private delegate IntPtr CreateInterfaceFn(IntPtr name, out int returnCode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateInterfaceDelegate(IntPtr name, out int returnCode);

    /// <summary>
    /// 提取结果
    /// </summary>
    public class ExtractResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string AppTicketHex { get; set; } = "";
        public string ETicketHex { get; set; } = "";
    }

    /// <summary>
    /// 通过独立子进程提取授权票据。
    /// 加载 steamclient64.dll 的进程会被 Steam 识别为游戏进程（点"停止游戏"会连坐杀掉），
    /// 因此提取必须在短命子进程中完成，主进程保持干净。
    /// </summary>
    public static async Task<ExtractResult> ExtractInSubprocessAsync(string appId)
    {
        var exePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "OSTGUI.exe");
        var outFile = Path.Combine(Path.GetTempPath(), $"ost_extract_{Guid.NewGuid():N}.json");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--extract-ticket {appId} \"{outFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return new ExtractResult { Success = false, Message = "无法启动提取进程" };

            await proc.WaitForExitAsync();

            if (!File.Exists(outFile))
                return new ExtractResult
                {
                    Success = false,
                    Message = $"提取进程无输出（退出码 {proc.ExitCode}）",
                };

            var json = await File.ReadAllTextAsync(outFile);
            var result = JsonSerializer.Deserialize<ExtractResult>(json);
            return result ?? new ExtractResult { Success = false, Message = "提取结果解析失败" };
        }
        catch (Exception ex)
        {
            return new ExtractResult { Success = false, Message = $"提取异常: {ex.Message}" };
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
        }
    }

    /// <summary>
    /// 从当前登录的 Steam 账号提取 AppTicket + ETicket（阻塞，建议放后台线程）
    /// </summary>
    public ExtractResult Extract(string appId)
    {
        try
        {
            var steamPath = FindSteamInstallPath();
            if (steamPath == null)
                return Fail("未找到 Steam 安装路径（注册表）");

            var dllPath = Path.Combine(steamPath, "steamclient64.dll");
            if (!File.Exists(dllPath))
                return Fail("找不到 steamclient64.dll，请确认 Steam 已安装");

            // 必须在加载 DLL 前设置，让接口调用落到目标 AppID
            Environment.SetEnvironmentVariable("SteamAppId", appId);
            Environment.SetEnvironmentVariable("SteamGameId", appId);

            SetDllDirectory(steamPath);
            var module = LoadLibraryEx(dllPath, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (module == IntPtr.Zero)
                return Fail($"加载 steamclient64.dll 失败 (0x{GetLastError():X8})");

            try
            {
                var createInterfacePtr = GetProcAddress(module, "CreateInterface");
                if (createInterfacePtr == IntPtr.Zero)
                    return Fail("steamclient64.dll 缺少 CreateInterface 导出");

                var createInterface = Marshal.GetDelegateForFunctionPointer<CreateInterfaceDelegate>(createInterfacePtr);
                var client = CreateSteamClient(createInterface);
                if (client == IntPtr.Zero)
                    return Fail("创建 Steam 客户端接口失败，请确认 Steam 正在运行");

                var clientVtable = Marshal.ReadIntPtr(client);
                var createPipe = GetDelegate<CreateSteamPipeFn>(clientVtable, 0);
                var releasePipe = GetDelegate<BReleaseSteamPipeFn>(clientVtable, 1);
                var connectGlobal = GetDelegate<ConnectToGlobalUserFn>(clientVtable, 2);
                var getUtils = GetDelegate<GetISteamUtilsFn>(clientVtable, 9);
                var getUser = GetDelegate<GetISteamUserFn>(clientVtable, 5);
                var getGeneric = GetDelegate<GetISteamGenericInterfaceFn>(clientVtable, 12);

                var pipe = createPipe(client);
                if (pipe == 0)
                    return Fail("CreateSteamPipe 失败，请确认 Steam 正在运行");

                try
                {
                    var user = connectGlobal(client, pipe);
                    if (user == 0)
                        return Fail("ConnectToGlobalUser 失败，请确认已登录 Steam 账号");

                    // ETicket 仅由 Steam 服务器为正版拥有的账号签发，
                    // 拿不到即说明当前账号无所有权或未在线
                    var eTicketHex = ExtractEncryptedAppTicket(
                        client, user, pipe, getUtils, getUser, appId);
                    if (eTicketHex == null)
                        return Fail("获取加密票据失败（请确认已登录正版账号且在线，该账号需拥有此游戏）");

                    var appTicketHex = ExtractAppOwnershipTicket(client, user, pipe, getGeneric, appId);
                    if (appTicketHex == null)
                        return Fail("获取所有权票据失败（在线验证已通过，但本地未缓存所有权票据）");

                    return new ExtractResult
                    {
                        Success = true,
                        Message = $"提取成功（AppTicket {appTicketHex.Length / 2} 字节 / ETicket {eTicketHex.Length / 2} 字节）",
                        AppTicketHex = appTicketHex,
                        ETicketHex = eTicketHex,
                    };
                }
                finally
                {
                    releasePipe(client, pipe);
                }
            }
            finally
            {
                FreeLibrary(module);
                // 解除 Steam 的进程识别：加载 DLL 期间设置的 SteamAppId/SteamGameId
                // 会让 Steam 把本进程当作该 AppID 的游戏进程，点“停止游戏”会连坐杀掉
                Environment.SetEnvironmentVariable("SteamAppId", null);
                Environment.SetEnvironmentVariable("SteamGameId", null);
            }
        }
        catch (Exception ex)
        {
            return Fail($"提取异常: {ex.Message}");
        }
    }

    private static string? ExtractAppOwnershipTicket(
        IntPtr client, int user, int pipe, GetISteamGenericInterfaceFn getGeneric, string appId)
    {
        var versionPtr = Marshal.StringToCoTaskMemAnsi(SteamAppTicketVersion);
        try
        {
            var intf = getGeneric(client, user, pipe, versionPtr);
            if (intf == IntPtr.Zero) return null;

            var getOwnership = GetDelegate<GetAppOwnershipTicketDataFn>(Marshal.ReadIntPtr(intf), 0);
            var buffer = Marshal.AllocHGlobal(2048);
            try
            {
                var written = getOwnership(
                    intf, uint.Parse(appId), buffer, 2048,
                    out _, out _, out _, out _);
                if (written == 0 || written > 2048) return null;

                var data = new byte[written];
                Marshal.Copy(buffer, data, 0, (int)written);
                return ToHex(data);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(versionPtr);
        }
    }

    private static string? ExtractEncryptedAppTicket(
        IntPtr client, int user, int pipe,
        GetISteamUtilsFn getUtils, GetISteamUserFn getUser, string appId)
    {
        var utilsVersionPtr = Marshal.StringToCoTaskMemAnsi(SteamUtilsVersion);
        var userVersionPtr = Marshal.StringToCoTaskMemAnsi(SteamUserVersion);
        try
        {
            var utils = getUtils(client, pipe, utilsVersionPtr);
            var steamUser = getUser(client, user, pipe, userVersionPtr);
            if (utils == IntPtr.Zero || steamUser == IntPtr.Zero) return null;

            var utilsVtable = Marshal.ReadIntPtr(utils);
            var isCompleted = GetDelegate<IsAPICallCompletedFn>(utilsVtable, 11);
            var getResult = GetDelegate<GetAPICallResultFn>(utilsVtable, 13);

            var userVtable = Marshal.ReadIntPtr(steamUser);
            var requestTicket = GetDelegate<RequestEncryptedAppTicketFn>(userVtable, 21);
            var getTicket = GetDelegate<GetEncryptedAppTicketFn>(userVtable, 22);

            var hCall = requestTicket(steamUser, IntPtr.Zero, 0);
            if (hCall == 0) return null;

            bool failed = false;
            var waited = 0;
            while (!isCompleted(utils, hCall, out failed))
            {
                if (waited >= MaxWaitMs) return null;
                Thread.Sleep(PollStepMs);
                waited += PollStepMs;
            }
            if (failed) return null;

            var callbackBuf = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                var gotResult = getResult(
                    utils, hCall, callbackBuf, sizeof(int), EncryptedAppTicketCallback, out failed);
                var result = Marshal.ReadInt32(callbackBuf);
                if (!gotResult || failed || result != EResultOK) return null;
            }
            finally
            {
                Marshal.FreeHGlobal(callbackBuf);
            }

            uint cbTicket = 0;
            getTicket(steamUser, IntPtr.Zero, 0, out cbTicket);
            if (cbTicket == 0) return null;

            var ticketBuf = Marshal.AllocHGlobal((int)cbTicket);
            try
            {
                if (!getTicket(steamUser, ticketBuf, (int)cbTicket, out cbTicket)) return null;
                var data = new byte[cbTicket];
                Marshal.Copy(ticketBuf, data, 0, (int)cbTicket);
                return ToHex(data);
            }
            finally
            {
                Marshal.FreeHGlobal(ticketBuf);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(utilsVersionPtr);
            Marshal.FreeCoTaskMem(userVersionPtr);
        }
    }

    private static IntPtr CreateSteamClient(CreateInterfaceDelegate createInterface)
    {
        var namePtr = Marshal.StringToCoTaskMemAnsi(SteamClientVersion);
        try
        {
            return createInterface(namePtr, out _);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    private static T GetDelegate<T>(IntPtr vtable, int index) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, index * IntPtr.Size));

    private static string? FindSteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static ExtractResult Fail(string message) => new() { Success = false, Message = message };

    private static string ToHex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr hFile, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    [DllImport("kernel32.dll")]
    private static extern uint GetLastError();
}
