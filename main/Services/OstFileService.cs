using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OSTGUI.Services;

/// <summary>
/// OST 授权文件（.ost）模型
/// 明文 JSON，无加密；元数据记录来源、生成/失效时间
/// </summary>
public class OstFile
{
    public const string FormatId = "OST-AUTH";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = CurrentVersion;
    public string AppId { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ExpiresAt { get; set; } = DateTime.Now;
    public string AppTicket { get; set; } = "";
    public string ETicket { get; set; } = "";
    public int UseCount { get; set; }
    public string ExporterVersion { get; set; } = "";

    [JsonIgnore]
    public bool IsExpired => DateTime.Now > ExpiresAt;
}

/// <summary>
/// OST 授权文件（.ost）读写与校验服务
/// </summary>
public class OstFileService
{
    /// <summary>ETicket 有效窗口约 30 分钟，导出时默认按此计算失效时间</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 生成 .ost 授权文件
    /// </summary>
    public async Task<(bool success, string message, string filePath)> ExportAsync(
        string appId, string source, string appTicket, string eTicket, string outputPath)
    {
        try
        {
            var appIdTrimmed = appId.Trim();
            if (string.IsNullOrEmpty(appIdTrimmed))
                return (false, "AppID 不能为空", "");

            var appTicketTrimmed = appTicket?.Trim() ?? "";
            var eTicketTrimmed = eTicket?.Trim() ?? "";
            if (string.IsNullOrEmpty(appTicketTrimmed) && string.IsNullOrEmpty(eTicketTrimmed))
                return (false, "没有可导出的授权数据", "");

            var ost = new OstFile
            {
                AppId = appIdTrimmed,
                Source = source?.Trim() ?? "",
                CreatedAt = DateTime.Now,
                ExpiresAt = DateTime.Now.Add(DefaultValidity),
                AppTicket = appTicketTrimmed,
                ETicket = eTicketTrimmed,
                ExporterVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            };

            var json = JsonSerializer.Serialize(ost, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(outputPath, json, new System.Text.UTF8Encoding(false));
            return (true, $"已导出 {appIdTrimmed}.ost", outputPath);
        }
        catch (Exception ex)
        {
            return (false, $"导出失败: {ex.Message}", "");
        }
    }

    /// <summary>
    /// 读取并校验 .ost 授权文件
    /// </summary>
    public async Task<(bool success, string message, OstFile? data)> ParseAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var ost = JsonSerializer.Deserialize<OstFile>(json);
            if (ost == null)
                return (false, "文件内容无效", null);

            if (ost.Format != OstFile.FormatId)
                return (false, "不是 OST 授权文件", null);
            if (ost.Version != OstFile.CurrentVersion)
                return (false, $"不支持的授权文件版本: {ost.Version}", null);
            if (string.IsNullOrWhiteSpace(ost.AppId))
                return (false, "授权文件缺少 AppID", null);
            if (string.IsNullOrWhiteSpace(ost.AppTicket) && string.IsNullOrWhiteSpace(ost.ETicket))
                return (false, "授权文件中没有授权数据", null);

            return (true, "解析成功", ost);
        }
        catch (JsonException)
        {
            return (false, "文件不是有效的 OST 授权文件", null);
        }
        catch (Exception ex)
        {
            return (false, $"读取失败: {ex.Message}", null);
        }
    }

    /// <summary>
    /// 回写授权文件（导入消费次数后更新 UseCount）
    /// </summary>
    public async Task UpdateUseCountAsync(string filePath, OstFile ost)
    {
        try
        {
            var json = JsonSerializer.Serialize(ost, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 文件只读等情况：次数回写失败不阻断导入
        }
    }
}
