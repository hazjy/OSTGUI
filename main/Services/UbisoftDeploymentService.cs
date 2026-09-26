using System.Reflection;

namespace OSTGUI.Services;

/// <summary>
/// 免育碧部署：探测 uplay/upc loader → 备份 → 替换为 Goldberg R2 模拟器 → 写 uplay_r2.ini。
/// 资源：upc_r2_loader64.dll（自编译自 RefProjects/1-在用/Goldberg_r2_extended，LGPL-3.0，见 docs/dev/THIRD-PARTY-NOTICES.md）。
/// 引擎证据：模拟器从自身 DLL 同目录读 uplay_r2.ini（emu.cpp UPC_Init: lib_path + "\\uplay_r2.ini"），故 ini 与 loader 同目录写入。
/// </summary>
public class UbisoftDeploymentService
{
    private const string EmbeddedDll = "upc_r2_loader64.dll";
    private const string IniFileName = "uplay_r2.ini";

    /// <summary>育碧 loader 固定名（按常见度排序）：upc_r2 为 Unity 育碧新作（如 UNO），uplay_r2/uplaypc_r2 为传统 R2</summary>
    public static readonly string[] KnownLoaders = { "upc_r2_loader64.dll", "uplay_r2_loader64.dll", "uplaypc_r2_loader64.dll" };

    private readonly string _tempDir;

    public UbisoftDeploymentService()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString().Replace(".", "") ?? "unknown";
        _tempDir = Path.Combine(Path.GetTempPath(), "OSTGUI_Ubisoft", ver);
    }

    private static void Log(string message)
    {
        LogService.Event($"[Ubisoft] {message}");
        System.Diagnostics.Debug.WriteLine($"[Ubisoft] {message}");
    }

    /// <summary>在游戏目录（含常见 Unity 插件子目录 *_Data\Plugins\x86_64）探测 loader，返回 loader 完整路径；未找到返回 null</summary>
    public string? DetectLoader(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir)) return null;

        var candidates = new List<string> { gameDir };
        foreach (var dataDir in Directory.EnumerateDirectories(gameDir, "*_Data", SearchOption.AllDirectories))
            candidates.Add(Path.Combine(dataDir, "Plugins", "x86_64"));

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var loader in KnownLoaders)
            {
                var path = Path.Combine(dir, loader);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    /// <summary>确保模拟器 DLL 已解压到临时目录（缺失即解压，返回 DLL 路径）</summary>
    private string EnsureExtracted()
    {
        Directory.CreateDirectory(_tempDir);
        var dll = Path.Combine(_tempDir, EmbeddedDll);
        if (!File.Exists(dll))
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"OSTGUI.Assets.Ubisoft.{EmbeddedDll}")
                ?? throw new InvalidOperationException($"内嵌资源缺失: OSTGUI.Assets.Ubisoft.{EmbeddedDll}");
            using var fs = File.Create(dll);
            s.CopyTo(fs);
        }
        if (!File.Exists(dll))
            throw new InvalidOperationException($"解压后仍缺少模拟器 DLL（可能被杀软隔离），请检查 {_tempDir}");
        return dll;
    }

    /// <summary>部署：备份 → 替换为模拟器（按 loader 原名落位，upc_r2_loader64.dll 同名直覆盖）→ 写 uplay_r2.ini（loader 同目录）</summary>
    public (bool success, string message) Deploy(string loaderPath, string language = "en-US")
    {
        try
        {
            if (string.IsNullOrEmpty(loaderPath) || !File.Exists(loaderPath))
                return (false, $"未找到 loader（{loaderPath}），请先在免育碧面板选择游戏目录并确认检测到 loader");

            var loaderName = Path.GetFileName(loaderPath);
            var dir = Path.GetDirectoryName(loaderPath)!;

            // 1. 备份原 loader（已备份则跳过）
            var bak = loaderPath + ".bak";
            if (!File.Exists(bak))
                File.Copy(loaderPath, bak);
            else
                Log("备份已存在，跳过备份");

            // 2. 替换为模拟器 DLL（目标名 = 原 loader 名）
            File.Copy(EnsureExtracted(), loaderPath, true);

            // 3. 写 uplay_r2.ini（与 loader 同目录，emu.cpp 按 lib_path 读）
            File.WriteAllText(Path.Combine(dir, IniFileName), BuildIni(language), new System.Text.UTF8Encoding(false));

            Log($"已部署：{loaderName} → Goldberg R2（原文件备份为 {loaderName}.bak），uplay_r2.ini 已生成于 {dir}");
            return (true, $"部署完成：{loaderName} → Goldberg R2（原文件已备份为 {loaderName}.bak）");
        }
        catch (Exception ex)
        {
            Log($"部署失败: {ex.Message}");
            return (false, $"部署失败: {ex.Message}");
        }
    }

    /// <summary>还原：删除模拟器与 ini，把 .bak 改回原 loader 名</summary>
    public (bool success, string message) Restore(string loaderPath)
    {
        try
        {
            if (string.IsNullOrEmpty(loaderPath)) return (false, "loader 路径为空");
            var dir = Path.GetDirectoryName(loaderPath)!;
            var bak = loaderPath + ".bak";
            if (File.Exists(bak))
            {
                if (File.Exists(loaderPath)) File.Delete(loaderPath);
                File.Move(bak, loaderPath);
            }
            var ini = Path.Combine(dir, IniFileName);
            if (File.Exists(ini)) File.Delete(ini);

            Log($"已还原 {Path.GetFileName(loaderPath)} 并删除 {IniFileName}");
            return (true, "已还原原版 loader");
        }
        catch (Exception ex)
        {
            Log($"还原失败: {ex.Message}");
            return (false, $"还原失败: {ex.Message}");
        }
    }

    /// <summary>生成默认 uplay_r2.ini（Phase 1：账号 Goldberg + 语言 + 存档默认 + 空 DLC 段）</summary>
    private static string BuildIni(string language)
    {
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "en-US","zh-CN","zh-TW","es-MX","es-ES","ru-RU","pt-PT","pt-BR",
            "ja-JP","ko-KR","fr-FR","de-DE","it-IT","tr-TR","pl-PL","nl-NL","sv-SE"
        };
        if (!valid.Contains(language)) language = "en-US";
        return
            "[Settings]\r\n" +
            "Username = Goldberg\r\n" +
            "UserId = c0d3c0d3-c0d3-c0d3-c0d3-c0d3c0d3c0d3\r\n" +
            "Email = gold@berg\r\n" +
            $"Language = {language}\r\n" +
            "SaveType = 0\r\n" +
            "SavePath = SAVE_GAMES\r\n" +
            "GenerateNewId = true\r\n" +
            "\r\n[DLC]\r\n";
    }
}