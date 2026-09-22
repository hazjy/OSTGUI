using CommunityToolkit.Mvvm.ComponentModel;

namespace OSTGUI.Models;

/// <summary>
/// 入库游戏条目模型
/// </summary>
public class DlcInfo
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = "未知 DLC";
    public string Status { get; set; } = "";
    public bool IsInstalled => Status == "installed";
    public string StatusText => IsInstalled ? "已入库" : "未入库";
}

public class LibraryItem : ObservableObject
{
    public string AppId { get; set; } = string.Empty;
    public string GameName { get; set; } = "未知游戏";
    public string FileName { get; set; } = string.Empty;

    /// <summary>卡片次级行（WinUI 的 {Binding} 不支持 StringFormat，故在此拼好）</summary>
    public string AppIdDisplay => $"AppID: {AppId}";

    public string UnlockerType { get; set; } = "ost"; // ost = OpenSteamTool
    private string _versionMode = "auto";
    public string VersionMode // auto, fixed
    {
        get => _versionMode;
        set
        {
            if (SetProperty(ref _versionMode, value))
            {
                OnPropertyChanged(nameof(VersionModeText));
                OnPropertyChanged(nameof(VersionModeDisplay));
            }
        }
    }
    public List<DlcInfo> DlcList { get; set; } = new();
    public List<string> InstalledAppIds { get; set; } = new();
    public bool HasDlc => DlcList.Count > 0;
    public int DlcCount => DlcList.Count;
    public DateTime AddedTime { get; set; }
    public DateTime LastModified { get; set; }

    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _cover;

    /// <summary>
    /// 卡片封面（由 LibraryViewModel 在 UI 线程从 CoverImageService 给的本地文件路径构造；
    /// 必须在 UI 线程赋值——BitmapImage 是 DependencyObject）
    /// </summary>
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Cover
    {
        get => _cover;
        set => SetProperty(ref _cover, value);
    }

    /// <summary>
    /// 当前 <see cref="Cover"/> 位图是按多宽（逻辑像素）解码的，0 = 还没解码。
    /// 列表与网格两档视图的解码宽度不同（120 / 200），切视图时靠它判断要不要重建位图——
    /// 不做这个判断就会把列表尺寸的位图塞进网格卡片里显示，照样是糊的
    /// </summary>
    internal int CoverDecodeWidth { get; set; }

    /// <summary>
    /// 获取版本模式显示文本（无 emoji）
    /// </summary>
    public string VersionModeText => VersionMode switch
    {
        "fixed" => "锁定版本",
        "auto" => "自动更新",
        _ => "未知"
    };

    /// <summary>
    /// 获取版本模式显示文本（带 emoji）
    /// </summary>
    public string VersionModeDisplay => VersionMode switch
    {
        "fixed" => "🔒 固定版本",
        "auto" => "🔄 自动更新",
        _ => "未知"
    };

    /// <summary>
    /// 获取版本模式切换后的提示文本
    /// </summary>
    public string GetToggleStatusMessage()
    {
        return VersionMode switch
        {
            "fixed" => $"AppID {AppId} ({GameName}) 已锁定为固定版本",
            "auto" => $"AppID {AppId} ({GameName}) 已切换为自动更新",
            _ => $"AppID {AppId} ({GameName}) 状态未知"
        };
    }
}
