namespace OSTGUI.Services;

/// <summary>
/// 需要每日额度的手动密钥源（目前只有 Ryuu）的额度口径（纯逻辑，可自检）。
///
/// 为什么自己记：Ryuu **没有**额度查询端点（<c>/api/usage</c>、<c>/api/quota</c>、<c>/api/me</c> 全 404），
/// 只在响应头里给 <c>x-ratelimit-limit</c>（已用多少得自己数）。所以界面上一律写
/// "上限（服务器返回）"，不冒充官方剩余量。
/// </summary>
public static class SourceQuota
{
    /// <summary>跨日归零：存的是别的日期就把已用清零。</summary>
    public static (string Date, int Used) Normalize(string today, string? storedDate, int used)
    {
        if (!string.Equals(today, storedDate, StringComparison.Ordinal))
            return (today, 0);
        return (today, used < 0 ? 0 : used);
    }

    /// <summary>还能不能用。<paramref name="limit"/> ≤ 0 表示"服务器没告诉我们上限"→ 放行，交给服务端判。</summary>
    public static bool CanUse(int used, int limit) => limit <= 0 || used < limit;

    /// <summary>卡片上的文案。</summary>
    public static string Describe(int used, int limit) =>
        limit <= 0 ? $"今日已用 {used} / 上限未知" : $"今日已用 {used} / 上限 {limit}";

    /// <summary>自检：跨日归零、上限未知放行、达上限拒绝、文案两种形态。</summary>
    public static string SelfCheck()
    {
        // 跨日
        var (date, used) = Normalize("2026-10-25", "2026-10-24", 7);
        if (date != "2026-10-25" || used != 0) return $"跨日未归零：{date}/{used}";
        // 同日保留
        (date, used) = Normalize("2026-10-24", "2026-10-24", 7);
        if (date != "2026-10-24" || used != 7) return $"同日被清零：{date}/{used}";
        // 首次使用（没有历史日期）
        (date, used) = Normalize("2026-10-24", null, 3);
        if (used != 0) return "无历史日期时未归零";
        // 负数容错
        (_, used) = Normalize("2026-10-24", "2026-10-24", -5);
        if (used != 0) return "负的已用数未夹紧";

        // 上限判定
        if (!CanUse(29, 30)) return "未达上限却拒绝使用";
        if (CanUse(30, 30)) return "已达上限仍允许使用";
        if (!CanUse(999, 0)) return "上限未知时不该拦";

        // 文案
        if (Describe(3, 30) != "今日已用 3 / 上限 30") return $"文案错误：{Describe(3, 30)}";
        if (Describe(3, 0) != "今日已用 3 / 上限未知") return $"未知上限文案错误：{Describe(3, 0)}";

        return "";
    }
}
