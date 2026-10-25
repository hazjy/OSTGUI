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

    /// <summary>可选 build 列表（= appinfo 的 depots.branches；含 Valve 保留的旧版本分支）</summary>
    public List<BuildBranch> Branches { get; set; } = new();
}

/// <summary>一个可选 build（对应 appinfo 里 depots.branches.&lt;名字&gt;）。</summary>
public class BuildBranch
{
    public string Name { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public class DepotInfo
{
    public string DepotId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long MaxSize { get; set; }
    public bool IsDlc { get; set; }
    public string DlcAppId { get; set; } = string.Empty;
    public List<string> Manifests { get; set; } = new();

    /// <summary>
    /// 分支名 → manifest gid（appinfo 的 `depots.&lt;id&gt;.manifests` 本来就是按分支的字典）。
    /// 版本切换器用它算"某个 build 下每个 depot 用哪个 gid"；`Manifests` 仍只放 public 的，入库链路不受影响。
    /// </summary>
    public Dictionary<string, string> ManifestsByBranch { get; set; } = new();

    public string DecryptionKey { get; set; } = string.Empty;
}

[WinRT.GeneratedBindableCustomProperty]
public partial class SearchResult : ObservableObject
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