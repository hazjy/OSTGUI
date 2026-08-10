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
    public string UnlockerType { get; set; } = "ost"; // ost = OpenSteamTool
    private string _status = "ok";
    public string Status // ok, error, manifest
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ErrorLabel));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }
    private string _statusDetail = "";
    public string StatusDetail // 具体错误信息
    {
        get => _statusDetail;
        set
        {
            if (SetProperty(ref _statusDetail, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }
    public bool HasError => Status != "ok";
    public string ErrorLabel => Status switch
    {
        "error" => "Lua 错误",
        "manifest" => "Manifest 错误",
        _ => ""
    };
    public string StatusText => Status switch
    {
        "error" => $"Lua 错误: {StatusDetail}",
        "manifest" => $"清单缺失: {StatusDetail}",
        _ => "入库正常"
    };
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
    public string DepotKeySet { get; set; } = string.Empty; // depot key if present
    public List<string> ManifestIds { get; set; } = new();
    public bool HasDlc => DlcList.Count > 0;
    public int DlcCount => DlcList.Count;
    public DateTime AddedTime { get; set; }
    public DateTime LastModified { get; set; }

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
