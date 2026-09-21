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
- **搜索页卡片（2026-09-21）**：与入库管理同构——左侧 120×56 缩略图 + 名称 + `AppID:` 行 + 右侧「入库」与「信息」（`&#xE946;` Info 图标）；**不放版本模式行**（搜索结果没有版本状态）
- **搜索结果缩略图：与入库封面同一套来源，只走内存不落盘**——`FetchThumbnailBytesAsync(appId)` 走 `HeaderTemplates`（两条 header 布局）→ **官方 `GetHeaderImageUrlAsync`** → null（占位图标）。**不再用 `SearchResult.ImageUrl`**：那是 storesearch 的 `tiny_image`（231×87 小胶囊，≈2.66:1），塞进 2.14:1 的卡片会被裁掉两侧——2026-09-21 用户报的"搜索页缩略图缺一块"就是它。上屏用 `SetSourceAsync(MemoryStream.AsRandomAccessStream())`、`DecodePixelWidth=120`；并发 4；卡片 `Stretch="Uniform"` 作保险（宁愿留边也不裁）；**不调用 `EnsureCoverFileAsync`**（那条会落盘）

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
- **一键还原（2026-09-20，用户要求"照抄 SAC"）**：模拟器页「一键还原」按钮，用**同一个游戏 EXE 选择**（取它的目录），还原语义逐条照抄 SAC `SteamAutoCrack.Core/Utils/Restore.cs` 四步 —— ① 目录里存在 `SteamAPICheckBypass.json` 才递归删 `version.dll`/`winmm.dll`/`winhttp.dll`（不做哈希校验）；② 递归删 `steam_interfaces.txt`/`local_save.txt`/`SteamAPICheckBypass.json`；③ 递归把**所有** `*.bak` 换回原名（SAC 是先删原文件再改名，这里用覆盖式 `File.Move`，结果一致且失败不丢备份）；④ 递归删所有 `steam_settings` 目录。注意第 ③ 步是通配，游戏自带的 `*.bak` 也会被换回原名，这是照抄 SAC 的既定代价（曾试过"只认 `steam_api*.dll.bak` + 选定 EXE 的 `.bak`"的白名单版，被要求改掉）
- **只动游戏目录、不解压资源**（不用 `EnsureExtracted()`）；逐项日志 + 还原后复查残留：被占用（游戏在跑）时逐条报失败并输出"还原未完成"，不报假成功；`%APPDATA%\GSE Saves\<appid>` 只提示路径不删（用户进度）；AppID Changer 写在 EXE 同目录的 `steam_appid.txt` 归它自己的台账管，还原不碰。幂等：无残留时输出"未发现模拟器残留（可能已还原）"
- 实测：夹具两轮（完整产物正常还原；`.bak` 被独占锁定时只该项失败、其余照做、报"还原未完成"）+ **用户真机 Ib 部署 → 还原后正常**

## 7. 联机（三条路线）

内核只认一条：`-onlinefix`。GUI 因此提供三条互不依赖的启动路线，**不要混用**（一次只走一条）。页面结构：联机页顶部 Segmented 切「内核原生」（`Views/OnlineFixView`）/「其他」（`Views/OtherOnlineView`，下拉里是「DLL 注入（推荐）」与「AppID Changer（轻量）」两种方式——**推荐**＝宿主预注册 + 垫片预载，兼容性最好；**轻量**＝只写文件、不加载任何东西——两者共用同一套输入与启停按钮，靠下拉选中项分派，选中项记进 `config.json` 的 `OnlineOtherMode`（0＝DLL 注入，1＝AppID Changer），下次进页面仍是上次那个）。两个分页的内容块（游戏 AppID + 查询 + 协议AppID + ▶■ + 状态行）样式与文字保持一致。

### 7.1 路线 A：内核原生（联机页「内核原生」）

- 启动：`steam.exe -applaunch <游戏 AppID> -onlinefix=<会话 AppID>`。内核在 `SpawnProcess` 的 VEH 回调里把 pGameID 改成会话身份（默认 Spacewar 480，`-onlinefix=<appid>` 可换；其它 `-onlinefix*` 写法一律回落 480），全链路的身份改写（persona / LobbyInvite / GamesPlayed / P2P 翻转门控）都以该值为准。
- 检测与停止：GUI 扫描**命令行含 `-onlinefix` 的进程**（排除 `steam.exe` 自身）——命令行是通过读 **PEB** 拿的（x64 布局：PEB +0x20 → `RTL_USER_PROCESS_PARAMETERS`，+0x70 → CommandLine 的 `UNICODE_STRING`）；停止即 Kill 这些进程。
- 前提：Steam 已启动并登录（在线模式）。

### 7.2 路线 B：宿主 480（联机页「其他 → DLL 注入」）

- 由独立进程 `OnlineHost.exe "<游戏 exe>" <会话 AppID>` 完成（源码 `OnlineHost/Program.cs`，随 GUI 发布、与主程序同目录）。宿主行为：设 `SteamAppId`/`SteamGameId`/**`SteamOverlayGameId`** 环境变量 → best-effort 加载**游戏目录里自带的** `steam_api64.dll`（BFS ≤3 层，覆盖 Unity 的 `*_Data\Plugins\x86_64`）并调 `SteamAPI_InitFlat`（拿不到就退 `InitSafe`；连 DLL 都没有就跳过——**环境变量才是关键**）→ 把游戏**作为子进程**拉起 → 等它退出 → `SteamAPI_Shutdown`。
- 原理：游戏进程自己以该身份初始化 Steam（appid / 大厅 / P2P 证书 / 叠加层天然一致），**完全不需要内核改写**。代价是它不经过内核的 `-onlinefix` 路径，所以内核那份会话状态必须已经清干净（v1.1.3 起随游戏进程退出即清，见内核 DEV-NOTES §2）。
- 游戏 exe 由 GUI 按 AppID 自动解析：`libraryfolders.vdf` 找库 → `appmanifest_<appid>.acf` 的 `installdir` → 目录同名 exe，否则取目录里最大的 exe（排除 CrashHandler / vcredist / unins）。
- **宿主日志 `%LOCALAPPDATA%\OSTGUI\logs\onlinehost.log`**（与 `ostgui.log` 同目录，设置页「打开日志文件」会落在同一个文件夹）：每次会话写会话头（身份 + 游戏 exe）→ 垫片路径 → 自注册结果；文件法另写写入/还原两行。
  - `SteamAPI_InitFlat` 已改传 `SteamErrMsg` 缓冲区，失败会打印结果码 + 错误串：**0=OK / 1=FailedGeneric / 2=NoSteamClient（Steam 没跑或没登录）/ 3=VersionMismatch（垫片与客户端版本不匹配）**——"联机点了没反应"先看这行。
  - 回退链是 **`SteamAPI_InitFlat` → `SteamAPI_InitSafe`**：2026-09-21 实测有些游戏自带的垫片只导出 `InitSafe`（星露谷那份），只认 InitFlat 会白白跳过自注册；两个都没有的老垫片才会打"跳过自注册"。
- 停止：先从宿主命令行里取**最后一个带引号的 `.exe`**（第一个可能是宿主自身）→ 按进程名结束游戏 → 再结束宿主。
- 实测（2026-09-17）：这条路走的是**真大厅**——内核日志出现 `Recv k_EMsgClientMMSUserJoinedLobby(6619)` 与 `LobbyChatMsg(6614)`，PEAK 好友邀请**由用户与好友实测成功进房**；对照 09-02 用内核原生路线测 PEAK，纯好友邀请是死路（能弹邀请界面但进不去）。

### 7.3 路线 C：AppID Changer（文件法，联机页「其他 → AppID Changer」）

- 做法：宿主 `OnlineHost.exe --appid-txt "<游戏 exe>" <会话 AppID>` 写**游戏 exe 同目录**的 `steam_appid.txt`，**同时设与路线 B 相同的那套环境变量**（`SteamAppId`/`SteamGameId`/`SteamOverlayGameId`）并同样先用游戏自带的 shim 自注册一次，再把游戏作为子进程拉起，游戏退出后按台账还原原文件。宿主把工作目录设成游戏 exe 目录，于是"文件放 CWD"与"放 exe 同目录"两种口径重合，不必赌 SDK 读哪个。
- ⚠️ **2026-09-20 修正（原设计作废）**：这条路线原来是"只写文件、不设环境变量、不加载垫片"，被实测推翻——只写文件时 Steam 虽然会 `AppID 480 adding PID …` 把游戏登记成 480，但**没有 `GameOverlay: started`**，也拿不到 480 的真大厅/叠加层身份。对照物是闭源工具 `CaIInstallNext`（见 §7.4 与工作区 `doc/GUI-事实考证.md`）：它给子进程带的是 `SteamAppId`/`SteamGameId`/**`SteamOverlayGameId`**=480（外加 `SteamEnv`/`SteamAppUser`/`SteamVirtualGamepadInfo`/`STEAM_COMPAT_*`），并先起一个带环境的自身副本去注册。我们跟进的是前三个（联机必需）+ 自注册 + 文件；后四项没跟（Steam 自己的账号名/手柄信息/着色器缓存路径，联机不需要）。
- 为什么还留文件：文件留在磁盘上，游戏自己再起子进程或被重开时仍然生效；环境变量只对宿主直接拉起的进程有效。文件 + 环境变量各管一半，闭源工具也是两样都写。
- 台账 `%LOCALAPPDATA%\OSTGUI\appid-changer.txt`（三行 = 游戏目录 / 原本有无该文件 / 原内容）由宿主**先写台账再动文件**：中途被杀也能还原。宿主被杀 / 断电留下的残留，由 GUI 启动时的 `RestoreAppIdFileLeftover()` 补还原（有台账且**没有 OnlineHost 在跑**才动手，避免打断进行中的会话）。
- 启动前校验：GUI 在拉起宿主后等最多 1.5 秒确认文件真写上（目录只读 / 被占时宿主会立刻失败退出，不能报假成功）。停止沿用路线 B 的 `StopViaHost()`（按宿主命令行反查游戏 exe → 结束游戏 → 宿主自行还原），该方法给宿主 3 秒自己收尾再强杀。
- 实测：① 2026-09-19 旧版（只写文件）——`gameprocess_log.txt` 有 `AppID 480 adding PID …PEAK.exe`、PEAK 进程内三个 steam 模块齐全，但**无叠加层启动记录**，用户实测判为"不生效"；② 2026-09-20 修正版——机械验证：子进程确实继承到 `SteamAppId`/`SteamGameId`/`SteamOverlayGameId=480`（PEB 直读），文件写入与退出还原、台账清理全部照旧；（真机联机效果待好友实测）
- 边界：只等宿主拉起的那个进程，"启动器 → 另起的 exe" 会提前还原（代码里已标 `ponytail:`）。

### 7.4 显示不对称不是故障

480 方案下双方都以 Spacewar(480) 身份运行，好友列表里互相显示"正在玩 480"属预期。反过来若看到好友被显示成真实游戏名（如 PEAK），那是**本机内核残留了 onlinefix 状态**在改写入站 persona（日志 `Patched friend persona entries (480 -> 3527290)`）——v1.1.3 起该状态随游戏进程退出清空。

### 7.5 一次只能一条

内核侧的会话状态只有一份（`-onlinefix` 语义），所以同一时间只能有一个 480 会话；宿主路线的并发由 GUI 按 `OnlineHost` 进程数管理（`IsHostRunning`，路线 B 与 C 共用）。

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
- **弹层主题（2026-09-20，两轮才查对，含一次错误结论）**：① **XAML 里声明**的 ContentDialog 挂在页面树里 → 继承 `RootGrid.RequestedTheme`（浅色应用 + 深色系统下「使用说明」弹窗实测浅色 `#E7E7E7`——我先只测了这一个就断言"弹层本来就跟随"，是错的）；② **代码 `new` 出来**的 ContentDialog 不在 XAML 树里 → 主题落到**系统主题**，浅色应用 + 深色系统下整片发黑（用户截图确认；10 个代码弹窗全中，账号弹窗为例）→ 现在统一在 `ShowAsync` 前调 `Helpers.PopupTheme.Apply(dialog)`，**只设 `RequestedTheme`，背景/画刷仍走框架默认**。旧记录「ContentDialog 恒为深色是刻意行为」只对第 ② 类成立，已作废
- **代码搭的弹窗内容取画刷**：不能用 `Application.Current.Resources[...]`——同样走**系统主题**。改成在页面/窗口 XAML 里放"取样点"（`BrushProbe`：`{ThemeResource CardBackgroundFillColorDefaultBrush}` 等），代码读它的 `Background`/`BorderBrush`/`Foreground`。已改 `MainWindow`（账号弹窗）与 `LibraryPage`（入库信息弹窗）；账号弹窗的"确认重启"改用框架 `AccentButtonStyle`，不再手工染色
- **弹层背景不走「显示效果」**：试过给弹层换纯色/亚克力画刷 → 浅色+亚克力下弹窗背景变近黑 `#171719`、深色菜单变浅灰 `#A3A3A3`，比框架默认难看，已回退（框架默认背景本身就是亚克力质感）
- **`App.xaml` 自定义画刷已清空**（2026-09-20）：`OstAccentBrush`/`Status*Brush`/`FixedVersionBrush`/`AutoVersionBrush` 七个键实测**零引用**，已删，现在只挂 `XamlControlsResources`
- **窗口状态持久化（2026-09-20 修，用户报"最小化/最大化关窗后下次启动显示效果异常"）**：真因是**两套状态存储打架 + 存了非还原态尺寸**。① 第三方库 `WindowStateSaver.WinUi3`（v0.0.1）会在 **exe 目录**落 `WindowStateSaveData.json`（`{Width,Height,X,Y,IsMaximized}`），与自家 `config.json` 的 `WindowWidth/Height` **双写**；它恢复"最大化"标志时用的却是自己那份过期尺寸 → 窗口以"最大化"标志配小矩形出现。② 自家代码用 `AppWindow.Size` 存尺寸：**最小化关闭存的是 353x56**（图标态矩形）、**最大化关闭存 3868x2080**（工作区尺寸，比屏幕还"满"）→ 下次启动 `AppWindow.Resize` 成"非最大化但铺满/过小"的窗口，亚克力/云母看起来就不对。**修法**：删掉那个库（连同 PackageReference，顺带不再污染 exe 目录）+ 统一改用 Win32 `GetWindowPlacement().rcNormalPosition`（**永远是还原态矩形**，与最小化/最大化无关）存尺寸/位置、`IsZoomed()` 存最大化标志；恢复用 `SetWindowPos`（物理像素）并夹到当前显示器工作区，`IsWindowMaximized` 时再调 `OverlappedPresenter.Maximize()`。**验证**：正常/最大化/最小化三种关闭方式写进配置的尺寸完全一致 + 最大化标志正确；三种重启截图（亚克力正常、内容渲染正常）；exe 目录不再产出 `WindowStateSaveData.json`
- **启动初始化不能只挂一次性 `Activated`（2026-09-20 修，用户报"最大化关窗后重启是空白页、主页里 Steam 路径/DLL 都为空"）**：`OverlappedPresenter.Maximize()` 对尚未显示的窗口等价于 `ShowWindow(SW_MAXIMIZE)`——**当场显示并激活窗口**并同步抛出 `Activated`；当时订阅还没注册（构造函数里 `Maximize()` 在前、`this.Activated +=` 在后）→ 事件永久丢失；`App` 随后调用的 `Activate()` 对已激活窗口是 no-op，不会补发 → `InitializeAppAsync` 从未执行（不导航 = 空白页，`MainViewModel.InitializeAsync` 没跑 = Steam 路径/DLL 状态为空，两个定时器也没起）。**纪律**：① 窗口状态施加（最大化等会显示/激活窗口的动作）必须在窗口激活之后（现由 `App.OnLaunched` 在 `Activate()` 之后调 `ApplyStartupMaximizeIfNeeded()`）；② 初始化用**幂等入口** `EnsureInitialized()`，`Activated` 只是兜底，`Activate()` 之后也显式调一次；③ 初始化失败必须留日志（原来 `catch { }` 静默，导致"没跑"和"跑了但失败"无法区分，现打 `[Init] 初始化失败: …` + 成功时一行 `[Init] page=…, steam=…`）
- ⚠️ **量尺寸、截图都别用 DPI 不感知的进程**：本机 **3840x2160 @ 216 DPI（225%）**，DPI 不感知的 PowerShell 看到的是虚拟化值（1707x960 / 800x560），会把"最小尺寸"误读成"尺寸漂移"；**截图同理**——不感知的进程 `CopyFromScreen` 在 225% 下抓到的是错乱合成画面（最大化时看起来"卡片巨大、横向溢出"，其实布局完全正常，用户实测否定）。要截图/量尺寸先 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2 = -4)`。同理 `MinWindowWidth/MinWindowHeight = 800x560` 是**逻辑值**，在 225% 下对应的物理最小值是 **1800x1260** —— 恢复尺寸时若按物理像素夹取，必须先 `ScaleLogical()`，否则会把合理的窗口压到最小值。（本次排查第一版结论"AppWindow 与 Win32 单位不一致导致每次重启长大 1.44×"**是错的**：那其实是尺寸低于物理最小值被夹到 min 的表现）
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
| `CoverImageService` | 入库卡片封面：静态 CDN 链 → 官方 appdetails 兜底 → 缺失标记（`.miss2`），落盘 `%LOCALAPPDATA%\OSTGUI\covers\`；详见 §13 |
| `LibraryScanner` | 扫描 Lua 目录、检测错误 |
| `NoSteamLauncherService` / `NoSteamLaunchOrchestrator` | 免 Steam 部署封装 / 编排（Steamless + GBE + Bypass）+ 一键还原（照抄 SAC `Restore` 四步，见 §6）|
| `OnlineFixService` | 联机三条路线（§7）：内核原生（`steam.exe -applaunch <appid> -onlinefix=<session>` + PEB 读命令行检测/停止）、宿主 480（拉起 `OnlineHost.exe`、按 AppID 解析游戏 exe、从宿主命令行反查游戏进程后结束）、AppID Changer 文件法（`--appid-txt` + 台账还原 + 启动时补还原巡检）|
| `SteamDllService` | 三 DLL 是否已注入 / 内核 `opensteamtool.toml` 读写（`[denuvo] mode`、`[lua] paths`）/ **内核版本读取**（读已部署 `OpenSteamTool.dll` 的 Windows 版本资源）|
| `TicketService` / `OstFileService` / `SteamTicketExtractor` | Denuvo 授权管理 / .ost 导入导出 / 在线提取 |
| `ConfigService` / `LogService` / `ToastService` / `GameNameCacheService` | 配置 / 日志 / 通知 / 名称缓存 |

## 10. 已知限制（仍然有效的）

- MHub 只支持最新清单
- 补齐版本配置写入的是当前 GID
- 名称缓存 30 天 TTL，游戏改名后会显示旧名（08-28 起机制：仅主游戏入缓存；库页显示纯缓存读 + 后台静默补名；联机/DLC 等显示名处实时查询不参与缓存）
- DLC token 无按需查询渠道：生成 Lua 时会逐个查 Sudama 全量缓存并写入命中的 `addtoken`，但 dump 未收录的受限 DLC 没有补充途径
- 480 联机一次只能跑一个（内核侧会话状态只有一份；宿主路线由 GUI 按 `OnlineHost` 进程数管理，见 §7.5）
- Sudama 大文件下载速度取决于服务器线路，慢时走浏览器下载 + 手动导入

## 11. 创意工坊（Workshop）下载（Phase 1 已落地）

- 创意工坊内容走 depot 加密管线：Steam 客户端把订阅的 item 当作 **depot = consumer_app_id（游戏 AppID）** 下载（depotcache 命名 `<AppID>_<manifestID>.manifest`），解密密钥按 ConfigStore `...\<AppID>\DecryptionKey` 读取
- OST 内核已覆盖两环：manifest code（`GetManifestRequestCode` 劫持 + 第三方源仅按 gid 查询，workshop gid 被收录即可）与密钥注入（`ConfigStoreGetBinary` hook 从 Lua 的 DepotKeySet 取 key）
- 缺口曾是 Lua 主游戏行 `addappid(appid)` 不带密钥 → 内核无 key 可喂 → 订阅下载报"内容仍处于加密"
- Phase 1（v1.3.x）：`LuaBuilder` 主游戏行自动带上 Sudama depotkeys 中 AppID 自身的密钥（社区称"创意工坊密钥"，须恰好 64 位 hex）→ **新入库**即具备工坊下载解密能力；已入库游戏需重新入库或手动把主行改为 `addappid(appid, 1, "<key>")`
- 边界：**限制匿名的工坊**（部分游戏/新 manifest 的 code 请求被服务器拒绝）无法绕过，需真实拥有该游戏的账号；**老式独立 workshop depot**（SteamDB 标注 Workshop 的 depot，如 Dying Light）需该 depot 单独密钥，Phase 1 不覆盖

## 12. 显示效果（无 / 云母 / 亚克力）

- 入口：设置页「外观设置 → 显示效果」下拉；值存 `config.json` 的 `BackdropMode`（`none` / `mica` / `acrylic`），**默认 `acrylic`**（老配置没有这个键 → 落到默认值）
- 接线：`SettingsViewModel.BackdropIndex`（0/1/2）双向绑定下拉 → 改动触发 `BackdropChanged` → `SettingsPage.OnBackdropChanged` → `MainWindow.ApplyBackdrop(VM.BackdropMode)`；落盘复用既有的自动保存（`PropertyChanged → SaveAllToConfig`，加载期间由 `_isLoading` 抑制）
- 实现就一句：给 `Window.SystemBackdrop` 赋 `null` / `new MicaBackdrop()` / `new DesktopAcrylicBackdrop()`。`MainWindow.xaml` 里原来的静态 `<MicaBackdrop />` 已删掉，改代码单点控制；`MainWindow` 构造时就按配置执行一次，激活前就位、不闪一下默认底
- ⚠️ **`SystemBackdrop = null` 时窗口底色跟的是系统主题，不是应用主题**：浅色应用主题 + 深色系统时背景会露成灰/黑（实测采样 `#808080`、导航栏处 `#000000`）。所以「无」档由 `SolidBackdrop`（`RootGrid` 第一层的 Border，`{ThemeResource SolidBackgroundFillColorBaseBrush}`）自己铺底，云母/亚克力时隐藏让 backdrop 透出来
- 诊断：每次切换往应用日志写一行 `[Backdrop] <mode> -> <类名>`——"选了没效果"时先看这行在不在、类名对不对
- 系统要求：云母 Win11 22000+、亚克力 Win11 22621+；不支持时框架静默回落纯色底（不崩），设置页有一行小字说明
- 弹层（弹窗/菜单）**不**跟着这个设置换背景，只跟随深浅主题——实测与理由见 §8 那条「弹层走框架默认」

## 13. 封面图（入库管理卡片）

- 位置：卡片左侧 120×56（Steam `header.jpg` 原生比例 460×215），`Stretch="UniformToFill"` 吸收比例差；无图时露出底下的手柄图标占位（`Image` 直接盖在 `FontIcon` 上，不引入 null 判断转换器）
- 取值链（`CoverImageService`，信号量并发 4）：① `cdn.cloudflare.steamstatic.com/steam/apps/<id>/header.jpg` ② 新布局 `shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/<id>/header.jpg` ③ 同两处的 `capsule_616x353.jpg` ④ **官方 appdetails** 的 `header_image` → `capsule_image` → `capsule_imagev5`（仅对 ①–③ 全失败的条目调用，8s 超时，`SteamGameInfoService.GetHeaderImageUrlAsync`）
- ⚠️ **2024+ 新上架游戏在旧布局下没有 `header.jpg`**（2026-09-21 实测 PEAK/3527290 及 3548580/4001890 全部 404，静态猜不出来）→ 只有官方接口能拿到；官方返回的 URL 若落在 `*.akamai.steamstatic.com` / `steamcdn-a.akamaihd.net` / `media.steampowered.com`（本机 hosts 指向 127.0.0.1），自动换成等价 cloudflare 主机重试一次
- 缓存：`%LOCALAPPDATA%\OSTGUI\covers\<appid>.jpg`（命中不联网）；确无图的写 `<appid>.miss2`（TTL 1 天）。已知本来就无封面：`1716751`（育碧组件）——出现这类 `.miss2` 属正常，不是故障。**改 URL 链/兜底源时必须把 `MissSuffix` +1**，否则旧标记会在 TTL 内挡住新逻辑——2026-09-21 的"封面永远出不来"就是这么来的
- **存储尺寸 = 240×112**（`StoreWidth`）+ JPEG q85：卡片是 120×56 逻辑像素，200% DPI 的解码上限正好 240×112，存原始 460×215 等于 4/5 的字节白存。实测（2026-09-21）：迁移后 39 张 **1,921 KB → 361 KB**（平均 49.2 → 9.3 KB，最大 14 KB），1000 个游戏约 9 MB（原来约 48 MB）。编码用 `System.Drawing.Common`（已在 csproj 里，此前未被使用）。⚠️ **卡片调大时改 `StoreWidth`，并把 `MigrationMarker`（`.v2`）改名**触发一次性重编码
- 迁移：`covers\.v2` 标记 + 首次取图时后台**就地重编码**（不联网、不丢图；顺带遵守"改格式就 +1"的规则）
- **评估过但没采用：优先读 Steam 本地 `appcache\librarycache\<appid>\header.jpg`**——实测本地那份与 CDN 下的是**同一张图、逐字节完全相同**（12/12 命中样本），且覆盖率只有 **12/39（31%）**：它省网络请求、**省不了硬盘**，收益不值得再加一条取值路径
- 判定语义：只有 404/403 才算"确实没有"；超时/5xx/网络异常**不写标记**，下次重试（避免把"网络不通"记成"没有封面"）
- **官方接口的两种失败必须分开**：`GetHeaderImageUrlAsync` 内部重试 1 次，仍失败就**抛异常**（调用方按"接口暂时不可用"处理 → **不写标记**）；只有"应答正常但没有图片字段"才返回 null（= 确实没有 → 写 `.miss2`）。原实现把两者都当 null，一次偶发失败就把该游戏变成 1 天空白——2026-09-21 修（同一类坑的第二次）
- 诊断：每个失败条目往**日志文件**写一行带原因（`[Cover] 封面缺失（1 天内不再重试）: <id>（官方接口无图片字段）`）——"为什么这个游戏没封面"看这行
- **按需加载**：列表用 `ListView`（虚拟化），页面在 `ContainerContentChanging` 里对刚实体化的卡片调 `LibraryViewModel.EnsureCoverAsync` → 滚进视口才取图/建位图，滚出去回收后不重复取。实测：进页面只取 20 张（视口+缓冲），滚到底累计 33 张，而全量预加载是进来就 40 张一起发请求。⚠️ **换成 `ItemsControl` 等非虚拟化容器会让这条机制彻底失效**（40 张卡片会一次性全实体化）
- 线程：`CoverImageService` 只返回文件路径（不碰 WinUI 类型）；`BitmapImage` 由 VM 在 UI 线程构造，`DecodePixelWidth=120` + `DecodePixelType=Logical`（不设就是按 460×215 全量解码，几十张几十 MB）

## 14. 文档索引

- 事实考证（Lua 语义 / depot·manifest·key 关系 / Denuvo 授权 / 480 联机调研）：工作区根 `doc/` 下的事实考证（GUI 侧）
- 重要事件与调研：`../../doc/EVENTS/`（工作区根 doc 下）
- 按日台账：`docs/dev/agents-log/`（本地留存，不纳入 git）