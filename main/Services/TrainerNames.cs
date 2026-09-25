namespace OSTGUI.Services;

/// <summary>
/// 修改器名称/版本号的纯函数工具（无 IO、无状态）。
///
/// 为什么需要：站点给的**附件标题**（<c>Crimson.Desert.Enhanced.v1.0-v2.0x.Plus.12.Trainer-FLiNG</c>）、
/// 落地的 **exe 文件名**（<c>… Plus.12 Trainer.exe</c>）、索引里历史记录的写法三者并不一致，
/// 比对必须归一化；这套逻辑原先在服务与 VM 里各写了一份，现在只留这一处。
/// </summary>
public static class TrainerNames
{
    /// <summary>归一化：只留字母数字并转小写（容忍点/空格/下划线/扩展名的差别）</summary>
    public static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// 去扩展名后归一化。**只剥 .exe**，不要用 Path.GetFileNameWithoutExtension：
    /// 这些名字天生一堆点（<c>…v1.0-v2.0x.Plus.12</c>），那函数会把最后一段当扩展名砍掉
    /// （实测会变成 <c>…v1.0-v2.0x.Plus</c>，是自检抓到的真 bug）。
    /// </summary>
    public static string Key(string nameOrPath)
    {
        var file = Path.GetFileName(nameOrPath);
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
        return Normalize(file);
    }

    /// <summary>两个名称是否指同一个东西：归一化后相等，或互为前缀（-FLiNG 这类写法差异）</summary>
    public static bool IsSame(string a, string b)
    {
        var x = Key(a);
        var y = Key(b);
        return x.Length > 0 && y.Length > 0
               && (x == y || x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal));
    }

    /// <summary>
    /// 从本地文件名反推拿去 RSS 搜索的关键词：
    /// <c>Crimson.Desert.Enhanced.v1.0-v2.0x.Plus.12.exe</c> → <c>Crimson Desert Enhanced</c>
    /// </summary>
    public static string SearchQuery(string nameOrPath)
    {
        var bare = Path.GetFileNameWithoutExtension(nameOrPath);
        var cut = bare.IndexOf(".v", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) bare = bare[..cut];
        return bare.Replace('-', ' ').Replace('_', ' ').Replace('.', ' ').Replace("FLiNG", "").Trim();
    }

    /// <summary>
    /// 最小自检（<c>OSTGUI.exe --trainer-selftest</c> 调用）：返回失败说明，全通过返回空串。
    /// 覆盖的是"名称比对"这条容易悄悄错掉的逻辑（真实踩过：索引名无扩展名 vs 文件名带 .exe → 永远查不到）。
    /// </summary>
    public static string SelfCheck()
    {
        const string file = "Crimson.Desert.Enhanced.v1.0-v2.0x.Plus.12.exe";
        const string title = "Crimson.Desert.Enhanced.v1.0-v2.0x.Plus.12.Trainer-FLiNG";
        const string stored = "Crimson.Desert.Enhanced.v1.0-v2.0x.Plus.12";

        if (!IsSame(file, stored)) return "同版本：文件名 vs 索引名 应判定相同";
        if (!IsSame(file, title)) return "同版本：文件名 vs 附件标题 应判定相同（-FLiNG 后缀差异）";
        if (IsSame("Crimson.Desert.Enhanced.v1.0-v2.0x.Plus.12.exe", "Crimson.Desert.Enhanced.v1.0-v1.16.Plus.12.exe"))
            return "不同版本被判定成相同";
        if (SearchQuery(file) != "Crimson Desert Enhanced") return $"搜索词推导错误：{SearchQuery(file)}";
        if (Key(file) != Key(stored)) return "Key 去扩展名后应一致";
        return "";
    }
}
