namespace OSTGUI.Models;

/// <summary>
/// 修改器列表条目（来自 flingtrainer.com 的热门 / 新品 / 搜索）。
/// 不做封面图：只保留名字、详情页、日期，以及下载后的本地路径。
/// </summary>
public class TrainerInfo
{
    /// <summary>游戏名（已去掉站点标题里的 " Trainer" 后缀）</summary>
    public string GameName { get; set; } = "";

    /// <summary>详情页 URL（flingtrainer.com/trainer/...）</summary>
    public string PageUrl { get; set; } = "";

    /// <summary>更新时间（RSS 的 pubDate → yyyy.MM.dd；搜索页从日期节点拼）</summary>
    public string UpdateDate { get; set; } = "";

    /// <summary>下载完成后的本地文件路径（没下载就是空）</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>下载进度 0-100（仅下载中有效）</summary>
    public double DownloadProgress { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDownloading { get; set; }

    /// <summary>详情页里附件行的元信息（大小 / 下载次数），拿不到就是空</summary>
    public string SizeText { get; set; } = "";

    public bool IsDownloaded => !string.IsNullOrEmpty(LocalPath) && File.Exists(LocalPath);

    public string DisplayDate => string.IsNullOrEmpty(UpdateDate) ? "" : UpdateDate;
}
