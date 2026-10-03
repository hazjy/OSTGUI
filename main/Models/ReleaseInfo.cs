using System.Text.Json.Serialization;

namespace OSTGUI.Models;

/// <summary>
/// GitHub Releases API 的响应（**兜底数据源**）：只取用得到的三个字段。
/// 主源走 <c>releases/latest</c> 的 302 <c>Location</c>，不解析 JSON；本模型只在主源失败时用。
/// ⚠️ AOT：必须走源生成 context（<c>AppJsonCompactContext</c>），别用反射序列化（REF-AOT适配 §4）。
/// </summary>
public class ReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("published_at")]
    public string PublishedAt { get; set; } = string.Empty;
}
