# OSTGUI 开发笔记（DEV-NOTES）

> 本文合并自原 plan1.md / plan2.md 两份沉淀文档，并更新至 2026-09-18 现状（GUI v1.4.1 + 配套内核 v1.1.3）。
> 收录原则：**项目结构与工程现状长期保留**；领域事实/机制查证/调研结论提取至工作区根 `doc/` 下的事实考证（GUI 侧）（Lua 语义、depot/manifest/key 关系、Denuvo 授权、480 联机调研等）；工程状态快照（构建命令产物、临时数字、"待提交"类状态）不再收录，避免过时。

## 1. 架构总览

三层模型：

1. **Steam 客户端**：下载/解密/运行；向服务器要三样东西——所有权、depot 解密密钥、manifest（文件清单）
2. **OST 内核**（OpenSteamTool DLL，注入 Steam）：劫持这些请求，用 `Steam\config\lua\*.lua` 的配置作答
3. **OSTGUI**：生成 Lua + 预下载 manifest 进 depotcache + 提供密钥/令牌；另含免 Steam 部署（NoSteamLauncher 类库）与联机启动（OnlineHost 宿主进程）

仓库结构：`main/`（WinUI 3 主程序）+ `NoSteamLauncher/`（免 Steam 部署类库，运行时二进制以 EmbeddedResource 内嵌）+ `OnlineHost/`（联机宿主，独立 exe，随 GUI 发布，详见 §7）+ `docs/`（声明与更新说明）。

## 2. 固定版本体系

- `setManifestid` → `ManifestOverrides` → 内核 `BuildDepotDependency` 直接 patch depot 条目的 gid；同时双向拦截 `GetManifestRequestCode`（出站换 code / 入站伪造 OK），实测可回退旧版
- 内核不写 `.acf` / depotcache，全部内存 hook；`pinApp` 已弃用
- OSTGUI 侧：
  - 入库勾选"写入固定版本配置" = 预写**注释形式** `--setManifestid(...)`，默认仍是自动更新
  - 库页切换锁定模式（`LuaConfigService.ToggleVersionModeAsync`）：auto→fixed 要求每个 depot 都有 setManifestid 覆盖（depot 全覆盖检查），缺则拒绝并列出缺失项
  - "补齐版本配置"（`RepairVersionConfigAsync`）：现取全部 depot 当前 GID 写入注释块（即始终为当前最新版）

## 3. 搜索与入库链路

- 搜索全部走 `cc=us`（AppID 详情、storesearch 主源、HTML 备源、关键词源均已统一），成人内容不再被 cn 区过滤，名称搜索可直接命中；能搜到 ≠ 能入库（还需密钥存在）
- 清单源与密钥源分离：MHub = 清单源（仅最新版），Sudama = 仅密钥源
- **GitHub (Auiowu) 源已废弃**（v1.3.x）：上游仓库 `SteamAutoCracks/ManifestHub` 自 2025-07-24 停更，本机实测 CS2 (730) / Palworld (1623730) / Deadlock (892970) / 黑神话悟空 (2358720) 四个分支全部 404，社区生态已迁移到 MHub API（Hubcap / ManifestHub3 / SteaMidra 等主流工具均不再走 GitHub 分支模式）。`DownloadFromGithubAsync` 方法、`GetSourceApiKey` / `GetSourceBaseUrl` 辅助方法、`ManifestSourceType.GitHub` / `GitHubSearch` 枚举值、`CustomGithubRepos` / `CustomZipUrls` / `CustomManifestSources` 字段、`AppConfig.GithubToken` 字段均已删除；同时清理其他 5 个从未接入的清单源预设（`sac` / `walftech` / `steamautocracks_v2` / `buqiuren` / `auto_github`）及对应枚举值。`IsImplementedSource` 仅认 `mhub` / `sudama`
- 入库勾选"下载 Manifest"（默认开）：MHub → Sudama(仅密钥) 级联；不勾则跳过清单直接生成 Lua，由内核运行时兜底取清单，成功提示注明兜底
- `LuaBuilder`：Sudama 密钥/令牌 → `MergeAllDepotsAsync` 用全量 depot 列表补全（防止只写有 manifest 的 depot 漏密钥）→ 缺密钥收集并通知。DLC 段也对每个 DLC AppID 查 `keys[dlcId]`，Sudama 收录的独立 DLC depot key 会自动写入 `addappid(dlcId, 1, "<key>")`
- 缺解密密钥警告在两种模式下都保留（无 key 无法解密下载加密内容）

## 4. Sudama 缓存（v1.3.0 现状）

- 缓存文件：`%LOCALAPPDATA%\OSTGUI\sudama_cache.json`（约 22 万条 / 16.5MB）、`token_cache.json`
- 缓存**存在即用、不自动过期**（已去掉 24h TTL，08-28 起：缓存存在即用，不再自动刷新）；仅当无缓存文件时才会自动下载；新密钥/令牌靠设置页**手动刷新**或**手动导入本地文件**（浏览器直连快于应用内时使用，按文件名/内容自动识别类型）才能拿到
- 下载策略：密钥与令牌并行；流式接收；单次超时 max(120, 设置值)；重试间隔 1.5s；成功日志带条数/体积/耗时
- Sudama 无按需查询接口，只有全量端点；勿每次入库实时拉全量
- 隐藏调优参数 `DownloadTimeout`（config.json，默认 120，无 UI）：同时影响清单文件下载（max(60,·)）与 Sudama 缓存下载（max(120,·)）的超时；早期版本曾有设置控件，08-14 起移除仅留字段

## 5. 日志系统（含线程教训）

- 双轨：**运行时日志**（`LogService.AddLog`，内存集合绑定设置页，可复制/清空）与**应用日志文件**（`AddAppLog` → `%LOCALAPPDATA%\OSTGUI\logs\ostgui.log`，保留 N 行）
- ⚠️ **教训**：内存集合绑定着界面，非 UI 线程写入必须在 `AddLog` 内部经 DispatcherQueue 封送（现已内置）。此前后台服务直接写集合，异常沿调用方反向炸出，表现为各种莫名其妙的 NRE 且掩盖真实错误——排查这类"错误信息看不懂"的问题时优先怀疑跨线程 UI 操作
- `ConfigureAwait(false)` 不能防止上述问题：它只决定续延跑在哪个线程，真正的防线是集合操作点的封送

## 6. 免 Steam 部署（NoSteamLauncher）

流程：选游戏 EXE + AppID → （有壳时）Steamless 脱壳替换 EXE → GSE(Goldberg) 模拟器部署进游戏目录 → 可选 SteamAPICheckBypass（winmm 劫持隐藏模拟器痕迹）。原文件备份为 `.bak`，删除 `steam_settings` 并改回 `.bak` 即可还原。

- 对齐 SAC（SteamAutoCrack）的部署逻辑与 ini 配置格式；无壳游戏自动跳过脱壳不中断
- 老游戏（2016 前 SDK）会额外生成 `steam_settings/steam_interfaces.txt`，gbe_fork 需要
- **资源嵌入机制**：所有二进制（Steamless CLI/插件、GSE 模板、Bypass）以 EmbeddedResource 打进 NoSteamLauncher.dll，运行时解压到 `%TEMP%\OSTGUI_NoSteamLauncher\<版本>\` 并做关键文件完整性校验，缺失自动重解压
- ⚠️ **教训**：.gitignore 里全局 `*.dll/*.exe` 曾差点把这些资源挡在版本控制外——凡"运行必需的二进制"入库时务必显式反向规则确认
- 已知冲突：自带 winmm 依赖或反作弊的游戏对 Bypass 可能不适配（默认关闭）

## 7. 联机（两条路线）

内核只认一条：`-onlinefix`。GUI 因此提供两条互不依赖的启动路线，**不要混用**（一次只走一条）。页面结构：联机页顶部 Segmented 切「内核原生」（`Views/OnlineFixView`）/「其他」（`Views/OtherOnlineView`）。

### 7.1 路线 A：内核原生（联机页「内核原生」）

- 启动：`steam.exe -applaunch <游戏 AppID> -onlinefix=<会话 AppID>`。内核在 `SpawnProcess` 的 VEH 回调里把 pGameID 改成会话身份（默认 Spacewar 480，`-onlinefix=<appid>` 可换；其它 `-onlinefix*` 写法一律回落 480），全链路的身份改写（persona / LobbyInvite / GamesPlayed / P2P 翻转门控）都以该值为准。
- 检测与停止：GUI 扫描**命令行含 `-onlinefix` 的进程**（排除 `steam.exe` 自身）——命令行是通过读 **PEB** 拿的（x64 布局：PEB +0x20 → `RTL_USER_PROCESS_PARAMETERS`，+0x70 → CommandLine 的 `UNICODE_STRING`）；停止即 Kill 这些进程。
- 前提：Steam 已启动并登录（在线模式）。

### 7.2 路线 B：宿主 480（联机页「其他 → DLL 注入」）

- 由独立进程 `OnlineHost.exe "<游戏 exe>" <会话 AppID>` 完成（源码 `OnlineHost/Program.cs`，随 GUI 发布、与主程序同目录）。宿主行为：设 `SteamAppId`/`SteamGameId` 环境变量 → best-effort 加载**游戏目录里自带的** `steam_api64.dll`（BFS ≤3 层，覆盖 Unity 的 `*_Data\Plugins\x86_64`）并调 `SteamAPI_InitFlat`（拿不到就退 `InitSafe`；连 DLL 都没有就跳过——**环境变量才是关键**）→ 把游戏**作为子进程**拉起 → 等它退出 → `SteamAPI_Shutdown`。
- 原理：游戏进程自己以该身份初始化 Steam（appid / 大厅 / P2P 证书 / 叠加层天然一致），**完全不需要内核改写**。代价是它不经过内核的 `-onlinefix` 路径，所以内核那份会话状态必须已经清干净（v1.1.3 起随游戏进程退出即清，见内核 DEV-NOTES §2）。
- 游戏 exe 由 GUI 按 AppID 自动解析：`libraryfolders.vdf` 找库 → `appmanifest_<appid>.acf` 的 `installdir` → 目录同名 exe，否则取目录里最大的 exe（排除 CrashHandler / vcredist / unins）。
- 停止：先从宿主命令行里取**最后一个带引号的 `.exe`**（第一个可能是宿主自身）→ 按进程名结束游戏 → 再结束宿主。
- 实测（2026-09-17）：这条路走的是**真大厅**——内核日志出现 `Recv k_EMsgClientMMSUserJoinedLobby(6619)` 与 `LobbyChatMsg(6614)`，PEAK 好友邀请**由用户与好友实测成功进房**；对照 09-02 用内核原生路线测 PEAK，纯好友邀请是死路（能弹邀请界面但进不去）。

### 7.3 显示不对称不是故障

480 方案下双方都以 Spacewar(480) 身份运行，好友列表里互相显示"正在玩 480"属预期。反过来若看到好友被显示成真实游戏名（如 PEAK），那是**本机内核残留了 onlinefix 状态**在改写入站 persona（日志 `Patched friend persona entries (480 -> 3527290)`）——v1.1.3 起该状态随游戏进程退出清空。

### 7.4 一次只能一条

内核侧的会话状态只有一份（`-onlinefix` 语义），所以同一时间只能有一个 480 会话；宿主路线的并发由 GUI 按 `OnlineHost` 进程数管理（`IsHostRunning`）。

## 8. WinUI 3 踩坑合集（两轮合并）

- 分段切换用 CommunityToolkit **Segmented**（Pivot/TabView 效果不对）；`SymbolIcon` 无 FontSize 属性（WMC0011），统一 `FontIcon Glyph`
- 桌面应用没有 `Windows.UI.Colors` → 用 `Microsoft.UI.Colors`
- 附加属性 C# 侧用 `ToolTipService.SetToolTip()`，对象初始化器赋值编译不过
- ContentDialog 必须设 `XamlRoot`（Page 用 `this.XamlRoot`，Window 用 `RootGrid.XamlRoot`）
- 纯图标透明按钮：`Background=Transparent` + `BorderThickness=0` + `Padding=8,4`
- **点击空白取消输入框激活（09-02 起有解，此前结论作废）**：两个 WinUI 已知 bug 叠加——① [#4364](https://github.com/microsoft/microsoft-ui-xaml/issues/4364) 点击空白把焦点投给 ScrollViewer 内第一个可聚焦控件（→ 误激活首个输入框）；② [#10051](https://github.com/microsoft/microsoft-ui-xaml/issues/10051) 输入框已聚焦时点空白不转移焦点（→ 无法取消激活）。**方案=两层兜底**：
  - **页面锚点**：每个含输入框页面的 ScrollViewer 内容首位放 1×1 透明可聚焦 Grid（`IsTabStop=True, TabIndex=0, Opacity=0`）→ 拦截 #4364 的 fallback。已覆盖：设置页、联机页、D加密页(Transfer 面板)、S.A.C 页。
  - **全局回收**：`MainWindow.RootGrid` 挂 `PointerPressed`（`handledEventsToo:true`），点击处不在交互控件内时 `DispatcherQueue.TryEnqueue` 把焦点拉回全局锚点 `GlobalFocusAnchor`——排队保证晚于框架指针处理，从而覆盖 #10051 与 #4364。
  - **交互判定** `IsInteractive` 白名单：输入控件 + ComboBox/Slider/ToggleSwitch/ListViewBase/ScrollBar/ButtonBase；`ponytail:` 注释标注——新增交互控件类型需补清单。
  - **已知副作用**：点交互控件后键盘焦点回全局锚点（键盘 Tab 从头开始，鼠标无感）；ContentDialog 等弹层不冒泡到 RootGrid 故不生效；仅指针触发，键盘操作不受影响。
  - ⚠️ 历史记录"PointerPressed/Tapped 失焦方案全部无效"**作废**：当年失败根因是**没有可聚焦的焦点目标**；引入"可聚焦锚点"后主动拉焦成立。
- 浅色主题下按钮图标/文字需适配 `TextFillColorPrimaryBrush` 等 ThemeResource
- **RadioButton 的分组语义（09-17 踩到）**：有 `GroupName` 时分组根取**整个视觉树**（XamlRoot 范围，源码 `dxaml/xcp/dxaml/lib/RadioButton_Partial.cpp:519-522`：`groupNameExists ? VisualRelativeKind_Root : VisualRelativeKind_Parent`），**没有 GroupName 才按直接父容器分组**。两个页面/视图各用一组同名 `GroupName` 会串成一组，组内自动取消会把共享的布尔写空（症状：两个圈都不选中）。修法是**不写 GroupName**、靠隐式分组各成一组；`RadioButtons` 容器虽然忽略 GroupName，但它的布局盒与圆圈行有 4px 偏差，做左对齐时不要用。
- **打包时 `Views\` 下的 `.xbf` 也要拷**（09-17 差点发出去）：`CopyWinUIResourcesToPublish` 原先只拷 `Pages\*.xbf`，新增视图资源的包在别人机器上一打开该页就 `XamlParseException`——本机 Debug 目录里有文件，**本机测不出来**。现改为递归 `$(TargetDir)**\*.xbf`（并 `Exclude` publish 自身，否则会复制出 `publish\publish`）。
- ⚠️ 强调色的主题陷阱：`SystemAccentColor` 基础色**不随应用深浅主题翻转**；深色模式下需要"提亮版强调填充"的场景应使用 `AccentFillColorDefaultBrush` 等画刷
- **ContentDialog 恒为深色是刻意行为**：弹窗位于弹出层，不继承应用 `RequestedTheme`，浅色模式下也渲染成深色。曾尝试显式同步主题，但 WinUI 浅色弹窗对比度差、观感不佳，遂回退保留深色——勿当 bug 修复
- **构建**：只能用 VS MSBuild（`dotnet build/publish` 缺 PRI 任务必挂）；首次 Release 自包含发布需先带 RID Restore（运行时包要从源下载，直连 nuget.org 失败时可切国内镜像）；旧实例不关会 MSB3021 锁 exe
- 版本号只在 csproj 维护三处（Version/AssemblyVersion/FileVersion），运行时从程序集读取

## 9. 服务索引（当前）

| 服务 | 职责 |
|---|---|
| `GameSearchService` / `SteamSearchProvider` | 搜索编排 / Steam 官方 API 搜索源 |
| `SteamGameInfoService` | 统一查询：depot + manifest gid + DLC 列表与名称（优先走社区非官方 API `api.steamcmd.net`——注意并非 Valve 官方，由 github.com/steamcmd/api 项目运营；失败回退官方 `store.steampowered.com/api/appdetails`，大陆网络下通常不可达）|
| `ManifestDownloadService` | 多源清单下载 + 生成 Lua（门面已移除）|
| `LuaBuilder` / `LuaConfigService` | Lua 生成（补全 depot/key/token/DLC/固定版本）；Lua 读写与版本模式切换 |
| `SudamaKeyCache` | 密钥/令牌缓存（存在即用不自动过期、并行下载、手动刷新与本地导入）|
| `LibraryScanner` | 扫描 Lua 目录、检测错误 |
| `NoSteamLauncherService` / `NoSteamLaunchOrchestrator` | 免 Steam 部署封装 / 编排（Steamless + GBE + Bypass）|
| `OnlineFixService` | 联机两条路线（§7）：内核原生（`steam.exe -applaunch <appid> -onlinefix=<session>` + PEB 读命令行检测/停止）与宿主 480（拉起 `OnlineHost.exe`、按 AppID 解析游戏 exe、从宿主命令行反查游戏进程后结束）|
| `SteamDllService` | 三 DLL 是否已注入 / 内核 `opensteamtool.toml` 读写（`[denuvo] mode`、`[lua] paths`）/ **内核版本读取**（读已部署 `OpenSteamTool.dll` 的 Windows 版本资源）|
| `TicketService` / `OstFileService` / `SteamTicketExtractor` | Denuvo 授权管理 / .ost 导入导出 / 在线提取 |
| `ConfigService` / `LogService` / `ToastService` / `GameNameCacheService` | 配置 / 日志 / 通知 / 名称缓存 |

## 10. 已知限制（仍然有效的）

- MHub 只支持最新清单
- 补齐版本配置写入的是当前 GID
- 名称缓存 30 天 TTL，游戏改名后会显示旧名（08-28 起机制：仅主游戏入缓存；库页显示纯缓存读 + 后台静默补名；联机/DLC 等显示名处实时查询不参与缓存）
- DLC token 无按需查询渠道：生成 Lua 时会逐个查 Sudama 全量缓存并写入命中的 `addtoken`，但 dump 未收录的受限 DLC 没有补充途径
- 480 联机一次只能跑一个（内核侧会话状态只有一份；宿主路线由 GUI 按 `OnlineHost` 进程数管理，见 §7.4）
- Sudama 大文件下载速度取决于服务器线路，慢时走浏览器下载 + 手动导入

## 11. 创意工坊（Workshop）下载（Phase 1 已落地）

- 创意工坊内容走 depot 加密管线：Steam 客户端把订阅的 item 当作 **depot = consumer_app_id（游戏 AppID）** 下载（depotcache 命名 `<AppID>_<manifestID>.manifest`），解密密钥按 ConfigStore `...\<AppID>\DecryptionKey` 读取
- OST 内核已覆盖两环：manifest code（`GetManifestRequestCode` 劫持 + 第三方源仅按 gid 查询，workshop gid 被收录即可）与密钥注入（`ConfigStoreGetBinary` hook 从 Lua 的 DepotKeySet 取 key）
- 缺口曾是 Lua 主游戏行 `addappid(appid)` 不带密钥 → 内核无 key 可喂 → 订阅下载报"内容仍处于加密"
- Phase 1（v1.3.x）：`LuaBuilder` 主游戏行自动带上 Sudama depotkeys 中 AppID 自身的密钥（社区称"创意工坊密钥"，须恰好 64 位 hex）→ **新入库**即具备工坊下载解密能力；已入库游戏需重新入库或手动把主行改为 `addappid(appid, 1, "<key>")`
- 边界：**限制匿名的工坊**（部分游戏/新 manifest 的 code 请求被服务器拒绝）无法绕过，需真实拥有该游戏的账号；**老式独立 workshop depot**（SteamDB 标注 Workshop 的 depot，如 Dying Light）需该 depot 单独密钥，Phase 1 不覆盖

## 12. 文档索引

- 事实考证（Lua 语义 / depot·manifest·key 关系 / Denuvo 授权 / 480 联机调研）：工作区根 `doc/` 下的事实考证（GUI 侧）
- 重要事件与调研：`../../doc/EVENTS/`（工作区根 doc 下）
- 按日台账：`docs/dev/agents-log/`（本地留存，不纳入 git）