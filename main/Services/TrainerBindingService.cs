using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 绑定的落盘（<c>%LOCALAPPDATA%\OSTGUI\bindings.json</c>，与 trainers.json 同目录，即"绑定的索引"）。
/// GUI 与监控子进程共用这一份：GUI 写、监控按 mtime 热重载，所以只有一个写者、不需要额外同步。
/// 绑定里存的是**修改器名称**，实际路径按名称去修改器索引查（见 TrainerDownloadService.FindTrainerPath）。
/// </summary>
public class TrainerBindingService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string BindingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSTGUI", "bindings.json");

    public List<TrainerBinding> Load()
    {
        var list = new List<TrainerBinding>();
        try
        {
            if (!File.Exists(BindingsPath)) return list;
            list = JsonSerializer.Deserialize<List<TrainerBinding>>(File.ReadAllText(BindingsPath), JsonOptions)
                   ?? new List<TrainerBinding>();
        }
        catch (Exception ex)
        {
            // 配置坏了别丢用户数据：留一份 .bad 供人工看，然后当空处理
            LogService.Diag($"trainer 绑定读取失败: {ex.Message}");
            try { File.Copy(BindingsPath, BindingsPath + ".bad", overwrite: true); } catch { }
            return new List<TrainerBinding>();
        }

        // 名称 → 实际路径（供显示与启动；名称对应的文件被删了就是空的，靠 FilesExist 标出来）
        foreach (var binding in list)
            binding.ResolvedPath = TrainerDownloadService.FindTrainerPath(binding.TrainerName) ?? "";
        return list;
    }

    public void Save(List<TrainerBinding> bindings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BindingsPath)!);
            var temp = BindingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(bindings, JsonOptions));
            File.Move(temp, BindingsPath, overwrite: true);   // 原子替换，监控读到的永远是一份完整文件
            LogService.Diag($"trainer 绑定已保存 {bindings.Count} 条（启用 {bindings.Count(b => b.IsEnabled)}）");
        }
        catch (Exception ex)
        {
            LogService.Diag($"trainer 绑定保存失败: {ex.Message}");
        }
    }
}
