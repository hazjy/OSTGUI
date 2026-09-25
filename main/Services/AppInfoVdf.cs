using System.Text;

namespace OSTGUI.Services;

/// <summary>
/// 从 <c>&lt;Steam&gt;\appcache\appinfo.vdf</c> 取"客户端已知的 app：类型 + 名字"（实测 492 条，读一遍几十毫秒）。
///
/// 两个用途：
/// - 成就页的**正版候选池**：客户端自己认识哪些 app 就以它为准，再按类型白名单
///   （Game/Demo/Mod，与 Fluent-Steam-Lua 同款）与拥有判定过滤，否则 SDK/工具/Redistributable 会混进来；
/// - **名字**：在线补名走 store.steampowered.com，这台机器上被加速器接管、经常不通，
///   而 schema 的 gamename 可能是开发代号（实测 3751950 是 OBSIDIAN），所以用 appinfo 的 <c>common.name</c>。
///   （Fluent-Steam-Lua 还写了"中文优先：localization.schinese.name"，但实测我们的
///   appinfo.vdf 里没有 localization 键、也没有 name_localized 数据，两边都只能落回英文名。）
///
/// v29 格式（实测确认）：
/// - 头部：magic(4) universe(4) 字符串表偏移(8)，之后是连续条目，直到 appid==0；
/// - 条目：appid(4) size(4) 头部(44) 数据(size-44)，前进 <c>8 + size</c>；
/// - 数据里的 KV 是"<c>类型字节 + 键ID(u32) + 值</c>"，键名在字符串表里（表头 u32 计数 + NUL 分隔串，索引 0 起）。
/// 只做最小解析（不建整棵树）：先取字符串表里几个键的 ID，再按 <c>\x00&lt;ID&gt;</c>（子节点）/
/// <c>\x01&lt;ID&gt;</c>（字符串）在字节里定位。版本不认识就返回空表，让调用方回落其它名字源。
/// </summary>
public static class AppInfoVdf
{
    private const uint V29Magic = 0x07564429;

    /// <summary>所有条目：appid → (类型, 名字)；类型/名字取不到时给空串</summary>
    public static Dictionary<string, (string Type, string Name)> GetAll(string steamPath)
    {
        var result = new Dictionary<string, (string Type, string Name)>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(steamPath)) return result;

        try
        {
            var path = Path.Combine(steamPath, "appcache", "appinfo.vdf");
            if (!File.Exists(path)) return result;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 24 || BitConverter.ToUInt32(bytes, 0) != V29Magic) return result;

            var pool = (int)BitConverter.ToUInt64(bytes, 8);
            var nameKey = FindKeyId(bytes, pool, "name");
            var typeKey = FindKeyId(bytes, pool, "type");
            if (nameKey < 0 || typeKey < 0) return result;

            var offset = 16;
            while (offset + 8 <= bytes.Length)
            {
                var appId = BitConverter.ToUInt32(bytes, offset);
                var size = BitConverter.ToUInt32(bytes, offset + 4);
                if (appId == 0 || size == 0 || size > bytes.Length) break;

                var from = offset + 8;
                var to = (int)Math.Min(bytes.Length, offset + 8L + size);
                result[appId.ToString()] = (
                    ReadStringValue(bytes, from, to, typeKey) ?? "",
                    ReadStringValue(bytes, from, to, nameKey) ?? "");
                offset += 8 + (int)size;
            }
        }
        catch (Exception ex)
        {
            LogService.AddAppLog($"appinfo.vdf 读取失败: {ex.Message}");
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

    /// <summary>找 <c>\x01&lt;键ID&gt;</c>（字符串），返回它的值</summary>
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
