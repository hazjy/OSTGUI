namespace OSTGUI.Services;

/// <summary>
/// 有限额清单源（目前只有 Ryuu）的用量口径（纯逻辑，可自检）。
///
/// 三条事实（2026-10-24 实测 / 用户口径）：
///  · Ryuu **没有**额度查询端点（<c>/api/usage</c>、<c>/api/quota</c>、<c>/api/me</c> 全 404）；
///  · 限额是"每 24 小时 50 次，**从第一次请求起算**"——不是自然日，所以窗口起点要存下来；
///  · 响应头 <c>x-ratelimit-limit</c> 与站点显示对不上，**不做展示**，只报"已用 N 次"。
/// </summary>
public static class SourceQuota
{
    /// <summary>窗口长度：24 小时（从第一次请求起算）。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>
    /// 按当前时间推进窗口：起点为空、解析不了、已超过 24 小时、或起点在未来（时钟被改）
    /// → 开新窗口（已用清零）。返回新的 (窗口起点, 已用次数)。
    /// </summary>
    public static (DateTime WindowStart, int Used) Normalize(DateTime now, string? windowStartIso, int used)
    {
        if (!DateTime.TryParse(windowStartIso, out var start) || now - start >= Window || now < start)
            return (now, 0);
        return (start, used < 0 ? 0 : used);
    }

    /// <summary>卡片上的文案：只报已用次数（上限不准，不写）。</summary>
    public static string Describe(int used) => $"已使用：{used} 次";

    /// <summary>自检：窗口内保留、跨 24 小时清零、空/脏起点清零、时钟倒退清零、文案。</summary>
    public static string SelfCheck()
    {
        var now = new DateTime(2026, 10, 24, 18, 0, 0);

        // 窗口内（11 小时前开的窗）：保留计数与起点
        var (start, used) = Normalize(now, now.AddHours(-11).ToString("o"), 7);
        if (used != 7) return $"窗口内被清零：{used}";
        if (start != now.AddHours(-11)) return "窗口起点被改写";

        // 刚过 24 小时：开新窗口
        (start, used) = Normalize(now, now.AddHours(-24).AddSeconds(-1).ToString("o"), 7);
        if (used != 0) return $"跨 24 小时未清零：{used}";
        if (start != now) return "跨窗口时起点未更新";

        // 空起点 / 脏数据
        (_, used) = Normalize(now, null, 5);
        if (used != 0) return "空起点未清零";
        (_, used) = Normalize(now, "not-a-date", 5);
        if (used != 0) return "脏起点未清零";

        // 起点在未来（系统时钟被改）：当新窗口
        (_, used) = Normalize(now, now.AddDays(1).ToString("o"), 5);
        if (used != 0) return "起点在未来时未清零";

        // 负值容错
        (_, used) = Normalize(now, now.AddHours(-1).ToString("o"), -3);
        if (used != 0) return "负计数未夹紧";

        // 文案
        if (Describe(8) != "已使用：8 次") return $"文案错误：{Describe(8)}";

        return "";
    }
}
