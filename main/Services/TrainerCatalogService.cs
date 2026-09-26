using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// flingtrainer.com 的取数：**搜索走站点官方 RSS**，只有详情页才用正则取附件直链。
///
/// 搜索：<c>?s=&lt;词&gt;&feed=rss2</c>（WordPress 自带 feed）
/// - 好处：feed 里只有正文条目，**天然不受侧栏"热门/最新/相关"小工具污染**
///   （之前扒 HTML 时 ?s=elden 真结果 2 条却抓到 16 条、?s=peak 更是把 12 条推荐当结果）；
/// - 实测与页面结果区一致：elden 2/2、crimson 3/3、wukong 1/1（2026-09-25）；
/// - stdlib 的 XDocument 解析，没有正则脆性。
///
/// 详情页：附件行是 <c>&lt;a class="attachment-link" href=… title=…&gt;</c>，
/// 属性顺序不固定（实测 href 在前、class 在后）→ 先切整个标签再逐个取属性。
/// （首页「热门」小工具与「新品」feed 曾实现并实测可用，2026-09-25 按需求砍掉，需要时看 git 历史。）
///
/// 站点在 Cloudflare 后面且**间歇性连不上**（同一条命令时而 200 时而"基础连接已经关闭"）→ 抓取重试一次。
/// </summary>
public class TrainerCatalogService
{
    private const string BaseUrl = "https://flingtrainer.com";
    private readonly HttpClient _http;

    public TrainerCatalogService(HttpClient http) => _http = http;

    /// <summary>搜索：官方 RSS（只留 /trainer/ 条目）</summary>
    public async Task<List<TrainerInfo>> SearchAsync(string query, int count = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TrainerInfo>();

        var xml = await GetAsync($"{BaseUrl}/?s={Uri.EscapeDataString(query.Trim())}&feed=rss2", ct);
        var result = new List<TrainerInfo>();

        try
        {
            foreach (var item in XDocument.Parse(xml).Descendants("item"))
            {
                var title = ((string?)item.Element("title") ?? "").Trim();
                var link = ((string?)item.Element("link") ?? "").Split('?')[0];   // 去掉 rss 的 utm 尾巴
                if (title.Length == 0 || !link.Contains("/trainer/", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(new TrainerInfo
                {
                    GameName = StripTrainerSuffix(title),
                    PageUrl = link,
                    UpdateDate = FormatDate((string?)item.Element("pubDate") ?? ""),
                });
                if (result.Count >= count) break;
            }
        }
        catch (Exception ex)
        {
            LogService.Diag($"trainer 搜索 RSS 解析失败「{query}」: {ex.Message}");
        }

        if (result.Count == 0)
            LogService.Event($"trainer 搜索「{query}」无结果（feed 长度 {xml.Length}）");
        return result;
    }

    /// <summary>详情页里附件行的直链（含 class="attachment-link" 的那个 a 标签的 href 与 title）</summary>
    public async Task<(string Url, string FileName)?> GetDownloadAsync(string pageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;

        var html = await GetAsync(pageUrl, ct);
        var tag = Regex.Match(html, "<a\\b[^>]*class=\"[^\"]*attachment-link[^\"]*\"[^>]*>", RegexOptions.IgnoreCase).Value;
        if (tag.Length == 0) return null;

        var href = Regex.Match(tag, "href=\"([^\"]+)\"").Groups[1].Value;
        if (href.Length == 0) return null;
        if (href.StartsWith("//")) href = "https:" + href;
        else if (href.StartsWith('/')) href = BaseUrl + href;

        // title 属性就是文件名（如 Elden.Ring.v1.02-v1.16.1.Plus.35.Trainer-FLiNG），没有就退回 URL 末段
        var title = Regex.Match(tag, "title=\"([^\"]+)\"").Groups[1].Value;
        if (title.Length == 0) title = Uri.UnescapeDataString(href.Split('/').Last());
        return (WebUtility.HtmlDecode(href), Sanitize(title));
    }

    /// <summary>
    /// 抓一次；失败重试一次（实测 flingtrainer 会间歇性连不上——同一条命令时而成功时而报
    /// "基础连接已经关闭"，所以必须重试，重试还失败就抛给调用方显示"网络/站点不可用"）
    /// </summary>
    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    LogService.Diag($"trainer 抓取 HTTP {(int)resp.StatusCode} {url}（{text.Length} 字符）");
                return text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                LogService.Diag($"trainer 抓取失败（第 {attempt + 1} 次）{url}: {ex.Message}");
                if (attempt == 0) await Task.Delay(1500, ct);
            }
        }
        throw last ?? new IOException($"抓取失败: {url}");
    }

    private static string StripTrainerSuffix(string name)
    {
        const string suffix = " Trainer";
        return name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;
    }

    /// <summary>去掉文件名里的非法字符（保留字母数字与 . _ - 空格）</summary>
    private static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToHashSet();
        var cleaned = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "trainer.exe" : cleaned;
    }

    private static string FormatDate(string pubDate) =>
        DateTime.TryParse(pubDate, out var dt) ? dt.ToString("yyyy.MM.dd") : "";
}
