using System.Net;
using System.Text.RegularExpressions;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// flingtrainer.com 的目录抓取：搜索 + 详情页附件直链。
/// （首页「热门」与 RSS「新品」曾实现并实测可用，2026-09-25 按需求砍掉，需要时见 git 历史恢复。）
///
/// 为什么是正则而不是 HTML 解析库：本机 nuget 不通，加不了 HtmlAgilityPack（Fluent-Steam-Lua 用的就是它）；
/// 这里的正则只锚定站点固定的 class/路径。站点改版时表现是"条目为空"，日志里会留 HTTP 状态与页面长度。
/// </summary>
public class TrainerCatalogService
{
    private const string BaseUrl = "https://flingtrainer.com";
    private readonly HttpClient _http;

    public TrainerCatalogService(HttpClient http) => _http = http;

    /// <summary>结果区的文章块（侧栏/推荐用的是别的 class，不在这里）</summary>
    private const string ArticlePattern = "<article[^>]*class=\"[^\"]*\\bpost-(?:standard|list)\\b[^\"]*\"[^>]*>(.*?)</article>";

    /// <summary>文章块里的标题链接（h2.post-title &gt; a）</summary>
    private const string TitleLinkPattern = "<h2[^>]*class=\"[^\"]*post-title[^\"]*\"[^>]*>\\s*<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>";

    /// <summary>
    /// 搜索：**只解析结果区的文章块**（<c>article.post-standard</c> / <c>post-list</c>）。
    ///
    /// 2026-09-25 修正：原来抓整页所有 <c>/trainer/</c> 链接，会把侧栏的"热门/最新/相关"小工具一起捞进来——
    /// 实测 <c>?s=elden</c> 真结果只有 2 条却抓到 16 条，<c>?s=peak</c> 真结果 0 条却抓到 12 条（全是推荐）。
    /// </summary>
    public async Task<List<TrainerInfo>> SearchAsync(string query, int count = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TrainerInfo>();

        var html = await GetAsync($"{BaseUrl}/?s={Uri.EscapeDataString(query.Trim())}", ct);
        var result = new List<TrainerInfo>();

        foreach (Match block in Regex.Matches(html, ArticlePattern, RegexOptions.Singleline))
        {
            var body = block.Groups[1].Value;
            var link = Regex.Match(body, TitleLinkPattern, RegexOptions.Singleline);

            string url, name;
            if (link.Success)
            {
                url = WebUtility.HtmlDecode(link.Groups[1].Value);
                name = Clean(Regex.Replace(link.Groups[2].Value, "<[^>]+>", "").Trim());
            }
            else
            {
                // 兜底：块内第一个 /trainer/ 链接（仍限定在这一块里，不会带进侧栏）
                var any = Regex.Match(body, "href=\"(" + BaseUrl + "/trainer/[^\"]+)\"");
                if (!any.Success) continue;
                url = WebUtility.HtmlDecode(any.Groups[1].Value);
                name = "";
            }

            if (url.Length == 0 || result.Any(r => r.PageUrl == url)) continue;
            if (name.Length == 0) name = Uri.UnescapeDataString(url.TrimEnd('/').Split('/').Last());

            result.Add(new TrainerInfo { GameName = StripTrainerSuffix(name), PageUrl = url });
            if (result.Count >= count) break;
        }

        if (result.Count == 0)
            LogService.AddAppLog($"trainer 搜索「{query}」无结果（页面长度 {html.Length}）");
        return result;
    }

    /// <summary>详情页里附件行的直链（含 class="attachment-link" 的那个 a 标签的 href 与 title）</summary>
    public async Task<(string Url, string FileName)?> GetDownloadAsync(string pageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;

        var html = await GetAsync(pageUrl, ct);
        // 属性顺序不固定（实测 href 在 class 前、title 在中间），所以先切出整个标签再逐个取属性
        var tag = Regex.Match(html, "<a\\b[^>]*class=\"[^\"]*attachment-link[^\"]*\"[^>]*>", RegexOptions.IgnoreCase).Value;
        if (tag.Length == 0) return null;

        var href = Regex.Match(tag, "href=\"([^\"]+)\"").Groups[1].Value;
        if (href.Length == 0) return null;
        if (href.StartsWith("//")) href = "https:" + href;
        else if (href.StartsWith('/')) href = BaseUrl + href;

        // title 属性就是文件名（如 Elden.Ring.v1.02-v1.16.1.Plus.35.Trainer-FLiNG），没有就退回 URL 末段
        var title = Regex.Match(tag, "title=\"([^\"]+)\"").Groups[1].Value;
        if (title.Length == 0) title = Uri.UnescapeDataString(href.Split('/').Last());
        return (href, Sanitize(title));
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
                    LogService.AddAppLog($"trainer 抓取 HTTP {(int)resp.StatusCode} {url}（{text.Length} 字符）");
                return text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                LogService.AddAppLog($"trainer 抓取失败（第 {attempt + 1} 次）{url}: {ex.Message}");
                if (attempt == 0) await Task.Delay(1500, ct);
            }
        }
        throw last ?? new IOException($"抓取失败: {url}");
    }

    private static string Clean(string html) =>
        WebUtility.HtmlDecode(html).Replace('\u2019', '\'').Replace('\u2018', '\'').Trim();

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
}
