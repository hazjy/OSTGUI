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
        config.CustomManifestSources ??= defaults.CustomManifestSources;
        config.CustomGithubRepos ??= defaults.CustomGithubRepos;
        config.CustomZipUrls ??= defaults.CustomZipUrls;
        config.Extensions ??= defaults.Extensions;
        if (config.WindowWidth <= 0) config.WindowWidth = defaults.WindowWidth;
        if (config.WindowHeight <= 0) config.WindowHeight = defaults.WindowHeight;
    }
}
