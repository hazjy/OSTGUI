using Microsoft.Extensions.DependencyInjection;
using OSTGUI.Models;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OSTGUI.Services;

public enum UpdateCheckStatus
{
    /// <summary>本地就是最新（或远端更低）</summary>
    UpToDate,
    /// <summary>远端更高</summary>
    UpdateAvailable,
    /// <summary>没能确定（网络/解析失败）</summary>
    Failed,
    /// <summary>另一次检查正在进行</summary>
    Busy
}

/// <param name="Status">结果</param>
/// <param name="CurrentVersion">本机版本（三段式，如 1.7.5）</param>
/// <param name="LatestVersion">远端版本（仅 UpdateAvailable 时非空）</param>
/// <param name="Message">失败原因（仅 Failed 时非空）</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? Message = null);

/// <summary>
/// 检查更新：**直接读 GitHub Releases，不需要上传或维护任何清单文件**。
///
/// <list type="number">
/// <item>主源：<c>GET /repos/hazjy/OSTGUI/releases/latest</c> 的 302 —— 关掉自动重定向，只读响应头里的
/// <c>Location: .../releases/tag/vX.Y.Z</c>，拿到就取消请求（**不读 body**：你们在 ManifestLogWatcher 里
/// 踩过"HttpClient.Timeout 管不到读 body、静默挂 5 分钟"，这里连读都不读）；</item>
/// <item>备源：<c>api.github.com</c> 的同一端点（带 User-Agent，只取 tag_name）。</item>
/// </list>
///
/// 每源各自 5 秒硬超时；GitHub 的 "latest" 语义天然排除草稿与预发布，所以不用自己判 prerelease。
/// 提示走系统通知（带「前往发布页 / 暂不更新」两个按钮），按钮回调经 <see cref="HandleNotificationArgument"/>
/// （由 <c>Program</c> 的 NotificationInvoked 转入）。
/// </summary>
public static class UpdateService
{
    private const string Owner = "hazjy";
    private const string RepoName = "OSTGUI";
    private const string ReleasePageUrl = $"https://github.com/{Owner}/{RepoName}/releases/latest";
    private const string ApiLatestUrl = $"https://api.github.com/repos/{Owner}/{RepoName}/releases/latest";

    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(5);

    /// <summary>从 <c>.../releases/tag/v1.7.6</c> 里抠 tag</summary>
    private static readonly Regex TagFromLocation = new(@"releases/tag/([^/?#]+)", RegexOptions.Compiled);

    /// <summary>AllowAutoRedirect=false：主源正是靠 302 的 Location 拿 tag</summary>
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        // 超时逐源自己控制：Timeout 只管到响应头，读 body 那步没人管（ManifestLogWatcher 的实测教训）
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static int _checking;   // Interlocked 防重：手动与自动撞上时后者直接返回 Busy

    /// <summary>本机 GUI 版本（三段式；ProductVersion 里的 "+<git hash>" 不在这里）</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>启动后的自动检查：延迟 5 秒、不阻塞启动；只有发现新版本才弹通知，其余只进日志。</summary>
    public static async Task CheckOnStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var result = await CheckAsync(manual: false);
            if (result.Status == UpdateCheckStatus.Failed)
                LogService.Diag($"检查更新（自动）：未完成 —— {result.Message}");
        }
        catch (Exception ex)
        {
            LogService.Diag($"检查更新（自动）：异常 {ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// 检查一次。<paramref name="manual"/> = 用户在「关于」里点的：失败要给出原因、且**不受
    /// "同一版本只提示一次"的限制**；自动检查则相反（静默失败、同版本只提示一次）。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return new UpdateCheckResult(UpdateCheckStatus.Busy, CurrentVersion);

        try
        {
            var current = CurrentVersion;
            var local = ParseVersion(current);
            if (local == null)
                return Fail(current, $"本机版本无法解析：{current}");

            var tag = await TryGetLatestTagAsync(ct);
            if (tag == null)
                return Fail(current, "主源与备源均未取到版本号");

            var remote = ParseVersion(tag);
            if (remote == null)
                return Fail(current, $"远端版本无法解析：{tag}");

            var latest = VersionCompare.ToText(remote);
            if (!VersionCompare.IsRemoteNewer(remote, local))
            {
                LogService.Event($"检查更新：已是最新（本机 {current}，远端 {latest}）");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, current, latest);
            }

            // 有新版：自动检查时"同一版本只提示一次"（手动不受限）
            var config = TryGetConfig();
            if (!manual
                && config != null
                && string.Equals(config.Config.NotifiedUpdateVersion, latest, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Event($"检查更新：{latest} 已提示过，本次静默（本机 {current}）");
                return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, current, latest, "已提示过");
            }

            // 记账在"弹之前"写：手动弹过也算，避免下次启动自动再弹一遍同一版本
            // （ConfigService 的约定是"只写内存、退出时统一落盘"，所以硬杀进程可能没落盘 → 下次再提示一次）
            try { config?.Update(c => c.NotifiedUpdateVersion = latest); }
            catch (Exception ex) { LogService.Diag($"检查更新：写配置失败 {ex.GetType().Name}：{ex.Message}"); }

            LogService.Event($"检查更新：发现新版本 {latest}（本机 {current}）");
            ToastService.ShowUpdateAvailable(current, latest);
            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, current, latest);
        }
        catch (Exception ex)
        {
            return Fail(CurrentVersion, $"{ex.GetType().Name}：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>
    /// 系统通知按钮回调（Program 订阅 NotificationInvoked 后转进来）。
    /// <c>update-open</c> → 打开发布页；<c>update-skip</c> → 只记一行日志（关闭由系统完成）。
    /// </summary>
    public static void HandleNotificationArgument(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return;

        if (argument.Contains("update-open", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo { FileName = ReleasePageUrl, UseShellExecute = true });
                LogService.Event("检查更新：已打开项目发布页");
            }
            catch (Exception ex)
            {
                LogService.Diag($"检查更新：打开发布页失败 {ex.GetType().Name}：{ex.Message}");
            }
        }
        else if (argument.Contains("update-skip", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Event("检查更新：用户选择暂不更新");
        }
    }

    /// <summary>
    /// 版本比较的规则实现在 <see cref="VersionCompare"/>（纯逻辑，单独可验）：
    /// 按小数点逐位比，远端某位更大 → 有更新、某位更小 → 无更新、相等继续、全相等 → 无更新，缺位按 0。
    /// </summary>
    private static int[]? ParseVersion(string text) => VersionCompare.Parse(text);

    /// <summary>先主源（302 Location）后备源（API）；两者都失败返回 null</summary>
    private static async Task<string?> TryGetLatestTagAsync(CancellationToken ct)
    {
        // ① 主源：不跟随重定向，从 Location 里读 tag
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SourceTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasePageUrl);
            request.Headers.UserAgent.ParseAdd("OSTGUI-UpdateCheck");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var location = response.Headers.Location?.ToString();
            if (location != null)
            {
                var tag = TagFromLocation.Match(location).Groups[1].Value;
                if (!string.IsNullOrEmpty(tag))
                {
                    LogService.Event($"检查更新：主源命中 {tag}");
                    return tag;
                }
            }

            LogService.Diag($"检查更新：主源没给 tag（HTTP {(int)response.StatusCode}）");
        }
        catch (Exception ex)
        {
            LogService.Diag($"检查更新：主源失败 {ex.GetType().Name}：{ex.Message}");
        }

        // ② 备源：api.github.com（国内可能被拦，所以只当兜底）
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SourceTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, ApiLatestUrl);
            request.Headers.UserAgent.ParseAdd("OSTGUI-UpdateCheck");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                LogService.Diag($"检查更新：备源 HTTP {(int)response.StatusCode}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var info = await JsonSerializer.DeserializeAsync(stream, AppJsonCompactContext.Default.ReleaseInfo, timeout.Token);
            if (!string.IsNullOrEmpty(info?.TagName))
            {
                LogService.Event($"检查更新：备源命中 {info.TagName}");
                return info.TagName;
            }

            LogService.Diag("检查更新：备源没给 tag");
        }
        catch (Exception ex)
        {
            LogService.Diag($"检查更新：备源失败 {ex.GetType().Name}：{ex.Message}");
        }

        return null;
    }

    private static UpdateCheckResult Fail(string current, string message)
    {
        LogService.Diag($"检查更新：未完成 —— {message}");
        return new UpdateCheckResult(UpdateCheckStatus.Failed, current, null, message);
    }

    /// <summary>取配置服务（启动早期 DI 可能还没建好 → 返回 null，调用处按"没有配置"处理）</summary>
    private static ConfigService? TryGetConfig()
    {
        try { return App.Services.GetRequiredService<ConfigService>(); }
        catch { return null; }
    }
}
