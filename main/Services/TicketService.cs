using System.Text.Json;
using Microsoft.Win32;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// D加密 Ticket 管理服务
/// 管理 AppTicket/ETicket 的注册表写入和 Lua 配置生成
/// </summary>
public class TicketService
{
    private readonly LuaConfigService _luaService;
    private readonly ConfigService _configService;
    private readonly string _profilePath;

    private const string SteamAppsRegPath = @"Software\Valve\Steam\Apps";

    public TicketService(LuaConfigService luaService, ConfigService configService)
    {
        _luaService = luaService;
        _configService = configService;
        _profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OSTGUI", "ticket_profiles.json");
    }

    /// <summary>
    /// 加载所有 Ticket 方案
    /// </summary>
    public async Task<List<TicketProfile>> LoadProfilesAsync()
    {
        try
        {
            if (File.Exists(_profilePath))
            {
                var json = await File.ReadAllTextAsync(_profilePath);
                var profiles = JsonSerializer.Deserialize<List<TicketProfile>>(json);
                if (profiles != null) return profiles;
            }
        }
        catch { }

        // 返回默认空方案
        return new()
        {
            new TicketProfile { Id = "default", Name = "默认方案", IsActive = true }
        };
    }

    /// <summary>
    /// 保存所有 Ticket 方案
    /// </summary>
    public async Task SaveProfilesAsync(List<TicketProfile> profiles)
    {
        try
        {
            var dir = Path.GetDirectoryName(_profilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_profilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存方案失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将 Ticket 写入注册表（Windows 凭据存储）
    /// </summary>
    public (bool success, string message) WriteTicketToRegistry(TicketEntry ticket)
    {
        try
        {
            if (!ticket.HasAppTicket && !ticket.HasETicket)
                return (false, "没有可写入的 ticket 数据");

            using var key = Registry.CurrentUser.CreateSubKey($"{SteamAppsRegPath}\\{ticket.AppId}");

            if (ticket.HasAppTicket)
            {
                var appTicketBytes = HexStringToBytes(ticket.AppTicket);
                if (appTicketBytes != null)
                    key.SetValue("AppTicket", appTicketBytes, RegistryValueKind.Binary);
            }

            if (ticket.HasETicket)
            {
                var eTicketBytes = HexStringToBytes(ticket.ETicket);
                if (eTicketBytes != null)
                    key.SetValue("ETicket", eTicketBytes, RegistryValueKind.Binary);
            }

            ticket.LastUsedTime = DateTime.Now;
            return (true, $"已写入 AppID {ticket.AppId} 的授权数据到注册表");
        }
        catch (Exception ex)
        {
            return (false, $"注册表写入失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 读取注册表中的 Ticket 数据
    /// </summary>
    public TicketEntry? ReadTicketFromRegistry(string appId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($"{SteamAppsRegPath}\\{appId}");
            if (key == null) return null;

            var ticket = new TicketEntry { AppId = appId };

            var appTicketBytes = key.GetValue("AppTicket") as byte[];
            if (appTicketBytes != null)
                ticket.AppTicket = BytesToHexString(appTicketBytes);

            var eTicketBytes = key.GetValue("ETicket") as byte[];
            if (eTicketBytes != null)
                ticket.ETicket = BytesToHexString(eTicketBytes);

            return ticket;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 删除注册表中的 Ticket 数据
    /// </summary>
    public (bool success, string message) DeleteTicketFromRegistry(string appId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($"{SteamAppsRegPath}\\{appId}", true);
            if (key != null)
            {
                try { key.DeleteValue("AppTicket"); } catch { }
                try { key.DeleteValue("ETicket"); } catch { }
            }

            return (true, $"已删除 AppID {appId} 的授权数据");
        }
        catch (Exception ex)
        {
            return (false, $"删除失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 应用完整的 Ticket 方案到系统
    /// </summary>
    public async Task<(bool success, string message)> ApplyProfileAsync(TicketProfile profile)
    {
        var errors = new List<string>();
        var success = 0;

        foreach (var ticket in profile.Tickets)
        {
            // 1. 写入注册表
            var (regSuccess, regMsg) = WriteTicketToRegistry(ticket);
            if (!regSuccess) errors.Add(regMsg);
            else success++;

            // 2. 生成 Lua 配置
            if (ticket.HasAppTicket || ticket.HasETicket)
            {
                var luaContent = GenerateTicketLua(ticket);
                await _luaService.WriteLuaFileAsync(ticket.AppId, luaContent);
            }
        }

        if (errors.Count > 0 && success == 0)
            return (false, $"方案应用失败:\n{string.Join("\n", errors)}");

        var msg = $"已应用「{profile.Name}」方案（{success}/{profile.Tickets.Count} 个授权）";
        if (errors.Count > 0)
            msg += $"\n警告:\n{string.Join("\n", errors)}";

        return (true, msg);
    }

    /// <summary>
    /// 为指定方案生成 Lua 配置内容
    /// </summary>
    private static string GenerateTicketLua(TicketEntry ticket)
    {
        var lines = new List<string>
        {
            $"-- OpenSteamTool D加密授权配置 - AppID {ticket.AppId}",
            $"-- 账号: {ticket.AccountName}",
            $"-- 由 OSTGUI 自动生成",
            $"-- 注意: Denuvo 授权有效期为 30 分钟，超时后需刷新",
            $""
        };

        lines.Add($"addappid({ticket.AppId})");

        if (!string.IsNullOrEmpty(ticket.AppTicket))
            lines.Add($"setAppTicket({ticket.AppId}, \"{ticket.AppTicket}\")");

        if (!string.IsNullOrEmpty(ticket.ETicket))
            lines.Add($"setETicket({ticket.AppId}, \"{ticket.ETicket}\")");

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// 导入从 extract_tickets 工具提取的 ticket 数据
    /// </summary>
    public static TicketEntry? ParseTicketFromHex(string appId, string appTicketHex, string eTicketHex, string accountName = "")
    {
        if (string.IsNullOrWhiteSpace(appTicketHex) && string.IsNullOrWhiteSpace(eTicketHex))
            return null;

        return new TicketEntry
        {
            AppId = appId,
            AppTicket = appTicketHex?.Trim() ?? "",
            ETicket = eTicketHex?.Trim() ?? "",
            AccountName = accountName,
        };
    }

    /// <summary>
    /// 从 tickets.txt 文本解析 ticket 数据
    /// 格式示例:
    /// appid:1361510
    /// appticket(184 bytes):14000000...
    /// eticket(143 bytes):...
    /// </summary>
    public static List<TicketEntry> ParseTicketsFromText(string content, string accountName = "")
    {
        var tickets = new List<TicketEntry>();
        TicketEntry? current = null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("appid:"))
            {
                if (current != null) tickets.Add(current);
                var appId = trimmed[6..].Trim();
                current = new TicketEntry { AppId = appId, AccountName = accountName };
                continue;
            }

            if (current == null) continue;

            if (trimmed.StartsWith("appticket"))
            {
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx > 0)
                    current.AppTicket = trimmed[(colonIdx + 1)..].Trim();
            }
            else if (trimmed.StartsWith("eticket"))
            {
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx > 0)
                    current.ETicket = trimmed[(colonIdx + 1)..].Trim();
            }
        }

        if (current != null) tickets.Add(current);
        return tickets;
    }

    private static byte[]? HexStringToBytes(string hex)
    {
        hex = hex.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        if (hex.Length % 2 != 0) return null;
        try
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }
        catch { return null; }
    }

    private static string BytesToHexString(byte[] bytes)
    {
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }
}