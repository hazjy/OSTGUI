# OSTGUI 开发笔记（DEV-NOTES）

> 何时读：接手 GUI 仓库、改模块边界、查服务落点或交付形态时。
> 管什么：三层模型、目录与进程、12 条模块链路、交付形态、服务索引、已知限制。
> 不写什么：机制细节见各 `REF-*.md`；清单源与密钥缓存见 `SOURCES.md`；文档地图见工作区根 `README.md`。

## 1. 三层模型

- Steam 客户端负责下载、解密、运行，并向服务器索取所有权、depot 密钥与 manifest。
- OST 内核（`OpenSteamTool.dll`，注入 Steam）拦截这些请求，用 `<Steam>\config\lua\*.lua` 作答；内核侧见 `ZSteamTool/docs/dev/DEV-NOTES.md`。
- OSTGUI 生成 Lua、预投喂 manifest、发密钥与令牌，另提供免 Steam 部署与联机启动。
- 目录：`main/`（WinUI 3 主程序）、`NoSteamLauncher/`（部署类库，二进制内嵌）、`OnlineHost/`（联机宿主，独立 exe）、`docs/`（更新说明与本笔记）。
- 进程：一个 GUI、一个独立 `OnlineHost.exe`、若干短命子进程（`--stats-apply`、`--extract-ticket`）与长驻 `--trainer-monitor`；`main/Program.cs` 自写 `Main` 让监控子进程在 WinUI 初始化前返回。

## 2. 模块链路

- **搜索 → 入库**：`SteamGameInfoService` 取 depot / gid / DLC → 清单双写 → `SudamaKeyCache` 补密钥与令牌 → `LuaBuilder` 生成 Lua；点播没拿全的 depot 交内核运行时补；全链可取消，两处原子写不可打断。见 `REF-入库与Lua.md`、`SOURCES.md`。
- **清单按需投喂**：`ManifestLogWatcher` 尾随 `content_log.txt`，见「取不到请求码」才补一份，串行 1 份/秒。见 `SOURCES.md`。
- **请求码源**：`ManifestLuaService` 把设置页勾选渲染成 `manifest.lua`，内核逐源级联；该文件存在时旁路内核 `[manifest] url`。见 `SOURCES.md`。
- **固定版本**：GUI 只写注释形式 `--setManifestid(...)`，锁版本在内核 hook；库页锁定模式与「补齐版本配置」见 `REF-清单与版本.md`。
- **免 Steam 部署 / 免育碧**：Steamless 脱壳 + GSE 铺装 + 一键还原；免育碧走 Goldberg R2（实验性、挂起）。见 `REF-免Steam部署.md`。
- **联机**：`OnlineFixService` 统一入口，三条路线（内核原生 `-onlinefix`、宿主 480、AppID Changer 文件法）一次只走一条。见 `REF-联机.md`。
- **封面**：`CoverImageService` 只给文件路径，位图由 VM 在 UI 线程构造；入库封面落 `covers\`。见 `REF-资产与封面.md`。
- **显示效果**：设置页三档（无 / 云母 / 亚克力）写 `BackdropMode`，`MainWindow.ApplyBackdrop()` 生效。见 `REF-界面与主题.md`。
- **日志**：单一日志流（`LogService.Diag()` 与 `Event()` 同实现），文件 `%LOCALAPPDATA%\OSTGUI\logs\ostgui.log`，按 `LogMaxLines` 裁剪。见 `REF-日志与诊断.md`。
- **检查更新**：`UpdateService` 并行读 GUI 与内核两个仓库的 GitHub Releases（302 主源、API 备源），同一版本只提示一次。
- **配置**：`ConfigService` → `%LOCALAPPDATA%\OSTGUI\config.json`，改动只写内存、退出统一落盘；预置源字段以代码为准，见工作区根 `doc/开发经验.md` §三。
- **成就编辑**：勾选只写本地 `achievements\<appid>.json`，点保存才 spawn `--stats-apply` 用 `ISteamUserStats013` 写回。见 `REF-成就.md`。

## 3. 交付形态（AOT）

- `build-aot.bat` → `.build\OSTGUI\publish-aot\`，日常 Debug 用 `build-jit.bat`；csproj 不写 `PublishAot`，一律命令行传参，restore 与 publish 分两次且都带 AOT 属性，否则静默出 JIT 包。产物判据：无 `coreclr.dll` / `OSTGUI.dll` / `OSTGUI.runtimeconfig.json`，约 97 MB / 250 余个文件。
- AOT 六类硬约束（绑定成员手写、集合属性声明 `IList<T>`、投影用 `As<T>()`、JSON 源生成、绑定集合显式赋 `List<T>`、日志 LOH 节流）见 `REF-AOT适配.md`。

## 4. 服务索引

| 服务 | 职责 |
|---|---|
| `GameSearchService` / `SteamSearchProvider` / `SteamGameInfoService` | 搜索编排 / depot、gid、DLC 查询（先 `api.steamcmd.net`，回退 appdetails） |
| `ManifestDownloadService` / `ManifestLogWatcher` / `ManifestLuaService` | 多源清单下载 / 按需投喂 / 渲染 `manifest.lua` |
| `LuaBuilder` / `LuaConfigService` / `SudamaKeyCache` | Lua 生成与读写、密钥与令牌缓存 |
| `NoSteamLauncherService` / `NoSteamLaunchOrchestrator` / `UbisoftDeploymentService` / `OnlineFixService` | 免 Steam 部署与还原 / 免育碧 / 联机三条路线 |
| `LibraryScanner` / `CoverImageService` / `AppInfoVdf` / `SteamDllService` / `TicketService` / `OstFileService` / `SteamTicketExtractor` | 库扫描与错误检测 / 封面 / `appinfo.vdf` / DLL 注入与内核配置 / Denuvo 授权 / `.ost` 导入导出 / 在线提票 |
| `AchievementStore` / `SteamStatsSchema` / `SteamStatsService` + `SteamStatsChild` / `AchievementListCache` | 成就留底、schema 解析、子进程写回、列表缓存 |
| `TrainerCatalogService` / `TrainerDownloadService` / `TrainerBindingService` / `TrainerMonitor` / `ConfigService` / `LogService` / `ToastService` / `GameNameCacheService` / `UpdateService` / `VersionCompare` / `OstMemory` | 修改器链路；配置 / 日志 / 通知 / 名称缓存 / 检查更新 / 版本比较 / 大缓冲后压 LOH |

## 5. 已知限制

- MHub 按 depot+gid 取，旧分支 gid 也能取到，取决于该 gid 是否被收录；「补齐版本配置」写入的仍是当前 gid。
- 名称缓存 30 天 TTL、只收主游戏（改名后仍显示旧名）；DLC token 无按需查询渠道，dump 未收录的受限 DLC 没有补充途径。
- 480 联机一次只能一个（内核只有一份会话状态）；Sudama 大文件慢时改用浏览器下载 + 手动导入。
- 创意工坊：限制匿名的工坊无法绕过，老式独立 workshop depot 的密钥不覆盖；部署类：Bypass 对自带 winmm / 反作弊游戏可能不适配（默认关），免育碧只覆盖传统单 loader 的 R1/R2。

## 6. REF 索引（按主题查子文档）

| 文档 | 一句话 |
|---|---|
| `REF-入库与Lua.md` | Lua 各行语义与参数规则、搜索与入库链路契约、应用内 Lua 编辑器 |
| `REF-清单与版本.md` | depot·manifest·gid·key 关系、取码门槛、固定版本体系与实现 |
| `SOURCES.md` | 清单源 / 请求码源 / 密钥与令牌缓存的唯一权威（含投喂位置） |
| `REF-AOT适配.md` | AOT 下必须这么写的八条约束与实测数值 |
| `REF-免Steam部署.md` | 免 Steam 与免育碧两条部署链路的实现事实、组件与字段级契约、边界 |
| `REF-联机.md` | 三条联机路线（宿主 / 文件 / 命令行会话）、会话身份与 PEB 读取 |
| `REF-授权与Denuvo.md` | Denuvo 授权（`.ost` 导入导出）与 D 加密身份模式读写 |
| `REF-成就.md` | 假入库游戏成就为何显示不出来、成就页实现、游玩时长归属 |
| `REF-创意工坊.md` | 创意工坊下载（Phase 1）的实现事实与限制 |
| `REF-修改器.md` | 修改器下载源与进程绑定 |
| `REF-资产与封面.md` | 封面 CDN 取值链、缺失标记、DLC 名数据源、ImageUrl 与缩略图 |
| `REF-界面与主题.md` | 显示效果三档（无 / 云母 / 亚克力）实测 |
| `REF-日志与诊断.md` | 单一日志流、文件侧时序、崩溃钩子 |
| `THIRD-PARTY-NOTICES.md` | 第三方组件与许可证 |

