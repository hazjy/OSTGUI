using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>版本切换器的一个可选项（一个 build = appinfo 里的一个分支）。</summary>
public sealed class BuildOption
{
    public string Branch { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>该 build 下 depot → gid（只含有清单的 depot）。</summary>
    public Dictionary<string, string> Gids { get; set; } = new();

    /// <summary>覆盖了几个 depot（相对 lua 里声明的 depot 总数）。</summary>
    public int Covered { get; set; }
    public int Total { get; set; }

    public string Title => Branch == "public" ? "最新（public）" : Branch;

    public string SubTitle
    {
        get
        {
            var time = UpdatedAt == default ? "时间未知" : UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd");
            return $"build {BuildId} · {time} · 覆盖 {Covered}/{Total} 个 depot";
        }
    }

    public bool IsComplete => Total > 0 && Covered >= Total;
}

public sealed class BuildCatalog
{
    public bool Ok { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public List<BuildOption> Builds { get; set; } = new();
}

/// <summary>
/// 读"有哪些 build 可选"：直接用 <see cref="SteamGameInfoService"/> 已经拉的 appinfo
/// （<c>depots.branches</c> + 每个 depot 的 <c>manifests.&lt;分支&gt;</c>），不额外接数据源。
/// 结果按 appid 缓存 5 分钟——开几次弹窗不该重复打接口。
/// </summary>
public class BuildCatalogService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly SteamGameInfoService _gameInfo;
    private readonly Dictionary<string, (DateTime At, BuildCatalog Catalog)> _cache = new();

    public BuildCatalogService(SteamGameInfoService gameInfo) => _gameInfo = gameInfo;

    public async Task<BuildCatalog> GetAsync(string appId, IReadOnlyCollection<string> luaDepots,
        CancellationToken ct = default)
    {
        var depotKey = string.Join(",", luaDepots.OrderBy(d => d, StringComparer.Ordinal));
        var cacheKey = appId + "|" + depotKey;
        if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
            return hit.Catalog;

        var catalog = new BuildCatalog { AppId = appId };
        try
        {
            var game = await _gameInfo.GetGameDetailsFromSteamAsync(appId, ct);
            if (game is null || game.Depots.Count == 0)
            {
                catalog.Error = "读取 appinfo 失败（网络或接口异常），请稍后重试。";
                return catalog;
            }

            // 只看 lua 里声明过的 depot：别的 depot 锁不锁对这份配置没意义
            var wanted = luaDepots.Where(d => game.Depots.ContainsKey(d)).ToList();
            if (wanted.Count == 0) wanted = game.Depots.Keys.ToList();

            var branches = game.Branches.Count > 0
                ? game.Branches.Select(b => b.Name).ToList()
                : new List<string> { "public" };

            foreach (var branch in branches)
            {
                var option = new BuildOption
                {
                    Branch = branch,
                    BuildId = game.Branches.FirstOrDefault(b => b.Name == branch)?.BuildId ?? "",
                    UpdatedAt = game.Branches.FirstOrDefault(b => b.Name == branch)?.UpdatedAt ?? default,
                    Total = wanted.Count,
                };

                foreach (var depot in wanted)
                {
                    if (game.Depots[depot].ManifestsByBranch.TryGetValue(branch, out var gid) &&
                        !string.IsNullOrEmpty(gid))
                    {
                        option.Gids[depot] = gid;
                    }
                }
                option.Covered = option.Gids.Count;
                catalog.Builds.Add(option);
            }

            // public 排最前，其余按更新时间从新到旧
            catalog.Builds = catalog.Builds
                .OrderBy(b => b.Branch == "public" ? 0 : 1)
                .ThenByDescending(b => b.UpdatedAt)
                .ToList();

            catalog.Ok = catalog.Builds.Count > 0;
            if (!catalog.Ok) catalog.Error = "这个游戏没有可选的 build。";
            else _cache[cacheKey] = (DateTime.UtcNow, catalog);
            return catalog;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            catalog.Error = $"读取版本列表失败：{ex.Message}";
            return catalog;
        }
    }
}
