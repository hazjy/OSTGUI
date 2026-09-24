using System.Text;

namespace OSTGUI.Services;

/// <summary>
/// 从 <c>&lt;Steam&gt;\appcache\appinfo.vdf</c> 取"客户端已知的游戏名"。
///
/// 为什么需要它：在线补名走 store.steampowered.com，这台机器上该域名被加速器接管、经常不通；
/// 而 schema 里的 gamename 可能是开发代号（实测 3751950 是 OBSIDIAN）。
/// appinfo.vdf 是本地、离线、权威的名字源。
///
/// v29 的格式（实测确认）：
/// - 头部：magic(4) universe(4) 字符串表偏移(8)，之后是连续条目，直到 appid==0；
/// - 条目：appid(4) size(4) 头部(44) 数据(size-44)，前进 <c>8 + size</c>；
/// - 数据里的 KV 是"<c>类型字节 + 键ID(u32) + 值</c>"，键名在字符串表里（表头 u32 计数 + NUL 分隔串，索引 0 起）。
/// 只做最小解析：找到 "name" 的键 ID，然后每个条目里找第一个 <c>\x01&lt;ID&gt;</c> 取其后的字符串（= common.name）。
/// 版本不认识就直接返回空表，让调用方回落其它名字源。
/// </summary>
public static class AppInfoVdf
{
    private const uint V29Magic = 0x07564429;

    public static Dictionary<string, string> GetNames(string steamPath, IReadOnlyCollection<string> wanted)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wanted.Count == 0 || string.IsNullOrEmpty(steamPath)) return result;

        try
        {
            var path = Path.Combine(steamPath, "appcache", "appinfo.vdf");
            if (!File.Exists(path)) return result;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 24 || BitConverter.ToUInt32(bytes, 0) != V29Magic) return result;

            var nameKey = FindKeyId(bytes, (int)BitConverter.ToUInt64(bytes, 8), "name");
            if (nameKey < 0) return result;

            var left = new HashSet<uint>();
            foreach (var id in wanted)
                if (uint.TryParse(id, out var appId) && appId != 0) left.Add(appId);

            var offset = 16;
            while (left.Count > 0 && offset + 8 <= bytes.Length)
            {
                var appId = BitConverter.ToUInt32(bytes, offset);
                var size = BitConverter.ToUInt32(bytes, offset + 4);
                if (appId == 0 || size == 0 || size > bytes.Length) break;

                var end = (int)Math.Min(bytes.Length, offset + 8L + size);
                if (left.Remove(appId) && ReadStringValue(bytes, offset + 8, end, nameKey) is { } name)
                    result[appId.ToString()] = name;

                offset += 8 + (int)size;
            }
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"appinfo.vdf 取名失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>字符串表里某个键名的索引（表头是 u32 计数，之后是 NUL 分隔串，索引从 0 起）</summary>
    private static int FindKeyId(byte[] bytes, int poolOffset, string key)
    {
        if (poolOffset <= 0 || poolOffset + 4 >= bytes.Length) return -1;

        var index = 0;
        var pos = poolOffset + 4;
        while (pos < bytes.Length && index < 20000)
        {
            var end = pos;
            while (end < bytes.Length && bytes[end] != 0) end++;
            if (Encoding.UTF8.GetString(bytes, pos, end - pos) == key) return index;

            index++;
            pos = end + 1;
        }
        return -1;
    }

    /// <summary>在条目数据里找 <c>\x01&lt;键ID&gt;</c>（类型=字符串 + 目标键），返回其值</summary>
    private static string? ReadStringValue(byte[] bytes, int from, int to, int keyId)
    {
        for (var i = from; i + 5 < to; i++)
        {
            if (bytes[i] != 1 || BitConverter.ToUInt32(bytes, i + 1) != (uint)keyId) continue;

            var start = i + 5;
            var end = start;
            while (end < to && bytes[end] != 0) end++;
            if (end == start) return null;
            return Encoding.UTF8.GetString(bytes, start, end - start);
        }
        return null;
    }
}
