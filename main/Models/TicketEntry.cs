namespace OSTGUI.Models;

/// <summary>
/// D加密游戏 Ticket 条目模型（仅保留导入/写注册表路径使用的成员）
/// </summary>
public class TicketEntry
{
    public string AppId { get; set; } = string.Empty;
    public string AppTicket { get; set; } = string.Empty;
    public string ETicket { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public DateTime LastUsedTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 是否有有效的 AppTicket
    /// </summary>
    public bool HasAppTicket => !string.IsNullOrWhiteSpace(AppTicket);

    /// <summary>
    /// 是否有有效的 ETicket
    /// </summary>
    public bool HasETicket => !string.IsNullOrWhiteSpace(ETicket);
}