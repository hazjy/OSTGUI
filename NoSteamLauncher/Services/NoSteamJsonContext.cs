using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoSteamLauncher.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Native AOT 下的 System.Text.Json 源生成（替代反射序列化）。
//
// GBEDeploymentService.GenerateBypassConfig 原来用
// JsonSerializer.Serialize<Dictionary<string, object>>(..., JsonSerializerOptions)：
// 反射序列化在 AOT/裁剪下报 IL2026 + IL3050，且它序列化的值是**匿名类型**
// （new { mode = ..., to = ..., ... }）——匿名类型没法写进 [JsonSerializable]，
// 源生成解析不到运行时类型会直接抛 NotSupportedException，所以这里把匿名类型换成
// 下面这个显式模型（键名/键序/类型一一对齐，落盘字节不变）。
//
// 选项精确复刻改造前的 new JsonSerializerOptions { WriteIndented = true }：
//   - WriteIndented = true（2 空格缩进）
//   - DefaultIgnoreCondition = WhenWritingNull：原来每个匿名类型只有自己有那几个属性，
//     换成统一模型后必须忽略 null，否则 hide 规则会多出 "to": null / "file_must_exist": null
//   - encoder 保持默认（中文/非 ASCII 转义成 \uXXXX，与改造前一致；别改成 UnsafeRelaxedJsonEscaping）
// 注意：DefaultIgnoreCondition 在 JsonSourceGenerationOptionsAttribute 上表达不了 →
// 按 AppJsonContext.cs 里"甲"的做法走 new NoSteamJsonContext(options) 实例模式。
// 只有一个调用点，实例 static 缓存一次（别在调用点反复 new）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>SteamAPICheckBypass.json 的规则值（原来是匿名类型，见文件顶部说明）。</summary>
internal sealed class BypassRule
{
    // 属性名故意全小写：JSON 里的键就长这样，改名会改落盘格式。
    public string mode { get; set; } = "";

    public string? to { get; set; }

    public bool? file_must_exist { get; set; }

    public string? hook_times_mode { get; set; }

    public string? hook_time_n { get; set; }
}

/// <summary>SteamAPICheckBypass.json（缩进 + 默认 encoder，忽略 null 属性）</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, BypassRule>))]
internal partial class NoSteamJsonContext : JsonSerializerContext { }
