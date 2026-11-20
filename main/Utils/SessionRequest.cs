using OSTGUI.Services;

namespace OSTGUI.Utils;

/// <summary>
/// 宿主会话的启动请求：GUI 先写这个文件，再由 Steam 启动 OnlineHost.exe，宿主读它拿参数。
///
/// 为什么绕一道文件：非 Steam 快捷方式的启动选项是静态的，而每局的游戏 exe 与会话身份
/// 都要变。把参数放进快捷方式就得每个游戏建一条；放进文件则一条快捷方式长期复用。
/// 2026-11-17 实测：Steam 拉起的进程树（宿主 → 游戏）才拿得到叠加层，所以宿主必须由
/// Steam 启动，这条文件就是那趟启动的唯一参数通道。
///
/// 格式是三行纯文本，不用 JSON：两边都是 AOT 发布，<c>JsonSerializer&lt;T&gt;</c> 走反射，
/// AOT 下直接抛 "Reflection-based serialization has been disabled"（2026-11-17 实测）。
/// 三行分别是游戏 exe 路径、会话身份、是否文件法（1/0）。
/// </summary>
internal static class SessionRequest
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI", "session-request.txt");

    /// <summary>写请求文件。失败必须让调用方知道：否则界面报成功、实际什么都没发生。</summary>
    public static bool Write(string gameExe, uint sessionAppId, bool viaAppIdFile)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllLines(FilePath, new[]
            {
                Path.GetFullPath(gameExe),
                sessionAppId.ToString(),
                viaAppIdFile ? "1" : "0"
            });
            return true;
        }
        catch (Exception ex)
        {
            LogService.Diag($"写会话请求失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
