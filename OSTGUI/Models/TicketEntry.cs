namespace OSTGUI.Models;

/// <summary>
/// D加密游戏 Ticket 条目模型
/// </summary>
public class TicketEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string AppId { get; set; } = string.Empty;
    public string GameName { get; set; } = "未知游戏";
    public string AppTicket { get; set; } = string.Empty;
    public string ETicket { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime AddedTime { get; set; } = DateTime.Now;
    public DateTime LastUsedTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 是否有有效的 AppTicket
    /// </summary>
    public bool HasAppTicket => !string.IsNullOrWhiteSpace(AppTicket);

    /// <summary>
    /// 是否有有效的 ETicket
    /// </summary>
    public bool HasETicket => !string.IsNullOrWhiteSpace(ETicket);

    /// <summary>
    /// 是否完整（两者都有）
    /// </summary>
    public bool IsComplete => HasAppTicket && HasETicket;

    /// <summary>
    /// 获取状态显示文本
    /// </summary>
    public string StatusDisplay => IsComplete ? "✅ 完整" : (HasAppTicket || HasETicket ? "⚠️ 部分" : "❌ 缺失");

    /// <summary>
    /// Denuvo 授权有效期为30分钟
    /// </summary>
    public static int DenuvoValidityMinutes => 30;

    /// <summary>
    /// 检查授权是否仍在30分钟窗口内
    /// </summary>
    public bool IsWithinValidityWindow => (DateTime.Now - LastUsedTime).TotalMinutes < DenuvoValidityMinutes;

    /// <summary>
    /// 获取剩余有效时间
    /// </summary>
    public string ValidityRemaining
    {
        get
        {
            var elapsed = DateTime.Now - LastUsedTime;
            var remaining = TimeSpan.FromMinutes(DenuvoValidityMinutes) - elapsed;
            if (remaining.TotalSeconds <= 0) return "已过期";
            return $"{remaining.Minutes}分{remaining.Seconds}秒";
        }
    }
}

/// <summary>
/// Ticket 方案/配置组 - 用于切换不同账号的授权
/// </summary>
public class TicketProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "默认方案";
    public string Description { get; set; } = string.Empty;
    public List<TicketEntry> Tickets { get; set; } = new();
    public bool IsActive { get; set; }
    public DateTime CreatedTime { get; set; } = DateTime.Now;
}