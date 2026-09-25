using System.Text.Json.Serialization;

namespace OSTGUI.Models;

/// <summary>
/// 进程绑定：游戏进程一出现就自动启动对应修改器，游戏退出就自动结束它。
/// 落盘在 <c>%LOCALAPPDATA%\OSTGUI\bindings.json</c>（与 trainers.json 同目录），
/// 由 GUI 与监控子进程共同读取（监控每 2 秒看一次文件 mtime，变了才重载）。
///
/// **记的是修改器「名称」而不是路径**（名称 = 索引里的 Name = 下载时的附件标题：
/// 既对应界面的「复制名称」，又不会因为更新换了文件名/路径而失效——更新时只需改索引，
/// 绑定不用动）。
/// </summary>
public class TrainerBinding
{
    /// <summary>修改器名称（= 已下载列表里显示的名字），实际路径按它去索引查</summary>
    public string TrainerName { get; set; } = "";

    /// <summary>游戏名（显示用；手选 exe 时取 exe 文件名）</summary>
    public string GameName { get; set; } = "";

    /// <summary>游戏主程序完整路径——监控就是拿它来找进程的</summary>
    public string GameExePath { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    [JsonIgnore]
    public string GameExeName => string.IsNullOrEmpty(GameExePath) ? "" : Path.GetFileName(GameExePath);

    /// <summary>按名称查出来的修改器实际路径（显示与启动用；查不到为空）</summary>
    [JsonIgnore]
    public string ResolvedPath { get; set; } = "";

    [JsonIgnore]
    public string TrainerFileName => string.IsNullOrEmpty(ResolvedPath) ? TrainerName : Path.GetFileName(ResolvedPath);

    /// <summary>游戏 exe 与修改器是否都在（修改器被删/游戏被卸载就为 false）</summary>
    [JsonIgnore]
    public bool FilesExist => File.Exists(GameExePath) && ResolvedPath.Length > 0 && File.Exists(ResolvedPath);

    /// <summary>列表里的副标题：游戏主程序 → 修改器（缺文件时标出来，免得上转换器）</summary>
    [JsonIgnore]
    public string Subtitle =>
        $"{GameExeName} → {TrainerFileName}" + (FilesExist ? "" : "（文件缺失）");
}
