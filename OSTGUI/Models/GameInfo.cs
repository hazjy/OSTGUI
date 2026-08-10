namespace OSTGUI.Models;

/// <summary>
/// 游戏搜索/信息模型
/// </summary>
public class GameInfo
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string HeaderImage { get; set; } = string.Empty;
    public string CapsuleImage { get; set; } = string.Empty;
    public List<string> Developers { get; set; } = new();
    public List<string> Publishers { get; set; } = new();
    public bool IsFree { get; set; }
    public List<int> Dlc { get; set; } = new();
    public string ShortDescription { get; set; } = string.Empty;
    public Dictionary<string, DepotInfo> Depots { get; set; } = new();
}

public class DepotInfo
{
    public string DepotId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long MaxSize { get; set; }
    public bool IsDlc { get; set; }
    public string DlcAppId { get; set; } = string.Empty;
    public List<string> Manifests { get; set; } = new();
    public string DecryptionKey { get; set; } = string.Empty;
}

public class SearchResult
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}