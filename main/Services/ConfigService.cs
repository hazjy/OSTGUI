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
    /// 用默认值补充缺失的字段，并把两个源列表里的"代码事实"覆盖回来。
    /// 这是**唯一**的"以代码为准"入口（每次启动都跑）：预置源的名称 / 说明 / 接口地址 / UA / 顺序 /
    /// 能力标记一律来自预置表，配置里只保留用户数据（<c>ApiKey</c> / <c>IsEnabled</c>）。
    /// ⚠️ 站点一改端点，配置里那条旧 URL 就是死的，而配置又优先于预设 → 光改代码不生效
    /// （2026-10-27 Ryuu 的查询式端点下线，就是这么让每个 depot 静默 404 的）。
    /// ⚠️ 给预置源加字段时必须同时加进对应的 <c>ApplyPreset</c>，否则又是"改了代码不生效"。
    /// </summary>
    internal static void MergeDefaults(AppConfig config, AppConfig defaults)
    {
        config.ManifestSourceEnabled ??= defaults.ManifestSourceEnabled;
        config.ManifestSources ??= defaults.ManifestSources;
        config.RequestCodeSources ??= defaults.RequestCodeSources;

        var manifestPresets = ManifestSource.GetPresetSources();
        var droppedManifests = DropNonPreset(config.ManifestSources!, manifestPresets.Select(p => p.Id), s => s.Id);
        if (droppedManifests.Count > 0)
            LogService.Event($"清理清单源残留（已下线或自定义）：{string.Join(", ", droppedManifests)}");
        if (config.ManifestSources!.Count == 0) config.ManifestSources = defaults.ManifestSources;
        foreach (var src in config.ManifestSources)
        {
            var preset = manifestPresets.FirstOrDefault(p => p.Id == src.Id);
            if (preset != null) src.ApplyPreset(preset);
        }

        var codePresets = RequestCodeSource.GetPresetSources();
        var droppedCodes = DropNonPreset(config.RequestCodeSources!, codePresets.Select(p => p.Id), s => s.Id);
        if (droppedCodes.Count > 0)
            LogService.Event($"清理请求码源残留：{string.Join(", ", droppedCodes)}");
        if (config.RequestCodeSources!.Count == 0) config.RequestCodeSources = defaults.RequestCodeSources;
        foreach (var src in config.RequestCodeSources)
        {
            var preset = codePresets.FirstOrDefault(p => p.Id == src.Id);
            if (preset != null) src.ApplyPreset(preset);
        }

        config.Extensions ??= defaults.Extensions;
        if (config.WindowWidth <= 0) config.WindowWidth = defaults.WindowWidth;
        if (config.WindowHeight <= 0) config.WindowHeight = defaults.WindowHeight;
    }

    /// <summary>丢掉 id 不在预置表里的条目（已下线 / 自定义源），返回被丢掉的 id 列表。</summary>
    private static List<string> DropNonPreset<T>(List<T> list, IEnumerable<string> presetIds, Func<T, string> idOf)
    {
        var keep = presetIds.ToHashSet();
        var dropped = list.Where(x => !keep.Contains(idOf(x))).Select(idOf).ToList();
        if (dropped.Count > 0) list.RemoveAll(x => !keep.Contains(idOf(x)));
        return dropped;
    }

    /// <summary>
    /// 自检（挂 <c>OSTGUI.exe --trainer-selftest</c> 链）：<see cref="MergeDefaults"/> 的不变量——
    /// 非预置 id 被清掉、代码事实压过配置、用户数据（ApiKey / IsEnabled）不被覆盖、派生字段不落盘。
    /// 全通过返回空串。
    /// </summary>
    public static string SelfCheck()
    {
        var cfg = new AppConfig
        {
            ManifestSources =
            [
                new ManifestSource
                {
                    Id = "mhub", ApiKey = "K", IsEnabled = false,
                    Name = "旧名字", Description = "旧说明", BaseUrl = "https://dead.example",
                    Priority = 99, PerDepotFetch = false, RequiresToken = false, ShowKeyUpdatedAt = false,
                },
                new ManifestSource { Id = "sac", ApiKey = "legacy", IsEnabled = true },
            ],
            RequestCodeSources =
            [
                new RequestCodeSource { Id = "manifestdex", IsEnabled = true, UrlTemplate = "https://dead.example/{gid}", UserAgent = "", Priority = 99 },
                new RequestCodeSource { Id = "gone", UrlTemplate = "https://dead.example/{gid}" },
            ],
        };

        MergeDefaults(cfg, AppConfig.GetDefault());

        if (cfg.ManifestSources!.Any(s => s.Id == "sac")) return "清单源自检：已下线的源没有被清掉（sac 仍在）";
        var mhubCfg = cfg.ManifestSources!.FirstOrDefault(s => s.Id == "mhub")
            ?? throw new InvalidOperationException("清单源自检：预置源 mhub 被误删");
        var mhubPreset = ManifestSource.GetPresetSources().First(p => p.Id == "mhub");

        if (mhubCfg.Name != mhubPreset.Name) return "清单源自检：Name 没有以代码为准";
        if (mhubCfg.BaseUrl != mhubPreset.BaseUrl) return "清单源自检：BaseUrl 没有以代码为准";
        if (mhubCfg.Priority != mhubPreset.Priority) return "清单源自检：Priority 没有以代码为准";
        if (mhubCfg.PerDepotFetch != mhubPreset.PerDepotFetch) return "清单源自检：PerDepotFetch 没有以代码为准";
        if (mhubCfg.RequiresToken != mhubPreset.RequiresToken) return "清单源自检：RequiresToken 没有以代码为准";
        if (mhubCfg.ApiKey != "K") return "清单源自检：清理时把用户的 ApiKey 弄丢了";
        if (mhubCfg.IsEnabled) return "清单源自检：用户的开关状态被代码覆盖了";

        if (cfg.RequestCodeSources!.Any(s => s.Id == "gone")) return "请求码源自检：非预置 id 没有被清掉";
        var dexCfg = cfg.RequestCodeSources.FirstOrDefault(s => s.Id == "manifestdex")
            ?? throw new InvalidOperationException("请求码源自检：预置源 manifestdex 被误删");
        var dexPreset = RequestCodeSource.GetPresetSources().First(p => p.Id == "manifestdex");
        if (dexCfg.UrlTemplate != dexPreset.UrlTemplate) return "请求码源自检：UrlTemplate 没有以代码为准";
        if (dexCfg.UserAgent != dexPreset.UserAgent) return "请求码源自检：UserAgent 没有以代码为准";
        if (dexCfg.Priority != dexPreset.Priority) return "请求码源自检：Priority 没有以代码为准";

        // 派生字段不该写进 config：序列化一遍看有没有它们
        var ctx = new AppJsonConfigContext(new System.Text.Json.JsonSerializerOptions());
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(ManifestSource.GetPresetSources()[0], ctx.ManifestSource);
        foreach (var noise in new[] { "ShowRefreshCacheButton", "ShowApiKeyUpdatedAt" })
            if (manifestJson.Contains(noise)) return $"序列化自检：派生字段 {noise} 仍会写进 config.json";
        var codeJson = System.Text.Json.JsonSerializer.Serialize(RequestCodeSource.GetPresetSources()[0], ctx.RequestCodeSource);
        if (codeJson.Contains("NeedsDepotId")) return "序列化自检：派生字段 NeedsDepotId 仍会写进 config.json";

        return string.Empty;
    }
}
