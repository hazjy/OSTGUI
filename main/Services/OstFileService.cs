using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OSTGUI.Services;

/// <summary>
/// OST 授权文件（.ost）模型
/// 明文 JSON，无加密；元数据记录来源、生成/失效时间
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public partial class OstFile
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
            {
                CleanupEmptyFile(outputPath);
                return (false, "AppID 不能为空", "");
            }

            var appTicketTrimmed = appTicket?.Trim() ?? "";
            var eTicketTrimmed = eTicket?.Trim() ?? "";
            if (string.IsNullOrEmpty(appTicketTrimmed) && string.IsNullOrEmpty(eTicketTrimmed))
            {
                CleanupEmptyFile(outputPath);
                return (false, "无可导出的授权数据", "");
            }

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

            var json = JsonSerializer.Serialize(ost, AppJsonIndentedContext.Default.OstFile);

            // 先写临时文件再覆盖：保存对话框已经建出目标文件，直接写一半失败会留下残缺内容
            var tempPath = outputPath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(tempPath, json, new System.Text.UTF8Encoding(false));
                File.Move(tempPath, outputPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }

            return (true, $"已导出 {appIdTrimmed}.ost", outputPath);
        }
        catch (Exception ex)
        {
            CleanupEmptyFile(outputPath);
            return (false, $"导出失败：{ex.Message}", "");
        }
    }

    /// <summary>
    /// 保存对话框（FileSavePicker）在选好路径时就建出了目标文件；导出没写成就把它删掉，
    /// 只删 0 字节的，避免误删用户原本就有内容的同名文件。
    /// </summary>
    private static void CleanupEmptyFile(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && new FileInfo(path).Length == 0)
                File.Delete(path);
        }
        catch { }
    }

    /// <summary>
    /// 读取并校验 .ost 授权文件
    /// </summary>
    public async Task<(bool success, string message, OstFile? data)> ParseAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var ost = JsonSerializer.Deserialize(json, AppJsonIndentedContext.Default.OstFile);
            if (ost == null)
                return (false, "文件内容无效", null);

            if (ost.Format != OstFile.FormatId)
                return (false, "不是 OST 授权文件", null);
            if (ost.Version != OstFile.CurrentVersion)
                return (false, $"不支持的授权文件版本：{ost.Version}", null);
            if (string.IsNullOrWhiteSpace(ost.AppId))
                return (false, "授权文件缺少 AppID", null);
            if (string.IsNullOrWhiteSpace(ost.AppTicket) && string.IsNullOrWhiteSpace(ost.ETicket))
                return (false, "授权文件中无授权数据", null);

            return (true, "解析成功", ost);
        }
        catch (JsonException)
        {
            return (false, "文件不是有效的 OST 授权文件", null);
        }
        catch (Exception ex)
        {
            return (false, $"读取失败：{ex.Message}", null);
        }
    }

    /// <summary>
    /// 自检：失败路径不能留下 0 字节的 .ost，也不能误删用户已有内容的同名文件。
    /// ⚠️ 自检跑在 UI 线程（带 DispatcherQueueSynchronizationContext），直接
    /// <c>GetAwaiter().GetResult()</c> 阻塞异步调用会死锁 —— 必须放到线程池里跑。
    /// </summary>
    public static string SelfCheck()
    {
        return Task.Run(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "ostgui-ostcheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var svc = new OstFileService();
            var ticket = new string('a', 128);
            try
            {
                // 1) 正常导出：写出文件，内容可解析
                var okPath = Path.Combine(dir, "ok.ost");
                var (ok, _, _) = svc.ExportAsync("730", "tester", ticket, ticket, okPath).GetAwaiter().GetResult();
                if (!ok) return "OstFileService：正常导出失败";
                if (!File.Exists(okPath)) return "OstFileService：正常导出没有产出文件";
                if (new FileInfo(okPath).Length == 0) return "OstFileService：正常导出写成了空文件";

                // 2) 无授权数据 + 保存对话框预先建出的空文件 → 必须被清掉
                var emptyPath = Path.Combine(dir, "empty.ost");
                File.WriteAllText(emptyPath, "");
                var (ok2, _, _) = svc.ExportAsync("730", "tester", "", "", emptyPath).GetAwaiter().GetResult();
                if (ok2) return "OstFileService：无授权数据却报导出成功";
                if (File.Exists(emptyPath)) return "OstFileService：导出失败后残留了 0 字节文件";

                // 3) 失败时不能误删用户已有内容的同名文件
                var keepPath = Path.Combine(dir, "keep.ost");
                File.WriteAllText(keepPath, "user-data");
                _ = svc.ExportAsync("730", "tester", "", "", keepPath).GetAwaiter().GetResult();
                if (!File.Exists(keepPath)) return "OstFileService：失败时误删了用户原有文件";
                if (File.ReadAllText(keepPath) != "user-data") return "OstFileService：失败时改动了用户原有文件";
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return "";
        }).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 回写授权文件（导入消费次数后更新 UseCount）
    /// </summary>
    public async Task UpdateUseCountAsync(string filePath, OstFile ost)
    {
        try
        {
            var json = JsonSerializer.Serialize(ost, AppJsonIndentedContext.Default.OstFile);
            await File.WriteAllTextAsync(filePath, json, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 文件只读等情况：次数回写失败不阻断导入
        }
    }
}
