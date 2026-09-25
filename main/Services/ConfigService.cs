using System.Text.Json;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// 应用配置管理服务
/// </summary>
public class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSTGUI");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private AppConfig _config = AppConfig.GetDefault();

    public AppConfig Config => _config;

    /// <summary>
    /// 配置是否已从磁盘加载完成（加载前禁止写回，防止默认值覆盖真实配置）
    /// </summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    /// 加载配置，不存在则创建默认
    /// </summary>
    public async Task<AppConfig> LoadAsync()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            if (File.Exists(ConfigPath))
            {
                var json = await File.ReadAllTextAsync(ConfigPath).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded != null)
                {
                    // 合并默认值，确保新字段有默认值
                    var defaults = AppConfig.GetDefault();
                    MergeDefaults(loaded, defaults);
                    _config = loaded;
                    IsLoaded = true;
                    return _config;
                }
            }
            else
            {
                await SaveAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 加载失败使用默认值
            _config = AppConfig.GetDefault();
        }
        IsLoaded = true;
        return _config;
    }

    /// <summary>
    /// 保存配置
    /// </summary>
    public async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(_config, JsonOptions);
            await File.WriteAllTextAsync(ConfigPath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新配置并保存
    /// </summary>
    public async Task UpdateAndSaveAsync(Action<AppConfig> updateAction)
    {
        updateAction(_config);
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 只改内存，不落盘——退出时由 MainWindow 统一 SaveAsync（与窗口尺寸/导航栏同一机制）。
    /// 偏好类改动（视图档位、勾选、下拉项等）都走这里，避免每改一下就写一次文件。
    /// </summary>
    public void Update(Action<AppConfig> updateAction)
    {
        try { updateAction(_config); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"更新配置失败: {ex.Message}"); }
    }

    /// <summary>
    /// 重置为默认配置
    /// </summary>
    public async Task ResetAsync()
    {
        _config = AppConfig.GetDefault();
        await SaveAsync();
    }

    /// <summary>
    /// 用默认值补充缺失的字段
    /// </summary>
    private static void MergeDefaults(AppConfig config, AppConfig defaults)
    {
        config.ManifestSourceEnabled ??= defaults.ManifestSourceEnabled;
        config.ManifestSources ??= defaults.ManifestSources;
        // 修复旧版本写入的乱码名称：预置源（非自定义）的显示字段始终以当前代码为准，
        // 用户的 ApiKey / 启用状态 / 排序不受影响
        if (config.ManifestSources != null)
        {
            foreach (var src in config.ManifestSources)
            {
                var preset = ManifestSource.GetPresetSources().FirstOrDefault(p => p.Id == src.Id);
                if (preset != null) { src.Name = preset.Name; src.Description = preset.Description; }
            }
        }
        config.Extensions ??= defaults.Extensions;
        if (config.WindowWidth <= 0) config.WindowWidth = defaults.WindowWidth;
        if (config.WindowHeight <= 0) config.WindowHeight = defaults.WindowHeight;
    }
}
