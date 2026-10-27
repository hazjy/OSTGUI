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
    // AOT 源生成：把上面这套选项（缩进 + 中文不转义）塞回 context；静态缓存，别在调用点反复 new
    private static readonly AppJsonConfigContext JsonCtx = new(JsonOptions);

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
                var loaded = JsonSerializer.Deserialize(json, JsonCtx.AppConfig);
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
            var json = JsonSerializer.Serialize(_config, JsonCtx.AppConfig);
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
    internal static void MergeDefaults(AppConfig config, AppConfig defaults)
    {
        config.ManifestSourceEnabled ??= defaults.ManifestSourceEnabled;
        config.ManifestSources ??= defaults.ManifestSources;
        config.RequestCodeSources ??= defaults.RequestCodeSources;
        if (config.ManifestSources != null)
        {
            var presets = ManifestSource.GetPresetSources();

            // 清单源只留当前预置表里的那几家：自定义源功能早已下线，残留 id（sac / walftech /
            // buqiuren / github_auiowu / opensteamtool …）界面上看不见、级联里也进不去，但会一直被
            // 写回配置，以后任何"忘了过滤"的新代码都会把它们当成可用源。清理放在启动时做，
            // 不依赖用户是否打开过设置页。
            var dropped = config.ManifestSources.Where(s => presets.All(p => p.Id != s.Id)).Select(s => s.Id).ToList();
            if (dropped.Count > 0)
            {
                config.ManifestSources.RemoveAll(s => presets.All(p => p.Id != s.Id));
                LogService.Event($"清理清单源残留（已下线或自定义）：{string.Join(", ", dropped)}");
            }
            if (config.ManifestSources.Count == 0) config.ManifestSources = defaults.ManifestSources;

            // 修复旧版本写入的乱码名称：预置源的显示字段始终以当前代码为准，
            // 用户的 ApiKey / 启用状态 / 排序不受影响。
            // ⚠️ BaseUrl 也一并以代码为准：它是**接口形态**，站点一改端点，配置里那条旧 URL 就是死的，
            // 而配置又优先于预设 → 光改代码不生效。2026-10-27 Ryuu 的查询式端点下线就踩了这个坑
            // （表现为入库时每个 depot 都 404，且用户在设置页怎么改 key 都没用）。
            foreach (var src in config.ManifestSources)
            {
                var preset = presets.FirstOrDefault(p => p.Id == src.Id);
                if (preset != null)
                {
                    src.Name = preset.Name;
                    src.Description = preset.Description;
                    src.BaseUrl = preset.BaseUrl;
                }
            }
        }
        config.Extensions ??= defaults.Extensions;
        if (config.WindowWidth <= 0) config.WindowWidth = defaults.WindowWidth;
        if (config.WindowHeight <= 0) config.WindowHeight = defaults.WindowHeight;
    }

    /// <summary>
    /// 自检（挂 <c>OSTGUI.exe --trainer-selftest</c> 链）：<see cref="MergeDefaults"/> 的三条不变量——
    /// 非预置 id 被清掉、用户的 ApiKey 不被误删、BaseUrl 以代码为准。全通过返回空串。
    /// </summary>
    public static string SelfCheck()
    {
        var cfg = new AppConfig
        {
            ManifestSources =
            [
                new ManifestSource { Id = "mhub", ApiKey = "K", IsEnabled = true, BaseUrl = "https://stale.example" },
                new ManifestSource { Id = "sac", ApiKey = "legacy", IsEnabled = true, BaseUrl = "https://dead.example" },
            ],
        };

        MergeDefaults(cfg, AppConfig.GetDefault());

        if (cfg.ManifestSources.Any(s => s.Id == "sac"))
            return "清单源自检：已下线的源没有被清掉（sac 仍在）";

        var mhub = cfg.ManifestSources.FirstOrDefault(s => s.Id == "mhub");
        if (mhub == null) return "清单源自检：预置源 mhub 被误删";
        if (mhub.ApiKey != "K") return "清单源自检：清理时把用户的 ApiKey 弄丢了";

        var presetUrl = ManifestSource.GetPresetSources().First(p => p.Id == "mhub").BaseUrl;
        if (mhub.BaseUrl != presetUrl) return "清单源自检：BaseUrl 没有以代码里的预置值为准";

        return string.Empty;
    }
}
