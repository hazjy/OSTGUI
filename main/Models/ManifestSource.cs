namespace OSTGUI.Models;

/// <summary>
/// 清单源配置模型
/// BaseUrl 为 URL 模板，支持以下占位符：
///   {appid}     游戏 AppID
///   {key}       该源配置的 API Key
///   {token}     同上（兼容别名）
///   {depotid}   Depot ID（下载清单时替换）
///   {manifestid} Manifest GID（下载清单时替换）
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public partial class ManifestSource
{
    public const string PlaceholderAppId = "{appid}";
    public const string PlaceholderKey = "{key}";
    public const string PlaceholderToken = "{token}";
    public const string PlaceholderDepotId = "{depotid}";
    public const string PlaceholderManifestId = "{manifestid}";

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public ManifestSourceType Type { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool RequiresToken { get; set; }

    /// <summary>
    /// 该源独立的 API Key / Token（在界面中配置）
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public int Priority { get; set; } = 100;

    /// <summary>
    /// 该源是否需要手动刷新缓存的按钮（当前仅 Sudama 密钥库）
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowRefreshCacheButton => Id == "sudama";

    /// <summary>
    /// Sudama 缓存上次刷新/导入时间文案（仅 Sudama 行显示）
    /// </summary>
    public string SudamaCacheAgeText { get; set; } = "";

    /// <summary>
    /// 该源 API Key / Token 上次设置时间文案（需 token 的源显示；页面加载时的绑定初始化不触发）
    /// </summary>
    public string ApiKeyUpdatedAtText { get; set; } = "";

    /// <summary>
    /// 「API Key / Token」输入框的占位说明。各家对密钥的依赖不同（MHub 没 key 就不能用），
    /// 别再共用一个含糊的"留空则不使用"。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ApiKeyPlaceholder => Id switch
    {
        "mhub" or "hubcap" => "未配置密钥则不使用",
        "ryuu" => "整包下载需要，请填 auth code",
        _ => "可选，留空则不使用"
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowApiKeyUpdatedAt => RequiresToken && ShowKeyUpdatedAt && !string.IsNullOrEmpty(ApiKeyUpdatedAtText);

    /// <summary>
    /// 获取该源 API Key / Token 的页面地址（无则返回 null）
    /// </summary>
    public static string? GetTokenPageUrl(string sourceId) => sourceId switch
    {
        "mhub" => "https://manifesthub2.filegear-sg.me/",
        "hubcap" => "https://hubcapmanifest.com/",
        "ryuu" => "https://generator.ryuu.lol/login",
        "github_auiowu" => "https://github.com/settings/tokens",
        _ => null
    };

    /// <summary>
    /// 该源是否已接入入库逻辑（未接入的源仅在设置页隐藏，仍保留在配置与文档中）
    /// </summary>
    public static bool IsImplementedSource(string sourceId) => sourceId switch
    {
        "mhub" or "hubcap" or "sudama" or "ryuu" => true,
        _ => false
    };

    /// <summary>
    /// 替换 URL 模板中的占位符
    /// </summary>
    public string BuildUrl(string? appId = null, string? depotId = null, string? manifestId = null)
    {
        var url = BaseUrl;
        if (!string.IsNullOrEmpty(appId))
            url = url.Replace(PlaceholderAppId, appId);
        if (!string.IsNullOrEmpty(depotId))
            url = url.Replace(PlaceholderDepotId, depotId);
        if (!string.IsNullOrEmpty(manifestId))
            url = url.Replace(PlaceholderManifestId, manifestId);
        url = url.Replace(PlaceholderKey, ApiKey)
                   .Replace(PlaceholderToken, ApiKey);
        return url;
    }

    /// <summary>
    /// 这个源是不是"按 depot+gid 点播"的清单源。预置里显式写死：
    /// MHub = true，Sudama（密钥源，只有全量文件下载）= false。
    /// 级联下载按它筛，「测活」按钮与连通性也按它显示——不做模板字符串嗅探。
    /// </summary>
    public bool PerDepotFetch { get; set; }

    /// <summary>
    /// 这个源是不是"按 appid 整包"给清单：一次拿 zip，内含 `&lt;appid&gt;.lua` 与
    /// `&lt;depot&gt;_&lt;gid&gt;.manifest`。整包源不参与按 depot 的级联，只在点播源全失败后兜底。
    /// URL 模板用 <see cref="PlaceholderAppId"/> 与 <see cref="PlaceholderKey"/>。
    /// </summary>
    public bool WholeAppFetch { get; set; }

    /// <summary>
    /// 是否显示"上次更新"时间。密钥长期有效的源不必显示，否则只会误导。
    /// </summary>
    public bool ShowKeyUpdatedAt { get; set; } = true;

    /// <summary>「测活」结果文案（未测 / 测试中… / 是 / 否）。只显示，不落盘。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConnectivityText { get; set; } = "未测";

    /// <summary>
    /// 把预置表里的**代码事实**覆盖到配置对象上（名称 / 说明 / 接口地址 / 顺序 / 能力标记）。
    /// <c>Id</c> / <c>ApiKey</c> / <c>IsEnabled</c> 属于用户数据，不碰。
    /// 只在 <c>ConfigService.MergeDefaults</c> 里调用——那是唯一的"以代码为准"入口。
    /// </summary>
    public void ApplyPreset(ManifestSource preset)
    {
        Name = preset.Name;
        Description = preset.Description;
        BaseUrl = preset.BaseUrl;
        Priority = preset.Priority;
        PerDepotFetch = preset.PerDepotFetch;
        WholeAppFetch = preset.WholeAppFetch;
        RequiresToken = preset.RequiresToken;
        ShowKeyUpdatedAt = preset.ShowKeyUpdatedAt;
        Type = preset.Type;
    }

    /// <summary>
    /// 预置清单源列表
    /// </summary>
    public static List<ManifestSource> GetPresetSources() => new()
    {
        new()
        {
            Id = "mhub", Name = "MHub", Description = "清单源",
            BaseUrl = "https://api.manifesthub2.filegear-sg.me/manifest?apikey={key}&depotid={depotid}&manifestid={manifestid}",
            Type = ManifestSourceType.MHub, RequiresToken = true, Priority = 1, PerDepotFetch = true
        },
        new()
        {
            // 单份生成（点播腿）：直接让站点去 Steam 取这一份，返回内层清单（magic 0x71F617D0）。
            // 2026-10-30 实测：当前 gid 与旧分支 gid 都能取（CS2 1.40.8.8、BG3 patch6_hf9 字节数与 MHub 一致）。
            // 额度：单份生成 1500/日（/api/v1/generate/usage），与整包/ lua 的 25/日 是两个独立桶。
            // key 走 api_key 查询参数即可（也接受 Authorization / X-API-Key 头，模板只需查询参数）。
            Id = "hubcap", Name = "Hubcap", Description = "清单源|单份生成 1500/日，整包 25/日",
            BaseUrl = "https://hubcapmanifest.com/api/v1/generate/manifest?depot_id={depotid}&manifest_id={manifestid}&api_key={key}",
            Type = ManifestSourceType.MHub, RequiresToken = true, Priority = 2, PerDepotFetch = true
        },
        new()
        {
            // 密钥源排在清单源之前显示（用户要求）：它不参与清单级联，Priority 只影响列表顺序
            Id = "sudama", Name = "Sudama", Description = "密钥源",
            BaseUrl = "https://api.993499094.xyz/depotkeys.json",
            Type = ManifestSourceType.KeyOnly, Priority = 0
        },
        new()
        {
            // 整包腿（2026-10-30 起站点唯一可用的清单接口）：GET /api/download/{appid}?file_type=manifest
            // 返回 zip，内含 <appid>.lua 与 <depot>_<gid>.manifest；必须带 auth code。
            // 点播腿 /api/manifest/<depot>_<gid>.manifest 已被站点移除（任何配对恒 404
            // {"error":"Manifest not found."}），故 PerDepotFetch = false、不再参与按 depot 的级联。
            Id = "ryuu", Name = "Ryuu's Manifests", Description = "清单源|整包，需 auth code",
            BaseUrl = "https://generator.ryuu.lol/api/download/{appid}?file_type=manifest&auth_key={key}",
            Type = ManifestSourceType.MHub, RequiresToken = true, Priority = 5,
            PerDepotFetch = false, WholeAppFetch = true,
            ShowKeyUpdatedAt = false,   // auth code 长期有效，不显示"上次更新"
        },
    };

    /// <summary>
    /// 自检（挂 <c>OSTGUI.exe --trainer-selftest</c> 链）：内置源的接口地址是**死写在这里**的，
    /// 站点一改端点就整条静默失效（2026-10-27 与 10-30 各发生过一次，都表现为"下载失败"）。
    /// 全通过返回空串。
    /// </summary>
    public static string SelfCheck()
    {
        foreach (var s in GetPresetSources())
        {
            // 预置表里的每一家都必须"已接入"：漏了就是界面看不见、级联不参与，且没有任何报错。
            if (!IsImplementedSource(s.Id))
                return $"预置源「{s.Name}」没有进 IsImplementedSource：界面不可见、清单级联也不参与";

            if (string.IsNullOrWhiteSpace(s.BaseUrl)) return $"内置源「{s.Name}」没有接口地址";

            var url = s.BuildUrl("APPID", "DEPOTID", "MANIFESTID");
            var at = url.IndexOf('{');
            if (at >= 0) return $"内置源「{s.Name}」的地址残留未替换的占位符：{url[at..]}";

            if (s.PerDepotFetch &&
                (!s.BaseUrl.Contains(PlaceholderDepotId) || !s.BaseUrl.Contains(PlaceholderManifestId)))
                return $"点播源「{s.Name}」的地址缺少 {PlaceholderDepotId} / {PlaceholderManifestId} 占位符";

            if (s.WholeAppFetch && !s.BaseUrl.Contains(PlaceholderAppId))
                return $"整包源「{s.Name}」的地址缺少 {PlaceholderAppId} 占位符";
        }
        return string.Empty;
    }
}

/// <summary>
/// 清单源类型。只为阅读配置方便而保留：全项目没有任何分支读它，
/// 能力判定走显式的 <see cref="ManifestSource.PerDepotFetch"/> 与 <see cref="ManifestSource.RequiresToken"/>。
/// 自定义源 / opensteamtool 两代早已下线，枚举值不再保留（老配置里的 2/3 会被启动清理按 id 丢掉）。
/// </summary>
public enum ManifestSourceType
{
    MHub,
    KeyOnly
}
