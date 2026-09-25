using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 绑定的落盘（<c>%LOCALAPPDATA%\OSTGUI\trainers\bindings.json</c>）。
/// GUI 与监控子进程共用这一份：GUI 写、监控按 mtime 热重载，所以只有一个写者、不需要额外同步。
/// </summary>
public class TrainerBindingService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string BindingsPath { get; } = Path.Combine(TrainerDownloadService.TrainerDir, "bindings.json");

    public List<TrainerBinding> Load()
    {
        try
        {
            if (!File.Exists(BindingsPath)) return new List<TrainerBinding>();
            var json = File.ReadAllText(BindingsPath);
            return JsonSerializer.Deserialize<List<TrainerBinding>>(json, JsonOptions) ?? new List<TrainerBinding>();
        }
        catch (Exception ex)
        {
            // 配置坏了别丢用户数据：留一份 .bad 供人工看，然后当空处理
            LogService.AddAppLog($"trainer 绑定读取失败: {ex.Message}");
            try { File.Copy(BindingsPath, BindingsPath + ".bad", overwrite: true); } catch { }
            return new List<TrainerBinding>();
        }
    }

    public void Save(List<TrainerBinding> bindings)
    {
        try
        {
            Directory.CreateDirectory(TrainerDownloadService.TrainerDir);
            var temp = BindingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(bindings, JsonOptions));
            File.Move(temp, BindingsPath, overwrite: true);   // 原子替换，监控读到的永远是一份完整文件
            LogService.AddAppLog($"trainer 绑定已保存 {bindings.Count} 条（启用 {bindings.Count(b => b.IsEnabled)}）");
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"trainer 绑定保存失败: {ex.Message}");
        }
    }
}
