using Microsoft.Extensions.DependencyInjection;
using OSTGUI.Models;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OSTGUI.Services;

public enum UpdateCheckStatus
{
    /// <summary>两个维度都是最新（或远端更低）</summary>
    UpToDate,
    /// <summary>至少一个维度远端更高</summary>
    UpdateAvailable,
    /// <summary>两个维度都没能确定（网络/解析失败）</summary>
    Failed,
    /// <summary>另一次检查正在进行</summary>
    Busy
}

/// <summary>
/// 组合检查结果：GUI 与内核各一个维度；某维度 <c>*Latest</c> 非空 = 该维度有更新。
/// 内核没装（<see cref="KernelCurrent"/> 为 null）时该维度**不参与**判定。
/// </summary>
/// <param name="Status">整体结论</param>
/// <param name="GuiCurrent">本机 GUI 版本（三段式，如 1.7.9）</param>
/// <param name="GuiLatest">远端 GUI 版本（仅"有更新"时非空）</param>
/// <param name="KernelCurrent">本机内核版本；未安装 / 读不到为 null</param>
/// <param name="KernelLatest">远端内核版本（仅"有更新"时非空）</param>
/// <param name="Message">部分失败的原因（两个维度都没成功时，它就是整体失败的原因）</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string GuiCurrent,
    string? GuiLatest = null,
    string? KernelCurrent = null,
    string? KernelLatest = null,
    string? Message = null)
{
    public bool GuiHasUpdate => GuiLatest != null;
    public bool KernelHasUpdate => KernelLatest != null;
}

/// <summary>通知上的一个按钮（<paramref name="Argument"/> 是回调参数：见 <see cref="UpdateService.HandleNotificationArgument"/>）</summary>
public sealed record UpdateToastButton(string Label, string Argument);

/// <summary>要弹的那条更新通知（由 <see cref="UpdateService"/> 组装；纯数据，便于自检）</summary>
/// <param name="Text">正文，如「发现内核新版本 v1.1.4，当前版本 v1.1.3」</param>
/// <param name="Buttons">按钮（内核维度在前、GUI 在后；1～2 个）</param>
/// <param name="HasKernel">这条通知覆盖了内核维度（记账用）</param>
/// <param name="HasGui">这条通知覆盖了 GUI 维度（记账用）</param>
public sealed record UpdateToastPlan(
    string Text,
    IReadOnlyList<UpdateToastButton> Buttons,
    bool HasKernel,
    bool HasGui);

/// <summary>
/// 检查更新：**直接读 GitHub Releases，不需要上传或维护任何清单文件**。
/// 一次检查同时看两个仓库 —— GUI（<c>hazjy/OSTGUI</c>）与内核（<c>hazjy/ZSteamTool</c>），
/// 两者各自的新版本合成**一条**系统通知（最多两个按钮）。
///
/// <list type="number">
/// <item>主源：<c>GET /repos/{owner}/{repo}/releases/latest</c> 的 302 —— 关掉自动重定向，只读响应头里的
/// <c>Location: .../releases/tag/vX.Y.Z</c>，拿到就取消请求（**不读 body**：ManifestLogWatcher 里
/// 踩过"HttpClient.Timeout 管不到读 body、静默挂 5 分钟"，这里连读都不读）；</item>
/// <item>备源：<c>api.github.com</c> 的同一端点（带 User-Agent，只取 tag_name）。</item>
/// </list>
///
/// 每源各自 5 秒硬超时；两个仓库**并行**发请求（串行最坏 2 × 10 秒，并行最坏 10 秒）。
/// GitHub 的 "latest" 语义天然排除草稿与预发布，所以不用自己判 prerelease。
/// 提示走系统通知（按钮回调经 <see cref="HandleNotificationArgument"/>，由 <c>Program</c> 的
/// NotificationInvoked 转入）；"同一版本只提示一次"按**维度**各自记账（GUI / 内核互不影响）。
/// </summary>
public static class UpdateService
{
    private const string Owner = "hazjy";
    private const string GuiRepo = "OSTGUI";
    private const string KernelRepo = "ZSteamTool";

    private static readonly string GuiReleasePageUrl = $"https://github.com/{Owner}/{GuiRepo}/releases/latest";
    private static readonly string KernelReleasePageUrl = $"https://github.com/{Owner}/{KernelRepo}/releases/latest";

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

    /// <summary>本机 GUI 版本（三段式；ProductVersion 里的 "+&lt;git hash&gt;" 不在这里）</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>启动后的自动检查：延迟 5 秒、不阻塞启动；只有发现新版本才弹通知，其余只进日志。</summary>
    public static async Task CheckOnStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var result = await CheckAllAsync(manual: false);
            if (result.Status == UpdateCheckStatus.Failed)
                LogService.Diag($"检查更新（自动）：未完成 —— {result.Message}");
        }
        catch (Exception ex)
        {
            LogService.Diag($"检查更新（自动）：异常 {ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// 检查一次（GUI + 内核）。<paramref name="manual"/> = 用户在「关于」里点的：失败要给出原因、
    /// 且**不受"同一版本只提示一次"的限制**；自动检查则相反（静默失败、同版本只提示一次）。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAllAsync(bool manual, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return new UpdateCheckResult(UpdateCheckStatus.Busy, CurrentVersion);

        try
        {
            var guiCurrent = CurrentVersion;
            // 未装内核 → null（该维度不参与）；kernelDiFailed 把"DI 还没就绪"与"未安装"分开
            var kernelCurrent = TryGetKernelVersion(out var kernelDiFailed);

            // 两个仓库并行取 tag（串行最坏 2 × 10 秒，并行最坏 10 秒）
            var guiTag = TryGetLatestTagAsync(GuiRepo, ct);
            var kernelTag = kernelCurrent == null
                ? Task.FromResult<string?>(null)
                : TryGetLatestTagAsync(KernelRepo, ct);
            await Task.WhenAll(guiTag, kernelTag);

            var gui = Judge(guiCurrent, guiTag.Result);
            var kernel = kernelCurrent == null ? null : Judge(kernelCurrent, kernelTag.Result);

            var errors = new List<string>();
            if (gui.Error != null) errors.Add($"GUI {gui.Error}");
            if (kernel?.Error != null) errors.Add($"内核 {kernel.Error}");
            var errorText = errors.Count == 0 ? null : string.Join("；", errors);

            // 两个维度都没判成功 = 整体失败；只有一个失败时，结论以成功的那维度为准（errorText 仍带出未确认的那半）
            if (gui.Error != null && (kernel == null || kernel.Error != null))
            {
                LogService.Diag($"检查更新：未完成 —— {errorText}");
                return new UpdateCheckResult(UpdateCheckStatus.Failed, guiCurrent, null, kernelCurrent, null, errorText);
            }

            if (gui.Latest == null && kernel?.Latest == null)
            {
                LogService.Event($"检查更新：已是最新（GUI {guiCurrent}{KernelNote(kernelCurrent, kernelDiFailed)}）");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, guiCurrent, null, kernelCurrent, null, errorText);
            }

            // 有新版：自动检查时"同一版本只提示一次"（手动不受限）
            var config = TryGetConfig();
            var plan = BuildToastPlan(gui, kernel, manual,
                                      NotifiedVersion(config, c => c.NotifiedUpdateVersion),
                                      NotifiedVersion(config, c => c.NotifiedKernelUpdateVersion));

            if (plan != null)
            {
                // 记账在"弹之前"写：手动弹过也算，避免下次启动自动再弹一遍同一版本
                // （ConfigService 的约定是"只写内存、退出时统一落盘"，所以硬杀进程可能没落盘 → 下次再提示一次）
                try
                {
                    if (config != null)
                    {
                        if (plan.HasGui && gui.Latest != null) config.Update(c => c.NotifiedUpdateVersion = gui.Latest);
                        if (plan.HasKernel && kernel?.Latest != null) config.Update(c => c.NotifiedKernelUpdateVersion = kernel.Latest);
                    }
                }
                catch (Exception ex) { LogService.Diag($"检查更新：写配置失败 {ex.GetType().Name}：{ex.Message}"); }

                LogService.Event($"检查更新：发现新版本（GUI {gui.Latest ?? "—"} / 内核 {kernel?.Latest ?? "—"}；" +
                                 $"本机 GUI {guiCurrent} / 内核 {kernelCurrent ?? "未装"}）");
                ToastService.ShowUpdatePlan(plan);
            }
            else
            {
                LogService.Event($"检查更新：新版本已提示过，本次静默（GUI {gui.Latest ?? "—"} / 内核 {kernel?.Latest ?? "—"}）");
            }

            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, guiCurrent, gui.Latest,
                                         kernelCurrent, kernel?.Latest, errorText);
        }
        catch (Exception ex)
        {
            LogService.Diag($"检查更新：未完成 —— {ex.GetType().Name}：{ex.Message}");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, CurrentVersion, null, null, null,
                                         $"{ex.GetType().Name}：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>通知参数（形如 <c>action=kernel-open</c>）解析出来的动作</summary>
    /// <param name="Url">要打开的发布页；<c>null</c> = 只记日志、不打开任何东西</param>
    /// <param name="Label">日志措辞用：打开的是"内核发布页 / 项目发布页"，或"用户选择暂不更新"</param>
    private sealed record ToastAction(string? Url, string Label);

    /// <summary>
    /// 参数 → 动作的**纯映射**（不碰 Process、不写日志，便于自检）：
    /// <c>kernel-open</c> → 内核发布页；<c>gui-open</c> 以及旧通知残留的 <c>update-open</c> → GUI 发布页；
    /// <c>update-skip</c> → 不打开（只记日志）；空参数 / 未识别的参数 → null（什么都不做）。
    /// </summary>
    private static ToastAction? MapNotificationArgument(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return null;

        if (argument.Contains("kernel-open", StringComparison.OrdinalIgnoreCase))
            return new ToastAction(KernelReleasePageUrl, "内核发布页");

        if (argument.Contains("gui-open", StringComparison.OrdinalIgnoreCase)
         || argument.Contains("update-open", StringComparison.OrdinalIgnoreCase))
            return new ToastAction(GuiReleasePageUrl, "项目发布页");

        if (argument.Contains("update-skip", StringComparison.OrdinalIgnoreCase))
            return new ToastAction(null, "暂不更新");

        return null;
    }

    /// <summary>
    /// 系统通知按钮回调（Program 订阅 NotificationInvoked 后转进来）—— 这里只负责执行；
    /// "哪个参数对应哪个动作"在 <see cref="MapNotificationArgument"/> 里（那份映射有自检兜着，
    /// 因为内核那条通知真机上很难点一次）。
    /// </summary>
    public static void HandleNotificationArgument(string? argument)
    {
        var action = MapNotificationArgument(argument);
        if (action == null) return;

        if (action.Url == null)
            LogService.Event($"检查更新：用户选择{action.Label}");   // 关闭由系统完成
        else
            OpenReleasePage(action.Url, action.Label);
    }

    private static void OpenReleasePage(string url, string what)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            LogService.Event($"检查更新：已打开{what}");
        }
        catch (Exception ex)
        {
            LogService.Diag($"检查更新：打开{what}失败 {ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// 「关于」弹窗里那行小字（手动检查的反馈）。抽成纯函数：文案的四种组合都能在自检里覆盖。
    /// </summary>
    public static string DescribeForDialog(UpdateCheckResult r) => r.Status switch
    {
        UpdateCheckStatus.Busy => "另一次检查正在进行…",
        UpdateCheckStatus.Failed => $"无法检查更新：{r.Message}",
        UpdateCheckStatus.UpToDate => r.Message == null ? "已是最新" : $"已是最新（{r.Message}）",
        // 有更新时不另起一对括号（两个括号连排很难看）：失败说明用分号接在同一句后面
        _ => DescribeFound(r) + (r.Message == null ? "" : $"；{r.Message}")
    };

    private static string DescribeFound(UpdateCheckResult r) => (r.KernelHasUpdate, r.GuiHasUpdate) switch
    {
        (true, true) => $"发现内核新版本 v{r.KernelLatest} 和 GUI 新版本 v{r.GuiLatest}（见系统通知）",
        (true, false) => $"发现内核新版本 v{r.KernelLatest}（见系统通知）",
        (false, true) => $"发现 GUI 新版本 v{r.GuiLatest}（见系统通知）",
        _ => "已是最新"
    };

    /// <summary>单个维度的判定结果</summary>
    private sealed record Dimension(string Current, string? Latest, string? Error);

    /// <summary>该维度是否有更新；取不到 tag / 解析不了算 Error（维度名由调用处拼进错误文案）</summary>
    private static Dimension Judge(string current, string? tag)
    {
        if (tag == null) return new Dimension(current, null, "未能取到版本号");

        var remote = VersionCompare.Parse(tag);
        if (remote == null) return new Dimension(current, null, $"远端版本无法解析：{tag}");

        var local = VersionCompare.Parse(current);
        if (local == null) return new Dimension(current, null, $"本机版本无法解析：{current}");

        return VersionCompare.IsRemoteNewer(remote, local)
            ? new Dimension(current, VersionCompare.ToText(remote), null)
            : new Dimension(current, null, null);
    }

    /// <summary>
    /// 生成要弹的那条通知（**纯函数，不联网不碰 UI**）：
    /// 自动检查时"已提示过的维度"不重复出现，两者都提示过 → 返回 null（静默）。
    /// 文案按既定句式：单个维度「发现 X 新版本 v{新}，当前版本 v{本机}」；两者都有则并列，按钮内核在前。
    /// </summary>
    private static UpdateToastPlan? BuildToastPlan(Dimension gui, Dimension? kernel, bool manual,
                                                   string guiNotified, string kernelNotified)
    {
        var showGui = gui.Latest != null
            && (manual || !string.Equals(gui.Latest, guiNotified, StringComparison.OrdinalIgnoreCase));
        var showKernel = kernel?.Latest != null
            && (manual || !string.Equals(kernel.Latest, kernelNotified, StringComparison.OrdinalIgnoreCase));

        if (!showGui && !showKernel) return null;

        var buttons = new List<UpdateToastButton>();
        string text;

        if (showKernel && showGui)
        {
            text = $"发现内核新版本 v{kernel!.Latest} 和 GUI 新版本 v{gui.Latest}，当前版本 v{kernel.Current} 和 v{gui.Current}";
            buttons.Add(new UpdateToastButton("前往内核发布页", "kernel-open"));
            buttons.Add(new UpdateToastButton("前往 GUI 发布页", "gui-open"));
        }
        else if (showKernel)
        {
            text = $"发现内核新版本 v{kernel!.Latest}，当前版本 v{kernel.Current}";
            buttons.Add(new UpdateToastButton("前往内核发布页", "kernel-open"));
        }
        else
        {
            text = $"发现 GUI 新版本 v{gui.Latest}，当前版本 v{gui.Current}";
            buttons.Add(new UpdateToastButton("前往 GUI 发布页", "gui-open"));
        }

        return new UpdateToastPlan(text, buttons, showKernel, showGui);
    }

    /// <summary>
    /// 纯逻辑自检（不联网、不弹通知）：四种文案与按钮、自动检查的降级、内核缺失、版本比较边界。
    /// 挂在工作区既有的 `--trainer-selftest` 链上（见 <c>App.xaml.cs</c>）。
    /// </summary>
    public static string SelfCheck()
    {
        var problems = new List<string>();
        void Expect(bool ok, string what) { if (!ok) problems.Add(what); }

        // ① 只有内核有更新
        var kernelOnly = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, "1.7.9", null, "1.1.3", "1.1.4");
        var p1 = BuildToastPlan(new Dimension("1.7.9", null, null),
                                new Dimension("1.1.3", "1.1.4", null), manual: true, "", "");
        Expect(p1?.Text == "发现内核新版本 v1.1.4，当前版本 v1.1.3", $"内核单维度文案不对：{p1?.Text}");
        Expect(p1?.Buttons.Count == 1 && p1.Buttons[0].Label == "前往内核发布页" && p1.Buttons[0].Argument == "kernel-open",
               "内核单维度按钮不对");
        Expect(DescribeForDialog(kernelOnly) == "发现内核新版本 v1.1.4（见系统通知）",
               $"内核单维度小字不对：{DescribeForDialog(kernelOnly)}");

        // ② 只有 GUI 有更新
        var guiOnly = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, "1.7.8", "1.7.9", "1.1.4");
        var p2 = BuildToastPlan(new Dimension("1.7.8", "1.7.9", null), null, manual: true, "", "");
        Expect(p2?.Text == "发现 GUI 新版本 v1.7.9，当前版本 v1.7.8", $"GUI 单维度文案不对：{p2?.Text}");
        Expect(p2?.Buttons.Count == 1 && p2.Buttons[0].Label == "前往 GUI 发布页" && p2.Buttons[0].Argument == "gui-open",
               "GUI 单维度按钮不对");
        Expect(DescribeForDialog(guiOnly) == "发现 GUI 新版本 v1.7.9（见系统通知）",
               $"GUI 单维度小字不对：{DescribeForDialog(guiOnly)}");

        // ③ 两者都有：文案并列、按钮两个且内核在前
        var both = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, "1.7.8", "1.7.9", "1.1.3", "1.1.4");
        var p3 = BuildToastPlan(new Dimension("1.7.8", "1.7.9", null),
                                new Dimension("1.1.3", "1.1.4", null), manual: true, "", "");
        Expect(p3?.Text == "发现内核新版本 v1.1.4 和 GUI 新版本 v1.7.9，当前版本 v1.1.3 和 v1.7.8",
               $"双维度文案不对：{p3?.Text}");
        Expect(p3?.Buttons.Count == 2 && p3.Buttons[0].Argument == "kernel-open" && p3.Buttons[1].Argument == "gui-open",
               "双维度按钮顺序/数量不对");
        Expect(DescribeForDialog(both).Contains("发现内核新版本 v1.1.4 和 GUI 新版本 v1.7.9"),
               $"双维度小字不对：{DescribeForDialog(both)}");

        // ④ 自动检查：内核那个版本已提示过 → 通知降级成只有 GUI
        var p4 = BuildToastPlan(new Dimension("1.7.8", "1.7.9", null),
                                new Dimension("1.1.3", "1.1.4", null), manual: false, "1.7.0", "1.1.4");
        Expect(p4?.Text == "发现 GUI 新版本 v1.7.9，当前版本 v1.7.8", $"降级文案不对：{p4?.Text}");
        Expect(p4?.Buttons.Count == 1 && p4.Buttons[0].Argument == "gui-open", "降级按钮不对");

        // ⑤ 自动检查：两个版本都提示过 → 不弹
        Expect(BuildToastPlan(new Dimension("1.7.8", "1.7.9", null),
                              new Dimension("1.1.3", "1.1.4", null), manual: false, "1.7.9", "1.1.4") == null,
               "两个维度都提示过时不该弹通知");

        // ⑥ 手动检查不受"已提示过"限制
        Expect(BuildToastPlan(new Dimension("1.7.8", "1.7.9", null),
                              new Dimension("1.1.3", "1.1.4", null), manual: true, "1.7.9", "1.1.4") != null,
               "手动检查不该被去重拦住");

        // ⑦ 内核未安装：只可能出 GUI 那条
        var p7 = BuildToastPlan(new Dimension("1.7.8", "1.7.9", null), null, manual: true, "", "");
        Expect(p7?.HasKernel == false && p7?.Buttons.Count == 1 && p7!.Buttons[0].Argument == "gui-open",
               "内核未安装时不该出现内核维度");

        // ⑧ 小字的无更新 / 失败形态
        Expect(DescribeForDialog(new UpdateCheckResult(UpdateCheckStatus.UpToDate, "1.7.9", null, "1.1.4")) == "已是最新",
               "无更新时小字必须是「已是最新」");
        Expect(DescribeForDialog(new UpdateCheckResult(UpdateCheckStatus.Failed, "1.7.9", null, null, null, "主源与备源均未取到版本号"))
                   == "无法检查更新：主源与备源均未取到版本号",
               "失败时小字不对");

        // ⑨ 版本比较边界（含内核这种三段式与四段式的等价）
        var v179 = VersionCompare.Parse("1.7.9");
        var v1710 = VersionCompare.Parse("v1.7.10");
        var v1140 = VersionCompare.Parse("1.1.4.0");
        var v114 = VersionCompare.Parse("v1.1.4");
        Expect(v179 != null && v1710 != null && VersionCompare.IsRemoteNewer(v1710, v179), "1.7.10 应比 1.7.9 新");
        Expect(v1140 != null && v114 != null && !VersionCompare.IsRemoteNewer(v1140, v114) && !VersionCompare.IsRemoteNewer(v114, v1140),
               "1.1.4.0 与 v1.1.4 应视为同版本");
        Expect(VersionCompare.ToText(v114!) == "1.1.4", $"ToText(v1.1.4) 应为 1.1.4，实际 {VersionCompare.ToText(v114!)}");
        Expect(VersionCompare.Parse("v1.1.4-beta") != null, "带后缀的 tag 应能解析出数字部分");

        // ⑩ 有更新 + 另一维度失败：失败说明用分号接上，不再另起一对括号（两个括号连排很难看）
        var partial = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, "1.7.9", null, "1.1.3", "1.1.4", "GUI 未能取到版本号");
        var dialog10 = DescribeForDialog(partial);
        Expect(dialog10 == "发现内核新版本 v1.1.4（见系统通知）；GUI 未能取到版本号",
               $"部分失败时小字不对：{dialog10}");
        Expect(!dialog10.Contains("）（"), $"小字里不该出现连排括号：{dialog10}");

        // ⑪ 通知按钮参数 → 动作映射 + 两个发布页 URL
        //    （内核那条通知真机上要"关 Steam 换 1.1.3 DLL"才点得到一次，只能靠自检兜住；
        //      URL 写错这类事故也是静默的：点了会去错页面）
        Expect(KernelReleasePageUrl == "https://github.com/hazjy/ZSteamTool/releases/latest"
               && GuiReleasePageUrl == "https://github.com/hazjy/OSTGUI/releases/latest"
               && KernelReleasePageUrl != GuiReleasePageUrl,
               $"发布页 URL 不对：内核 {KernelReleasePageUrl} / GUI {GuiReleasePageUrl}");

        var aKernel = MapNotificationArgument("action=kernel-open");
        Expect(aKernel?.Url == KernelReleasePageUrl && aKernel.Label == "内核发布页",
               $"kernel-open 映射不对：{aKernel?.Url} / {aKernel?.Label}");

        var aGui = MapNotificationArgument("action=gui-open");
        Expect(aGui?.Url == GuiReleasePageUrl && aGui.Label == "项目发布页",
               $"gui-open 映射不对：{aGui?.Url} / {aGui?.Label}");

        var aLegacy = MapNotificationArgument("action=update-open");   // 旧通知残留：当 gui-open 的别名
        Expect(aLegacy?.Url == GuiReleasePageUrl, $"update-open 别名映射不对：{aLegacy?.Url}");

        var aSkip = MapNotificationArgument("action=update-skip");
        Expect(aSkip != null && aSkip.Url == null && aSkip.Label == "暂不更新",
               $"update-skip 映射不对：{aSkip?.Url} / {aSkip?.Label}");

        Expect(MapNotificationArgument(null) == null
               && MapNotificationArgument("") == null
               && MapNotificationArgument("action=viewDetail") == null
               && MapNotificationArgument("garbage") == null,
               "空参数 / 未知参数不该产生任何动作");

        return problems.Count == 0 ? "" : string.Join("；", problems);
    }

    /// <summary>先主源（302 Location）后备源（API）；两者都失败返回 null</summary>
    private static async Task<string?> TryGetLatestTagAsync(string repo, CancellationToken ct)
    {
        // ① 主源：不跟随重定向，从 Location 里读 tag
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SourceTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{Owner}/{repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd("OSTGUI-UpdateCheck");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var location = response.Headers.Location?.ToString();
            if (location != null)
            {
                var tag = TagFromLocation.Match(location).Groups[1].Value;
                if (!string.IsNullOrEmpty(tag))
                {
                    LogService.Event($"检查更新：{repo} 主源命中 {tag}");
                    return tag;
                }
            }

            LogService.Diag($"检查更新：{repo} 主源没给 tag（HTTP {(int)response.StatusCode}）");
        }
        catch (Exception ex)
        {
            LogService.Diag($"检查更新：{repo} 主源失败 {ex.GetType().Name}：{ex.Message}");
        }

        // ② 备源：api.github.com（国内可能被拦，所以只当兜底）
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SourceTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd("OSTGUI-UpdateCheck");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                LogService.Diag($"检查更新：{repo} 备源 HTTP {(int)response.StatusCode}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var info = await JsonSerializer.DeserializeAsync(stream, AppJsonCompactContext.Default.ReleaseInfo, timeout.Token);
            if (!string.IsNullOrEmpty(info?.TagName))
            {
                LogService.Event($"检查更新：{repo} 备源命中 {info.TagName}");
                return info.TagName;
            }

            LogService.Diag($"检查更新：{repo} 备源没给 tag");
        }
        catch (Exception ex)
        {
            LogService.Diag($"检查更新：{repo} 备源失败 {ex.GetType().Name}：{ex.Message}");
        }

        return null;
    }

    /// <summary>某维度已提示过的版本号（配置不可用时按"没提示过"处理）</summary>
    private static string NotifiedVersion(ConfigService? config, Func<AppConfig, string> pick)
    {
        try { return config == null ? string.Empty : pick(config.Config); }
        catch { return string.Empty; }
    }

    /// <summary>
    /// 已部署内核的版本（读 DLL 的版本资源）；未安装 / 读不到 → null。
    /// <paramref name="diFailed"/> 分开"取不到"的两种成因：DI 还没就绪（true）vs 真的没装（false）——
    /// 日志里不能把前者冒充成"未装内核"，否则排查时会被带偏。
    /// </summary>
    private static string? TryGetKernelVersion(out bool diFailed)
    {
        diFailed = false;
        try { return App.Services.GetRequiredService<SteamDllService>().GetKernelVersion(); }
        catch (Exception ex)
        {
            diFailed = true;
            LogService.Diag($"检查更新：取内核版本失败（DI 未就绪？）{ex.GetType().Name}：{ex.Message}");
            return null;
        }
    }

    /// <summary>日志里"内核"那半句的补充说明：未装 / 读到版本 / 读取本身失败 三种状态分开</summary>
    private static string KernelNote(string? kernelCurrent, bool diFailed) =>
        kernelCurrent != null ? $"，内核 {kernelCurrent}"
        : diFailed ? "，内核版本未读到（DI 未就绪？）"
        : "，未装内核";

    /// <summary>取配置服务（启动早期 DI 可能还没建好 → 返回 null，调用处按"没有配置"处理）</summary>
    private static ConfigService? TryGetConfig()
    {
        try { return App.Services.GetRequiredService<ConfigService>(); }
        catch { return null; }
    }
}
