using System.Text;

namespace OSTGUI.Utils;

/// <summary>
/// 非 Steam 快捷方式登记表（<c>userdata\&lt;账号&gt;\config\shortcuts.vdf</c>）的只读解析。
///
/// 二进制 VDF 的布局是「类型字节 → 键名 → 0x00 → 值」：0x00 = 嵌套 map、0x01 = 字符串、
/// 0x02 = int32、0x07 = uint64、0x08 = map 结束。这里只认 appid / AppName / Exe /
/// LaunchOptions 四个键，其余类型按长度跳过。解析失败一律当作「没有快捷方式」，
/// 调用方回退到老路径，不让它挡住联机。
/// </summary>
internal static class ShortcutsVdf
{
    internal readonly record struct Shortcut(uint AppId, string AppName, string Exe, string LaunchOptions);

    /// <summary>找一个 Exe 指向 <paramref name="exePath"/> 的快捷方式。</summary>
    public static Shortcut? FindByExe(string steamPath, string exePath)
    {
        var wanted = Normalize(exePath);
        foreach (var shortcut in ReadAll(steamPath))
        {
            if (string.Equals(Normalize(shortcut.Exe), wanted, StringComparison.OrdinalIgnoreCase))
                return shortcut;
        }
        return null;
    }

    /// <summary>
    /// 快捷方式的 CGameID：<c>(appid &lt;&lt; 32) | (2 &lt;&lt; 24)</c>，类型位 2 = 非 Steam 快捷方式。
    /// 少了类型位 Steam 不认这条 URL（2026-11-17 实测：不带 2&lt;&lt;24 时 ExecuteSteamURL 收下但不起进程）。
    /// </summary>
    public static ulong ToGameId(uint shortcutAppId) => ((ulong)shortcutAppId << 32) | (2UL << 24);

    private static string Normalize(string p) => p.Trim().Trim('"').Replace('/', '\\');

    private static IEnumerable<Shortcut> ReadAll(string steamPath)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(userdata))
        {
            var vdf = Path.Combine(dir, "config", "shortcuts.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var s in Parse(vdf)) yield return s;
        }
    }

    private static IEnumerable<Shortcut> Parse(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch { yield break; }

        var i = 0;
        uint appId = 0;
        var name = "";
        var exe = "";
        var opts = "";

        while (i < data.Length)
        {
            var type = data[i++];
            if (type == 0x08) break;
            if (!TryReadCString(data, ref i, out var key)) break;

            switch (type)
            {
                case 0x00:  // 嵌套 map：键是索引，内容继续按同一状态机读
                    break;

                case 0x01:
                    if (!TryReadCString(data, ref i, out var value)) yield break;
                    switch (key)
                    {
                        case "AppName": name = value; break;
                        case "Exe": exe = value; break;
                        case "LaunchOptions": opts = value; break;
                    }
                    break;

                case 0x02:
                    if (i + 4 > data.Length) yield break;
                    var iv = BitConverter.ToUInt32(data, i);
                    i += 4;
                    if (key == "appid")
                    {
                        // 前一条收尾
                        if (appId != 0)
                            yield return new Shortcut(appId, name, exe, opts);
                        appId = iv;
                        name = ""; exe = ""; opts = "";
                    }
                    break;

                case 0x07:
                    if (i + 8 > data.Length) yield break;
                    i += 8;
                    break;

                default:
                    yield break;   // 未知类型：放弃这个文件，交给调用方回退
            }
        }

        if (appId != 0)
            yield return new Shortcut(appId, name, exe, opts);
    }

    private static bool TryReadCString(byte[] data, ref int pos, out string value)
    {
        var start = pos;
        while (pos < data.Length && data[pos] != 0) pos++;
        if (pos >= data.Length) { value = ""; return false; }
        value = Encoding.UTF8.GetString(data, start, pos - start);
        pos++;   // 跳过结尾的 0x00
        return true;
    }
}
