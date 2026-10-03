using System.Text.RegularExpressions;

namespace OSTGUI.Services;

/// <summary>
/// 版本比较与 tag 解析：**纯逻辑、零依赖**（就是为了能被单独拿出去跑一张验证表，不需要起整个应用）。
///
/// 规则（用户定）：按小数点切分、从左往右逐位比 ——
/// <list type="bullet">
/// <item>远端某一位**更大** → 有更新，提示；</item>
/// <item>远端某一位**更小** → 到此为止，无更新（后面不再看）；</item>
/// <item>相等 → 继续比下一位；全部相等 → 无更新。</item>
/// </list>
/// 短的一方缺位按 0 处理：<c>1.7</c> 对 <c>1.7.5</c> = 无更新，<c>1.8</c> 对 <c>1.7.5</c> = 有更新。
/// </summary>
internal static class VersionCompare
{
    /// <summary>从 tag/版本串里抠数字段：<c>v1.7.6</c>、<c>1.7.6-beta</c> 都得到 <c>[1,7,6]</c></summary>
    private static readonly Regex Digits = new(@"^[^0-9]*([0-9]+(?:\.[0-9]+)*)", RegexOptions.Compiled);

    /// <summary>解析失败（拿不到数字段）返回 null，调用方按"检查失败"处理</summary>
    internal static int[]? Parse(string text)
    {
        var match = Digits.Match(text.Trim());
        if (!match.Success) return null;

        var parts = match.Groups[1].Value.Split('.');
        var version = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out version[i])) return null;

        return version;
    }

    internal static bool IsRemoteNewer(int[] remote, int[] local)
    {
        var length = Math.Max(remote.Length, local.Length);
        for (var i = 0; i < length; i++)
        {
            var r = i < remote.Length ? remote[i] : 0;
            var l = i < local.Length ? local[i] : 0;
            if (r > l) return true;
            if (r < l) return false;
        }

        return false;
    }

    internal static string ToText(int[] version) => string.Join('.', version);
}
