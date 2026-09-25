using System.Text.Json.Serialization;

namespace OSTGUI.Models;

/// <summary>
/// 修改器与游戏的绑定：游戏进程一出现就自动启动修改器，游戏退出就自动结束它。
/// 落盘在 <c>%LOCALAPPDATA%\OSTGUI\trainers\bindings.json</c>，由 GUI 与监控子进程共同读取
/// （监控每 2 秒看一次文件 mtime，变了才重载）。
/// </summary>
public class TrainerBinding
{
    /// <summary>游戏 AppID（库里选的；手选 exe 时可能为空）</summary>
    public string AppId { get; set; } = "";

    public string GameName { get; set; } = "";

    /// <summary>游戏主程序完整路径——监控就是拿它来找进程的</summary>
    public string GameExePath { get; set; } = "";

    /// <summary>修改器文件完整路径</summary>
    public string TrainerFilePath { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    [JsonIgnore]
    public string GameExeName => string.IsNullOrEmpty(GameExePath) ? "" : Path.GetFileName(GameExePath);

    [JsonIgnore]
    public string TrainerFileName => string.IsNullOrEmpty(TrainerFilePath) ? "" : Path.GetFileName(TrainerFilePath);

    /// <summary>绑定指向的文件是否都还在（游戏卸载/修改器被删就为 false）</summary>
    [JsonIgnore]
    public bool FilesExist => File.Exists(GameExePath) && File.Exists(TrainerFilePath);

    /// <summary>列表里的副标题：游戏主程序 → 修改器（缺文件时标出来，免得上转换器）</summary>
    [JsonIgnore]
    public string Subtitle =>
        $"{GameExeName} → {TrainerFileName}" + (FilesExist ? "" : "（文件缺失）");

    public TrainerBinding Clone() => new()
    {
        AppId = AppId,
        GameName = GameName,
        GameExePath = GameExePath,
        TrainerFilePath = TrainerFilePath,
        IsEnabled = IsEnabled,
    };
}
