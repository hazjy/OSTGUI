namespace NoSteamLauncher.Services;

/// <summary>
/// 从游戏 EXE 反推「部署根」，对齐 SAC 的规则（给它一个目录，它整棵递归扫 steam_api*.dll）。
///
/// 为什么需要：UE 布局是 <c>&lt;根&gt;\&lt;项目&gt;\Binaries\Win64\Game.exe</c>，而 steam_api64.dll 在
/// <c>&lt;根&gt;\Engine\Binaries\ThirdParty\Steamworks\Steamv&lt;NNN&gt;\Win64\</c> —— 与 exe **不同枝**。
/// 只扫 exe 目录会漏掉那份 DLL，结果是"部署成功"但游戏照旧连真 Steam、模拟器不生效。
///
/// 非 UE 布局（exe 与 DLL 同枝，绝大多数游戏）一律返回 exe 所在目录，行为与从前一致。
/// </summary>
public static class GameRootResolver
{
    /// <summary>UE 根的特征目录：含它即认定这一层是游戏根。</summary>
    private static readonly string[] UnrealMarkers =
    {
        Path.Combine("Engine", "Binaries", "ThirdParty", "Steamworks"),
    };

    /// <summary>最多向上几层。UE 需要 3 层（Win64 → Binaries → &lt;项目&gt; → 根）。</summary>
    private const int MaxLevelsUp = 3;

    /// <summary>从游戏 EXE 反推部署根；找不到特征就返回 exe 所在目录。</summary>
    public static string ResolveFromExe(string gameExePath)
    {
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(gameExePath));
        if (string.IsNullOrEmpty(exeDir)) return Directory.GetCurrentDirectory();

        string? candidate = exeDir;
        for (var level = 0; level <= MaxLevelsUp && candidate is not null; level++)
        {
            if (IsUnrealRoot(candidate)) return candidate;

            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) || parent == candidate) break;   // 到盘根
            if (IsLibraryRoot(parent)) break;                                 // 绝不让根退到整个 Steam 库
            candidate = parent;
        }

        return exeDir;
    }

    /// <summary>该目录是否 UE 游戏根（用于日志里说明"这次多扫了 Engine 那棵树"）。</summary>
    public static bool IsUnrealRoot(string dir) =>
        UnrealMarkers.Any(m => Directory.Exists(Path.Combine(dir, m)));

    /// <summary>盘根，或 <c>...\steamapps\common</c> 这类"装着很多游戏"的目录 —— 不能当部署根。</summary>
    private static bool IsLibraryRoot(string dir)
    {
        var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(trimmed)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(root) && string.Equals(root, trimmed, StringComparison.OrdinalIgnoreCase))
            return true;

        var parentName = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);
        return string.Equals(Path.GetFileName(trimmed), "common", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parentName, "steamapps", StringComparison.OrdinalIgnoreCase);
    }
}
