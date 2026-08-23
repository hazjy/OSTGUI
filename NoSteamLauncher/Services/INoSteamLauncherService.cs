using NoSteamLauncher.Models;
using System.Threading;

namespace NoSteamLauncher.Services;

/// <summary>
/// NoSteamLauncher 核心服务接口，供宿主（如 OSTGUI）调用。
/// </summary>
public interface INoSteamLauncherService
{
    /// <summary>
    /// 执行免 Steam 部署流程。
    /// </summary>
    /// <param name="options">部署选项</param>
    /// <param name="progress">进度回调，接收日志消息</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>部署结果</returns>
    Task<LaunchResult> ExecuteAsync(LaunchOptions options, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// 验证选项是否有效（不执行部署）。
    /// </summary>
    /// <param name="options">要验证的选项</param>
    /// <returns>验证通过返回 null，否则返回错误消息</returns>
    string? ValidateOptions(LaunchOptions options);
}