using CommunityToolkit.Mvvm.ComponentModel;

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

public class SearchResult : ObservableObject
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>卡片次级行（WinUI 的 {Binding} 不支持 StringFormat，故在此拼好）</summary>
    public string AppIdDisplay => $"AppID: {AppId}";

    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _thumbnail;

    /// <summary>
    /// 搜索结果缩略图。**只走内存、不落盘**（搜索结果是临时的，缓存反而占盘）；
    /// 由 SearchViewModel 在 UI 线程构造并赋值
    /// </summary>
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }
}