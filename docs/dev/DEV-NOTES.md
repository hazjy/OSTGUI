# OSTGUI 开发笔记（DEV-NOTES）

> 只留核心架构：三层模型、仓库与进程结构、模块链路与数据流、交付形态（Native AOT）、服务索引、已知限制；分工与文档地图见工作区根 `README.md`，工程状态快照不收。
## 1. 架构总览
1. **Steam 客户端**：下载 / 解密 / 运行；向服务器索取所有权、depot 解密密钥与 manifest。
2. **OST 内核**（OpenSteamTool DLL，注入 Steam）：劫持这些请求，以 `<Steam>\config\lua\*.lua` 的配置作答（内核侧机制见 `ZSteamTool/docs/dev/DEV-NOTES.md`）。
3. **OSTGUI**：生成 Lua、预下载 manifest 进 depotcache、提供密钥与令牌；另含免 Steam 部署（NoSteamLauncher 类库）、免育碧（实验性，挂起）与联机启动（OnlineHost 宿主进程）。

仓库结构：`main/`（WinUI 3 主程序）+ `NoSteamLauncher/`（免 Steam 部署类库，运行时二进制以 EmbeddedResource 内嵌）+ `OnlineHost/`（联机宿主，独立 exe，随 GUI 发布）+ `docs/`（更新说明与开发笔记）。

进程形态：一个 GUI 进程 + 一个独立 `OnlineHost.exe` + 若干子进程（`--stats-apply` 写成就、`--extract-ticket` 提票、`--trainer-monitor` 长驻监控）。入口 `main/Program.cs` 自写 `Main`（csproj 用 `DISABLE_XAML_GENERATED_MAIN` 关掉 XAML 生成的 Main），唯一目的是让 `--trainer-monitor` 在 WinUI 初始化之前返回——那步会白装整套 UI 栈（实测 107 MB 工作集）；`OnlineHost.exe` 必须保持独立进程，合并进 GUI 会变成"启动即失败的空壳"（见 `main/OSTGUI.csproj` 注释）。
## 2. 模块与链路一览
- **搜索 → 入库链路**：搜索（Steam 官方 API 主源）→ 取 depot / manifest gid / DLC（`SteamGameInfoService`）→ 清单下载并**双写** `config\depotcache` 与 Steam 根 `depotcache`（`ManifestDownloadService` + `ManifestFileService`）→ 密钥 / 令牌（`SudamaKeyCache`）→ 生成 Lua（`LuaBuilder` → `LuaConfigService`）；入库可取消（`ct` 贯穿全链，两处原子写不可打断）。出处：`docs/dev/REF-入库与Lua.md`，各源状态见 `docs/dev/SOURCES.md`。
- **清单按需投喂**：尾随 Steam 自己的 `<Steam>\logs\content_log.txt`（内核那份日志只在 Debug 编入，Release 连文件都没有 → 监听会静默失效），看到"取不到请求码"才补：持久层 `config\depotcache` 命中就就地拷进根 depotcache，否则下载一份并双写。串行 1 份/秒（MHub 2 并发即 429）、失败不记入已处理、下次随 Steam 再试；设置页开关控制，关闭时什么都不做。出处：`main/Services/ManifestLogWatcher.cs` 类注释。
- **请求码源（内核运行时）**：设置页「清单源 → 请求码源」勾选 → `ManifestLuaService` 渲染成 `<lua 目录>\manifest.lua`（勾选顺序即级联顺序，**全关 = 短路版**），内核 Lua 钩子在每次 `GetManifestRequestCode` 时逐源取码（每源可带自己的 UA）。⚠️ **Lua 优先级高于内核 `[manifest] url`**，该文件一旦存在就会旁路内核那三个内置 provider；勾选一变就**直接覆盖**（无二次确认、不留备份，设置页用黄字提醒自行备份）。每源「测活」做在 GUI 侧——manifest.lua 的失败是静默的。出处：`main/Services/ManifestLuaService.cs` 类注释 + `docs/dev/SOURCES.md`。
- **固定版本体系**：GUI 只写 Lua（注释形式 `--setManifestid(...)` = 固定版本配置），实际锁版本由内核 hook 完成；库页切锁定模式（`LuaConfigService.ToggleVersionModeAsync`，要求 depot 全覆盖）与「补齐版本配置」（`RepairVersionConfigAsync`）。出处：`docs/dev/REF-清单与版本.md` + 内核 DEV-NOTES。
- **免 Steam 部署（NoSteamLauncher）**：Steamless 脱壳 + GSE(Goldberg) 部署 + 可选 Bypass，另有「一键还原」；三层 = 宿主类库 / GBE 部署服务 / 编排器。**两道锁独立**：Steamless 解 exe 上的 SteamStub（启动锁），Goldberg 顶 `steam_api*.dll` 的 Steamworks 调用（功能锁：成就 / DLC / 联机 / 云存档）——只脱壳对"能容忍无 Steam 初始化"的游戏已可运行，但没有功能层。出处：`docs/dev/REF-免Steam部署.md`。
- **免育碧（实验性，挂起）**：`UbisoftDeploymentService` 探测 `upc_r2` / `uplay_r2` / `uplaypc_r2` 系 loader（Unity 游戏还要扫 `*_Data\Plugins\x86_64\`）→ 备份 → 换 Goldberg R2 → 写 `uplay_r2.ini`，支持还原。只解"免 UC 客户端"一层；D 密世代与多组件 Unity 游戏的覆盖边界见 §「已知限制」。
- **联机（三条路线）**：`OnlineFixService` 统一入口，**一次只走一条**——① 内核原生（`steam.exe -applaunch <游戏> -onlinefix=<会话 AppID>`，读 PEB 扫命令行检测 / 停止）② 宿主 480（拉起 `OnlineHost.exe`，游戏 exe 按 AppID 自动解析）③ AppID Changer（`--appid-txt` 写 `steam_appid.txt` + 台账还原）。②③ 共用 `IsHostRunning` 判并发；内核侧只有一份会话状态。出处：`docs/dev/REF-联机.md`。
- **封面图**：`CoverImageService` 只返回文件路径（不碰 WinUI 类型），位图由 VM 在 UI 线程构造；入库封面落盘 `%LOCALAPPDATA%\OSTGUI\covers\`（缺失标记 `.miss2`），搜索页缩略图刻意只走内存不落盘；列表 / 网格共用同一份位图，切档只切宿主可见性。出处：`docs/dev/REF-资产与封面.md`。
- **显示效果（无 / 云母 / 亚克力）**：设置页下拉 → `config.json` 的 `BackdropMode` → `MainWindow.ApplyBackdrop()` 改 `Window.SystemBackdrop`；「无」档由 `SolidBackdrop` 自己铺底。出处：`docs/dev/REF-界面与主题.md`。
- **日志**：**单一日志流**——`LogService.Diag()` 与 `Event()` 行为完全一致，留两个名字只为让调用处读得出语义（异常 / 失败 vs 流水）。文件 `%LOCALAPPDATA%\OSTGUI\logs\ostgui.log`：`[yyyy-MM-dd HH:mm:ss.fff] [p<pid>] [D] msg`，追加写 + `FileShare.ReadWrite`（GUI / 监控 / stats 子进程共用同一份）；**按行数裁剪**——设置里的 `LogMaxLines` 就是硬上限，超出即重写为最后若干行，不留 `.1` / `.2` 备份。崩溃走 `LogService.Fatal()`（文件留 `ToString()` 全栈）。日志栏是会话内全量，面板只渲染尾部若干行；子进程没有视图、只能写文件。联机宿主另写 `onlinehost.log`。
- **检查更新**：`UpdateService` **直接读 GitHub Releases，不需要上传或维护任何清单文件**——一次检查同时看**两个仓库**（GUI `hazjy/OSTGUI` 与内核 `hazjy/ZSteamTool`），两者**并行**取最新 tag；主源是 `releases/latest` 的 302 `Location`（关掉自动重定向，只读响应头、连 body 都不读），备源是 `api.github.com` 同名端点的 `tag_name`，每源 5 秒硬超时。版本比较在 `VersionCompare`（纯逻辑、零依赖，可单独跑）：按小数点逐位比，远端某位更大才算有更新、更小即停、相等继续，缺位按 0。本机内核版本读**已部署 DLL 的 Windows 版本资源**（`SteamDllService.GetKernelVersion`），**未装内核则该维度不参与**判定与提示。提示是**一条**系统通知、最多两个按钮（`前往内核发布页` / `前往 GUI 发布页`；按钮参数由 `Program` 的 `NotificationInvoked` 转给 `UpdateService.HandleNotificationArgument`），文案只有三种组合（只有内核 / 只有 GUI / 两者都有）——由纯函数 `BuildToastPlan` 生成、`ToastService.ShowUpdatePlan` 渲染，「关于」弹窗那行小字由 `DescribeForDialog` 统一给，两者都在 `UpdateService.SelfCheck()` 里逐项断言（挂在 `--trainer-selftest` 链上）。自动检查在启动 5 秒后、受设置页「接收更新推送」控制，**同一版本只提示一次且按维度各自记账**（`config.json` 的 `NotifiedUpdateVersion` / `NotifiedKernelUpdateVersion`；自动检查时已提示过的维度不再出现在通知里，两者都提示过就静默），手动检查（「关于」弹窗）不受限。出处：`main/Services/UpdateService.cs`、`VersionCompare.cs` 头注释。
- **配置与状态**：`ConfigService` → `%LOCALAPPDATA%\OSTGUI\config.json`（**改动只写内存，退出时统一落盘**）；视图档位 `LibraryViewMode` / `SearchViewMode`、联机「其他」下拉 `OnlineOtherMode`、成就页来源勾选 `AchievementShowLua/Owned`、`BackdropMode`、清单按需投喂 `ManifestFeedEnabled`、请求码源 `RequestCodeSources`、检查更新 `UpdateCheckEnabled` 等偏好都落在这一份里。
- **成就编辑（成就页）**：左侧 = `LibraryScanner` 扫出的入库游戏（重开走 `AchievementListCache`，正版候选池由 `AppInfoVdf` 读 `appcache\appinfo.vdf`）；成就定义读本地 `<Steam>\appcache\stats\UserGameStatsSchema_<appid>.bin`（二进制 KV，`SteamStatsSchema`）。勾选**只写本地留底** `%LOCALAPPDATA%\OSTGUI\achievements\<appid>.json`；点「保存到 Steam」才 spawn `OSTGUI.exe --stats-apply`（`SteamStatsChild`，短命子进程 + 结果 JSON 文件，理由与 `SteamTicketExtractor` 相同）用 SAM 封装（`main/SteamApi/`，zlib）→ `ISteamUserStats013` 写回。**写入会进 Valve（重启 Steam 后仍在）**，但内核会对 addappid 游戏清空 819 里的成就数据 → 成就页可能显示不出来（显示层问题，不是没写进去）；证据与边界见 `docs/dev/REF-成就.md`。
## 3. 交付形态：Native AOT（跨模块）
- **打包**：`build-aot.bat`（Release + `PublishAot=true`，**restore 与 publish 必须分两次调用且都带 AOT 属性**，否则静默产出 JIT 包）→ `.build\OSTGUI\publish-aot\`（日常 Debug 构建用 `build-jit.bat`）。csproj 里**故意不写** `PublishAot`，一律命令行传参；脚本自带两道自检（产物里有 `coreclr.dll` 即判 AOT 被跳过；`OnlineHost.exe` 无参自检须 exit=2）。
- **产物事实**：无 `coreclr.dll` / `OSTGUI.dll` / `OSTGUI.runtimeconfig.json`；约 97 MB / 250 余个文件（`OSTGUI.exe` 单文件约 41 MB）。文件数随 WinUI 语言资源与 SDK 版本浮动，只记量级。内存收益与实测数值见 `docs/dev/REF-AOT适配.md`。
- **六类硬约束**（被 XAML 绑定的成员要手写 / 集合属性声明 `IList<T>` / XAML→CLR 投影用 `As<T>()` / JSON 必须源生成 / 绑定集合显式赋 `List<T>` / 日志面板 LOH 节流）：落脚与证据全在 `docs/dev/REF-AOT适配.md`——**动绑定与序列化之前先读它**。
## 4. 服务索引（当前）
| 服务 | 职责 |
|---|---|
| `GameSearchService` / `SteamSearchProvider` / `SteamGameInfoService` | 搜索编排与 Steam 官方 API 源 / 统一查询：depot + manifest gid + DLC 列表与名称（优先非官方 `api.steamcmd.net`——并非 Valve 官方；失败回退官方 `store.steampowered.com/api/appdetails`，大陆网络下通常不可达）|
| `ManifestDownloadService` / `ManifestLogWatcher` | 多源清单下载（门面已移除）/ 按需投喂：尾随 Steam `content_log.txt`，命中持久层就搬、否则下载并双写；串行 1 份/秒，失败下次随 Steam 重试 |
| `ManifestLuaService` | 请求码源 → 渲染并写入 `<lua 目录>\manifest.lua`（级联版；全关写短路版）；勾选一变就覆盖、不留备份；渲染与顺序有 `--trainer-selftest` 自检 |
| `LuaBuilder` / `LuaConfigService` | Lua 生成（补全 depot/key/token/DLC/固定版本）；Lua 读写与版本模式切换 |
| `SudamaKeyCache` | 密钥 / 令牌缓存（存在即用不自动过期、并行下载、手动刷新与本地导入）；入库取键走**流式扫描** |
| `CoverImageService` | 入库卡片封面：静态 CDN 链 → 官方 appdetails 兜底 → 缺失标记 `.miss2`，落盘 `covers\` |
| `LibraryScanner` | 扫描 Lua 目录、检测错误 |
| `NoSteamLauncherService` / `NoSteamLaunchOrchestrator` / `UbisoftDeploymentService` | 免 Steam 部署封装与编排（Steamless + GBE + Bypass）+ 一键还原 / 免育碧：探测 loader → 备份 → 换 Goldberg R2 → 写 `uplay_r2.ini`，支持还原（实验性，挂起） |
| `OnlineFixService` | 联机三条路线：内核原生（`-onlinefix` + PEB 读命令行）、宿主 480（`OnlineHost.exe` + 按 AppID 解析游戏 exe + 反查进程结束）、AppID Changer 文件法（`--appid-txt` + 台账还原 + 启动巡检） |
| `SteamDllService` | 三 DLL 是否已注入 / 内核 `opensteamtool.toml` 读写（`[denuvo] mode`、`[lua] paths`）/ **内核版本读取**（读已部署 `OpenSteamTool.dll` 的 Windows 版本资源） |
| `TicketService` / `OstFileService` / `SteamTicketExtractor` | Denuvo 授权管理 / .ost 导入导出 / 在线提取 |
| `ConfigService` / `LogService` / `ToastService` / `GameNameCacheService` | 配置 / 日志 / 通知 / 名称缓存 |
| `AppInfoVdf` / `AchievementListCache` | 读 `appcache\appinfo.vdf`（客户端已知的 app：类型 + 名字，成就页正版候选池）/ 成就页左侧列表缓存（lua 目录或 appinfo 变化即失效；拥有集合只在点「刷新」时重查） |
| `AchievementStore` / `SteamStatsSchema` / `SteamStatsService` + `SteamStatsChild` | 成就编辑：本地留底 JSON / 解析本地 schema（二进制 KV）/ 父进程 spawn 子进程；子进程侧用 `ISteamUserStats013` 读写 Steam 成就 |
| `TrainerCatalogService` / `TrainerDownloadService` / `TrainerBindingService` / `TrainerMonitor` | 修改器目录与下载（**搜索走站点官方 RSS** `?s=&feed=rss2`，XDocument 解析，只留 `/trainer/` 条目；详情页正则取附件直链；下载需浏览器 UA + Referer + 自行跟 302，内容嗅探 zip → 解压 → `.part` 原子落盘；已下载只认 `trainers.json` 索引）/ 进程绑定：`bindings.json`（GUI 唯一写者、监控按 mtime 热重载）+ 监控子进程 `OSTGUI.exe --trainer-monitor`（每 2s：游戏在→起修改器；游戏退→只结束自己启动过的那个；无启用绑定自退；`Global\OSTGUI_TrainerMonitor` 单实例） |
| `OstMemory` | 大缓冲用完手动压一次 LOH（治标：让已提交内存还回去；调用点都选在"用户刚干完一件事"） |
| `UpdateService` / `VersionCompare` | 检查更新：读 GitHub Releases（**GUI 与内核两个仓库**并行，302 `Location` 主源 + API 备源，各 5 秒硬超时；合成一条通知、最多两个按钮；文案与去重在 `SelfCheck()` 里断言）/ 版本比较与 tag 解析（纯逻辑、零依赖，可单独验） |
## 5. 已知限制（仍然有效的）
- MHub **不是"仅最新"**：按 depot+gid 取，旧分支 gid 也能取到（2026-10-25 实测 CS2 `1.40.8.8`、BG3 `release_patch6_hf9` 均 200 + 内层 magic）；不存在的 gid → 404 `{"success":false,"error":"Manifest not found"}`。「补齐版本配置」写入的仍是当前 GID
- 名称缓存 30 天 TTL，游戏改名后会显示旧名（机制：仅主游戏入缓存；库页 = 纯缓存读 + 后台静默补名；联机 / DLC 等显示名处实时查询不参与缓存）
- DLC token 无按需查询渠道：生成 Lua 时会逐个查 Sudama 全量缓存并写入命中的 `addtoken`，dump 未收录的受限 DLC 没有补充途径
- 480 联机一次只能跑一个（内核侧会话状态只有一份；宿主路线由 GUI 按 `OnlineHost` 进程数管理）
- Sudama 大文件下载速度取决于服务器线路，慢时走浏览器下载 + 手动导入
- 创意工坊：限制匿名的工坊无法绕过；老式独立 workshop depot 的密钥 Phase 1 不覆盖
- 部署类：免 Steam 部署下自带 winmm 依赖或有反作弊的游戏对 Bypass 可能不适配（默认关闭），`.bak` 已存在即保留不覆盖（要重拍备份先删掉它）；免育碧只覆盖传统单 loader 的 R1/R2 游戏，UNO 类多组件 Unity 游戏与 SecureDLC 世代覆盖不到
- 已知实现缺陷清单（`addtoken` 键错配、两条 lua 写入路径不一致、Steamless 插件目录误删游戏自带 `Plugins` 等）见 `docs/dev/REF-缺陷台账.md`
## 6. 文档索引（已收敛）
分工与文档地图见工作区根 `README.md`，本节不再复述。
