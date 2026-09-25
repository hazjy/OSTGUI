namespace OSTGUI.Models;

/// <summary>
/// 修改器列表条目：搜索结果的（只有名字/链接/日期），或已下载的（带本地路径与来源页）。
/// 不做封面图。
/// </summary>
public class TrainerInfo
{
    /// <summary>显示名：搜索结果里是游戏名，已下载里是 exe 文件名（= 绑定的名称）</summary>
    public string GameName { get; set; } = "";

    /// <summary>详情页 URL（flingtrainer.com/trainer/...）——「更新」要靠它取最新附件</summary>
    public string PageUrl { get; set; } = "";

    /// <summary>日期：搜索结果显示 RSS 的 pubDate；已下载显示文件修改日期</summary>
    public string UpdateDate { get; set; } = "";

    /// <summary>本地文件路径（未下载为空）</summary>
    public string LocalPath { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDownloading { get; set; }

    public string DisplayDate => UpdateDate;
}
