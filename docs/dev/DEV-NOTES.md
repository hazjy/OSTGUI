# OSTGUI 开发笔记（DEV-NOTES）

> 只留架构：三层模型、目录与进程、模块链路、交付形态、服务索引、已知限制。
> 机制细节见各 `REF-*.md`，清单源状态见 `SOURCES.md`，分工与文档地图见工作区根 `README.md`。

## 1. 三层模型

- Steam 客户端负责下载、解密、运行，并向服务器索取所有权、depot 密钥与 manifest。
- OST 内核（`OpenSteamTool.dll`，注入 Steam）拦截这些请求，用 `<Steam>\config\lua\*.lua` 作答；内核侧见 `ZSteamTool/docs/dev/DEV-NOTES.md`。
- OSTGUI 生成 Lua、预投喂 manifest、发密钥与令牌，另提供免 Steam 部署与联机启动。

目录：`main/`（WinUI 3 主程序）、`NoSteamLauncher/`（部署类库，运行时二进制以 EmbeddedResource 内嵌）、`OnlineHost/`（联机宿主，独立 exe）、`docs/`（更新说明与本笔记）。

进程：一个 GUI、一个独立 `OnlineHost.exe`、若干短命子进程（`--stats-apply`、`--extract-ticket`）与长驻 `--trainer-monitor`。`main/Program.cs` 自写 `Main`（csproj 关掉 XAML 生成的 Main），让监控子进程在 WinUI 初始化前返回，省掉整套 UI 栈（实测 107 MB 工作集）；`OnlineHost.exe` 保持独立进程，合并进 GUI 会变成启动即失败的空壳。

## 2. 模块链路

每条只写入口与落点，机制见对应 REF。

- **搜索 → 入库**：Steam 官方 API 搜索 → `SteamGameInfoService` 取 depot / gid / DLC → 清单双写 `config\depotcache` 与 Steam 根 `depotcache` → `SudamaKeyCache` 补密钥与令牌 → `LuaBuilder` 生成 Lua。点播源没拿全的 depot 交给内核运行时补。全链可取消，两处原子写不可打断。见 `REF-入库与Lua.md`、`SOURCES.md`。
- **清单按需投喂**：`ManifestLogWatcher` 尾随 `<Steam>\logs\content_log.txt`，见"取不到请求码"才补一份，串行 1 份/秒，失败下次随 Steam 重试；设置页开关控制。
- **请求码源**：设置页勾选经 `ManifestLuaService` 渲染成 `<lua 目录>\manifest.lua`，内核每次取码逐源级联（顺序即勾选顺序，全关写短路版）。该文件存在时旁路内核 `[manifest] url`，勾选一变即覆盖。见 `SOURCES.md`。
- **固定版本**：GUI 只写注释形式 `--setManifestid(...)`，锁版本在内核 hook；库页锁定模式与「补齐版本配置」见 `REF-清单与版本.md`。
- **免 Steam 部署**：Steamless 解 exe 的 SteamStub、Goldberg 顶 `steam_api*.dll` 的 Steamworks 调用，两层独立；另有一键还原。见 `REF-免Steam部署.md`。
- **免育碧**：探测 `upc_r2` / `uplay_r2` / `uplaypc_r2` loader，备份后换 Goldberg R2 并写 `uplay_r2.ini`，可还原。实验性，挂起。
- **联机**：`OnlineFixService` 统一入口，一次只走一条：内核原生 `-onlinefix`、宿主 480、AppID Changer 文件法。见 `REF-联机.md`。
- **封面**：`CoverImageService` 只给文件路径，位图由 VM 在 UI 线程构造；入库封面落 `covers\`，搜索页缩略图只走内存。见 `REF-资产与封面.md`。
- **显示效果**：设置页三档（无 / 云母 / 亚克力）写 `BackdropMode`，`MainWindow.ApplyBackdrop()` 换 `Window.SystemBackdrop`。见 `REF-界面与主题.md`。
- **日志**：单一日志流，`LogService.Diag()` 与 `Event()` 同实现；文件 `%LOCALAPPDATA%\OSTGUI\logs\ostgui.log`，按 `LogMaxLines` 裁剪且不留备份；崩溃走 `Fatal()` 带全栈。见 `REF-日志与诊断.md`。
- **检查更新**：`UpdateService` 并行读 GUI 与内核两个仓库的 GitHub Releases（302 `Location` 主源、API 备源，各 5 秒超时），合成一条通知；自动检查里同一版本只提示一次，按维度各自记账。`VersionCompare` 为零依赖纯逻辑。
- **配置**：`ConfigService` → `%LOCALAPPDATA%\OSTGUI\config.json`，改动只写内存，退出统一落盘。预置源的名称、URL、顺序、能力标记以代码为准，启动时由 `MergeDefaults` 覆盖（见工作区根 `doc/开发经验.md` §三）。
- **成就编辑**：勾选只写本地留底 `achievements\<appid>.json`，点保存才 spawn `--stats-apply` 子进程用 `ISteamUserStats013` 写回；写入会进 Valve，内核随后清空 819 里的成就数据，成就页可能显示不出来。见 `REF-成就.md`。

## 3. 交付形态（Native AOT）

- `build-aot.bat` → `.build\OSTGUI\publish-aot\`；日常 Debug 用 `build-jit.bat`。csproj 不写 `PublishAot`，一律命令行传参；restore 与 publish 分两次调用且都带 AOT 属性，否则静默出 JIT 包。
- 产物判据：无 `coreclr.dll`、`OSTGUI.dll`、`OSTGUI.runtimeconfig.json`；约 97 MB / 250 余个文件。
- 六类硬约束（绑定成员手写、集合属性声明 `IList<T>`、XAML→CLR 投影用 `As<T>()`、JSON 源生成、绑定集合显式赋 `List<T>`、日志面板 LOH 节流）见 `REF-AOT适配.md`，动绑定与序列化之前先读。

## 4. 服务索引

| 服务 | 职责 |
|---|---|
| `GameSearchService` / `SteamSearchProvider` / `SteamGameInfoService` | 搜索编排与官方 API 源 / depot、gid、DLC 查询（先 `api.steamcmd.net`，回退官方 appdetails） |
| `ManifestDownloadService` / `ManifestLogWatcher` | 多源清单下载 / 按需投喂 |
| `ManifestLuaService` | 请求码源 → 渲染写入 `manifest.lua` |
| `LuaBuilder` / `LuaConfigService` | Lua 生成（补 depot key / token / DLC / 固定版本）、读写与版本模式切换 |
| `SudamaKeyCache` | 密钥与令牌缓存，入库取键走流式扫描 |
| `CoverImageService` | 入库封面：CDN → appdetails 兜底 → 缺失标记 `.miss2` |
| `LibraryScanner` | 扫 Lua 目录、检测错误 |
| `NoSteamLauncherService` / `NoSteamLaunchOrchestrator` / `UbisoftDeploymentService` | 免 Steam 部署与一键还原 / 免育碧（实验性） |
| `OnlineFixService` | 联机三条路线与会话判并发 |
| `SteamDllService` | DLL 注入状态、内核 `opensteamtool.toml` 读写、内核版本读取 |
| `TicketService` / `OstFileService` / `SteamTicketExtractor` | Denuvo 授权 / `.ost` 导入导出 / 在线提票 |
| `ConfigService` / `LogService` / `ToastService` / `GameNameCacheService` | 配置 / 日志 / 通知 / 名称缓存 |
| `AppInfoVdf` / `AchievementListCache` | 读 `appinfo.vdf` / 成就页列表缓存 |
| `AchievementStore` / `SteamStatsSchema` / `SteamStatsService` + `SteamStatsChild` | 成就留底、schema 解析、子进程写回 Steam |
| `TrainerCatalogService` / `TrainerDownloadService` / `TrainerBindingService` / `TrainerMonitor` | 修改器目录、下载、进程绑定与监控子进程 |
| `OstMemory` | 大缓冲用完后压一次 LOH |
| `UpdateService` / `VersionCompare` | 检查更新 / 版本比较与 tag 解析 |

## 5. 已知限制

- MHub 按 depot+gid 取，旧分支的 gid 也能取到；能否取到取决于该 gid 是否被收录，不存在的 gid 回 404。「补齐版本配置」写入的仍是当前 gid。
- 名称缓存 30 天 TTL，游戏改名后仍显示旧名；缓存只收主游戏，联机与 DLC 的显示名实时查询、不参与缓存。
- DLC token 无按需查询渠道：生成 Lua 时查 Sudama 全量缓存，dump 未收录的受限 DLC 没有补充途径。
- 480 联机一次只能跑一个（内核侧只有一份会话状态）。
- Sudama 大文件下载速度取决于线路，慢时改用浏览器下载 + 手动导入。
- 创意工坊：限制匿名的工坊无法绕过；老式独立 workshop depot 的密钥 Phase 1 不覆盖。
- 部署类：免 Steam 部署下自带 winmm 依赖或有反作弊的游戏可能不适配 Bypass（默认关闭）；`.bak` 已存在即保留；免育碧只覆盖传统单 loader 的 R1/R2 游戏。
- 未修复的实现缺陷见 `REF-缺陷台账.md`。
