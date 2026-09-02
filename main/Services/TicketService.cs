using Microsoft.Win32;
using OSTGUI.Models;

namespace OSTGUI.Services;

/// <summary>
/// D加密 Ticket 管理服务——注册表写入（导入 .ost 时落地到内核认的凭证存储路径）
/// </summary>
public class TicketService
{
    private const string SteamAppsRegPath = @"Software\Valve\Steam\Apps";

    /// <summary>
    /// 将 Ticket 写入注册表（HKCU\Software\Valve\Steam\Apps\&lt;appid&gt;，内核 SteamCredentialStore 读取路径）
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
}