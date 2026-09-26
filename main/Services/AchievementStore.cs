using System.Text.Json;

namespace OSTGUI.Services;

/// <summary>单条成就的留底记录（Name = schema 里的内部名，可直接喂给 ISteamUserStats013）</summary>
public sealed class AchievementRecord
{
    public string Name { get; set; } = "";
    public bool Achieved { get; set; }
    public long UnlockTime { get; set; }
}

/// <summary>一个游戏的成就留底文件</summary>
public sealed class AchievementFile
{
    public int SchemaVersion { get; set; } = 1;
    public string AppId { get; set; } = "";
    /// <summary>留底时的登录账号（切换账号后仅作提示，不做分账号目录）</summary>
    public string SteamId { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    /// <summary>local = 本工具编辑；steam = 从 Steam 读取；import = 导入</summary>
    public string Source { get; set; } = "local";
    public List<AchievementRecord> Achievements { get; set; } = new();
}

/// <summary>
/// 成就留底：%LOCALAPPDATA%\OSTGUI\achievements\&lt;appid&gt;.json
/// 这是唯一的可靠副本——假入库游戏的 Steam 端成就随时可能被服务端/内核清空（见 doc/GUI-事实考证.md）。
/// </summary>
public class AchievementStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI", "achievements");

    public static string PathFor(string appId) => Path.Combine(Dir, appId + ".json");

    public AchievementFile? Load(string appId)
    {
        try
        {
            var path = PathFor(appId);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AchievementFile>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            // 坏文件不删，改名留证
            LogService.Diag($"成就留底解析失败 appid={appId}: {ex.Message}");
            try { File.Move(PathFor(appId), PathFor(appId) + ".bad", true); } catch { }
            return null;
        }
    }

    /// <summary>原子写（tmp → Move）</summary>
    public bool Save(AchievementFile file)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            file.UpdatedAt = DateTimeOffset.UtcNow.ToString("o");
            var path = PathFor(file.AppId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
            File.Move(tmp, path, true);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Diag($"成就留底保存失败 appid={file.AppId}: {ex.Message}");
            return false;
        }
    }
}
