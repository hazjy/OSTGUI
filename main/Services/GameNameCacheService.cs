using System.Text.Json;

namespace OSTGUI.Services;

/// <summary>
/// 游戏名称缓存服务 - 本地持久化，默认 30 天有效
/// </summary>
public class GameNameCacheService
{
    private readonly Dictionary<string, (string name, DateTime time)> _nameCache = new();
    private readonly string _cachePath;

    private const int CacheTtlDays = 30;

    private class CacheEntry
    {
        public string Name { get; set; } = "";
        public DateTime Time { get; set; }
    }

    public GameNameCacheService()
    {
        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OSTGUI", "name_cache.json");
        LoadCache();
    }

    /// <summary>
    /// 尝试读取未过期的名称缓存
    /// </summary>
    public bool TryGet(string appId, out string name)
    {
        if (_nameCache.TryGetValue(appId, out var cached) &&
            DateTime.Now - cached.time < TimeSpan.FromDays(CacheTtlDays))
        {
            name = cached.name;
            return true;
        }
        name = "";
        return false;
    }

    /// <summary>
    /// 写入名称缓存并落盘
    /// </summary>
    public void Set(string appId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        _nameCache[appId] = (name, DateTime.Now);
        SaveCache();
    }

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                if (data != null)
                {
                    foreach (var (key, entry) in data)
                    {
                        // 只加载未过期的缓存
                        if (!string.IsNullOrEmpty(entry.Name) &&
                            DateTime.Now - entry.Time < TimeSpan.FromDays(CacheTtlDays))
                            _nameCache[key] = (entry.Name, entry.Time);
                    }
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("加载名称缓存失败: " + ex.Message); }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var data = _nameCache.ToDictionary(k => k.Key, v => new CacheEntry { Name = v.Value.name, Time = v.Value.time });
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(data));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("保存名称缓存失败: " + ex.Message); }
    }
}
