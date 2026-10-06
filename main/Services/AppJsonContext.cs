using System.Text.Json;
using System.Text.Json.Serialization;
using OSTGUI.Models;

namespace OSTGUI.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Native AOT 下的 System.Text.Json 源生成（替代反射序列化）。
//
// 反射序列化在 AOT/裁剪下会报 IL2026 + IL3050，运行期还可能直接抛异常；main/ 里每一个
// JsonSerializer 调用都改成"带 JsonTypeInfo 的重载"（JsonSerializerContext）。
//
// 三套 context = 精确复刻改造前代码里那三套 JsonSerializerOptions，一个选项都不多加：
//
//   甲 AppJsonConfigContext   WriteIndented + UnsafeRelaxedJsonEscaping（中文原样写，不转义）
//        → ConfigService：%LOCALAPPDATA%\OSTGUI\config.json
//
//   乙 AppJsonIndentedContext WriteIndented（默认 encoder，中文转义成 \uXXXX）
//        → TrainerBindingService：bindings.json
//        → TrainerDownloadService.WriteIndex：trainers.json
//        → AchievementStore：achievements\<appid>.json（这套另外带 PropertyNameCaseInsensitive）
//        → OstFileService：*.ost
//        → SteamStatsChild（写结果文件）、App.xaml.cs（--extract-ticket 的输出文件）
//
//   丙 AppJsonCompactContext  紧凑无缩进（默认 encoder）
//        → AchievementListCache：cache\achievement-list.json
//        → GameNameCacheService：name_cache.json
//        → SudamaKeyCache：sudama_cache.json / token_cache.json
//        → SteamStatsService / SteamStatsChild 的临时文件（List<string> / List<AchievementRecord> / StatsChildResult）
//        → SteamSearchProvider：JsonElement
//        → SteamTicketExtractor：--extract-ticket 的结果
//
// 有两件事源生成属性表达不了，只能走 new AppJsonXxxContext(options) 的实例模式（把选项塞回
// context）——JsonSourceGenerationOptionsAttribute 既没有 Encoder 属性，也没有大小写开关
// （.NET 10 下写 Encoder = 直接 CS0246）：
//   - 中文不转义（甲）→ ConfigService 用 new AppJsonConfigContext(JsonOptions) 构造；
//   - PropertyNameCaseInsensitive → AchievementStore（乙）与 SteamStatsService（丙）各构造一个。
// 其余调用点里"选项本来就是一个 options 字段"的三处（AchievementListCache 丙、TrainerBindingService
// 乙，加上甲）同样用它构造实例，好处是那几套 options 保持唯一的定义处、不会变成没人读的死字段。
// 这 5 个实例都在各自调用类里 static 缓存一次（别在调用点反复 new：每次 new 都要重建类型信息）。
//
// 已知取舍：AppConfig.Extensions 是 Dictionary<string, object>，源生成只能序列化"运行时类型也在
// context 里"的值（string 可以；int / 自定义类型会抛 NotSupportedException）。代码里没有任何地方
// 往里写值（config.json 里恒为 {}），故不影响现有行为。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>甲：ConfigService 的 config.json（缩进 + 中文不转义）——实际用实例，见 ConfigService.JsonCtx</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ManifestSource))]
[JsonSerializable(typeof(List<ManifestSource>))]
[JsonSerializable(typeof(RequestCodeSource))]
[JsonSerializable(typeof(List<RequestCodeSource>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
internal partial class AppJsonConfigContext : JsonSerializerContext { }

/// <summary>乙：缩进 + 默认 encoder（bindings.json / trainers.json / achievements\*.json / *.ost / 子进程结果）</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(OstFile))]
[JsonSerializable(typeof(AchievementFile))]
[JsonSerializable(typeof(AchievementRecord))]
[JsonSerializable(typeof(List<AchievementRecord>))]
[JsonSerializable(typeof(TrainerBinding))]
[JsonSerializable(typeof(List<TrainerBinding>))]
[JsonSerializable(typeof(TrainerDownloadService.IndexEntry))]
[JsonSerializable(typeof(List<TrainerDownloadService.IndexEntry>))]
[JsonSerializable(typeof(StatsChildResult))]
internal partial class AppJsonIndentedContext : JsonSerializerContext { }

/// <summary>丙：紧凑无缩进 + 默认 encoder（name_cache / achievement-list 缓存 / sudama 缓存 / 临时文件）</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AchievementListCache.Snapshot))]
[JsonSerializable(typeof(AchievementListCache.Item))]
[JsonSerializable(typeof(List<AchievementListCache.Item>))]
[JsonSerializable(typeof(Dictionary<string, GameNameCacheService.CacheEntry>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SudamaCache))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(AchievementRecord))]
[JsonSerializable(typeof(List<AchievementRecord>))]
[JsonSerializable(typeof(IReadOnlyList<AchievementRecord>))]
[JsonSerializable(typeof(StatsChildResult))]
[JsonSerializable(typeof(SteamTicketExtractor.ExtractResult))]
[JsonSerializable(typeof(ReleaseInfo))]
[JsonSerializable(typeof(JsonElement))]
internal partial class AppJsonCompactContext : JsonSerializerContext { }
