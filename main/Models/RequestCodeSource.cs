namespace OSTGUI.Models;

/// <summary>
/// 内核「请求码源」——由 GUI 渲染成 &lt;lua 目录&gt;\manifest.lua，供内核运行时应答
/// Steam 的清单请求码（<c>GetManifestRequestCode</c>）。
///
/// <para><b>与 <see cref="ManifestSource"/> 的区别（别混）</b>：那一份是<b>入库时</b>从 MHub
/// 下载 .manifest 文件用的（GUI 侧、一次性、有 API Key）；本类型是<b>内核运行时</b>每次被
/// Steam 要码都要跑一遍的（无密钥、有 UA/端点差异），最终产物是 manifest.lua。两者消费方、
/// 生命周期、失败影响都不同，所以没有复用同一个模型。</para>
///
/// <para>URL 模板占位符：<c>{gid}</c> = manifest GID；<c>{depotid}</c> = Depot ID
/// （20770407 这类双参数源需要，缺失时该源会被跳过——内核只在 app_id 与 depot_id 都非 0 时
/// 才调 fetch_manifest_code_ex）。</para>
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public partial class RequestCodeSource
{
    public const string PlaceholderGid = "{gid}";
    public const string PlaceholderDepotId = "{depotid}";

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>一句话说明，显示在设置页名称后面</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>URL 模板（见类注释）；由预置表维护，界面不可改——上游端点会变，跟着版本走</summary>
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>该源要求的 User-Agent（空 = 不发这个头）。manifestdex 强制 ManifestDeX/1.0，其余写了会 403</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>响应体是 JSON（{"content":"&lt;码&gt;"}）而非裸十进制数字</summary>
    public bool JsonResponse { get; set; }

    /// <summary>URL 模板用到了 {depotid}（为真时 fetch_manifest_code(gid) 那一路会跳过它）</summary>
    public bool NeedsDepotId => UrlTemplate.Contains(PlaceholderDepotId);

    /// <summary>是否参与级联（全关 = 生成短路版 manifest.lua，不注入任何码）</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>级联顺序，小的在前（预置表定序，界面排序即此顺序）</summary>
    public int Priority { get; set; } = 100;

    /// <summary>「测活」结果文案（空 = 未测过）。只是界面状态，不写进 config.json</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string TestResultText { get; set; } = string.Empty;

    /// <summary>
    /// 预置请求码源。顺序即级联顺序，2026-10-06 全部实测可拿 CDN 清单
    /// （481 / 付费 Sifu 2138711 / 4157741 三组真实 depot+gid）。
    /// 前三家同一时刻返回同一个值（同一份码源，只算传输层冗余），20770407 是另一个号池。
    /// </summary>
    public static List<RequestCodeSource> GetPresetSources() => new()
    {
        new()
        {
            Id = "20770407", Name = "20770407", Description = "独立号池",
            UrlTemplate = "https://20770407.xyz/manifest/{depotid}/{gid}",
            Priority = 1
        },
        new()
        {
            Id = "manifestdex", Name = "ManifestDeX", Description = "需专用 UA",
            UrlTemplate = "https://manifest.manifestdex.com/{gid}",
            UserAgent = "ManifestDeX/1.0",
            Priority = 2
        },
        new()
        {
            Id = "wudrm", Name = "wudrm", Description = "与 steamrun 同源",
            UrlTemplate = "http://gmrc.wudrm.com/manifest/{gid}",
            Priority = 3
        },
        new()
        {
            Id = "steamrun", Name = "steamrun", Description = "JSON 响应",
            UrlTemplate = "https://manifest.steam.run/api/manifest/{gid}",
            JsonResponse = true,
            Priority = 4
        },
    };
}
