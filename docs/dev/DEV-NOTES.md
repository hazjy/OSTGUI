# OSTGUI 开发笔记（DEV-NOTES）

> 本文合并自原 plan1.md / plan2.md 两份沉淀文档，并更新至 v1.3.0 现状。
> 收录原则：领域事实与踩坑经验长期保留；工程状态快照（构建命令产物、临时数字、"待提交"类状态）不再收录，避免过时。

## 1. 架构总览

三层模型：

1. **Steam 客户端**：下载/解密/运行；向服务器要三样东西——所有权、depot 解密密钥、manifest（文件清单）
2. **OST 内核**（OpenSteamTool DLL，注入 Steam）：劫持这些请求，用 `Steam\config\lua\*.lua` 的配置作答
3. **OSTGUI**：生成 Lua + 预下载 manifest 进 depotcache + 提供密钥/令牌；另含免 Steam 部署（NoSteamLauncher 类库）

仓库结构：`main/`（WinUI 3 主程序）+ `NoSteamLauncher/`（免 Steam 部署类库，运行时二进制以 EmbeddedResource 内嵌）+ `docs/`（声明与更新说明）。

## 2. Lua 配置语义（内核查证）

```lua
addappid(2001760)                                  -- 解锁主游戏（假装拥有）
addappid(2001761, 1, "64位hex")                   -- 解锁 depot + 注入解密密钥
addappid(2827030)                                 -- DLC：只解锁不带密钥（多数复用本体 key）
addtoken(2001760, "令牌")                          -- 受限 app 获取 appinfo 需要
setManifestid(2001761, "gid", 大小)               -- 固定版本（锁 depot 的 manifest）
```

- `addappid` 第二参数内核实际忽略（源码只读第 1、3 参数）；密钥必须恰好 64 字符否则不生效
- `--` 开头是注释，内核忽略；函数名不区分大小写；文件放 `config\lua\{AppId}.lua`，内核热重载
- `setManifestid` 有则锁版本，无则自动更新；大小可省略

## 3. depot / manifest / gid / key 关系（查证）

- **depot**：游戏内容分区（本体/DLC/语言包各一），ID 固定不随版本变
- **manifest**：某 depot 某版本的文件清单；"下哪些 depot"由 appinfo 决定，manifest 管"下哪些文件块"
- **gid**：manifest ID，代表某次内容快照，每次更新换新；完整版本 = 全部 depot 的 gid 组合
- **key**：AES-256 密钥 per-depot 且对所有用户相同 → 第三方密钥库可行的根本原因
- 有内容清单（manifests）的 depot 内容均为 AES-256 加密，下载必须有 key；也存在**无 manifests 的纯所有权壳 depot**（常见于 DLC 占位，SteamCMD 数据中连 manifests 字段都没有），本就没有可解密内容，裸 `addappid(depotId)` 即为正确写法，不应计入"缺密钥"警告

### manifest 获取门槛（查证）

- 两步：向 CM 发 `GetManifestRequestCode`（需登录且**拥有该 depot**，参数须匹配当前 appinfo）→ 拿 code 向 CDN 拉 `depot/{id}/manifest/{gid}/5/{code}`；code 约 5 分钟轮换
- 无权限账户拿不到 code（AccessDenied）
- 第三方清单库本质：有人用拥有权限的正版账号批量抓取共享（ManifestHub README、pjy612 讨论、52pojie 帖佐证）
- OST 内核拦截 code 请求伪造响应喂回客户端，manifest 本体仍从 Steam CDN 直连；上游 provider 链：opensteamtool → wudrm → steamrun，可用 Lua `fetch_manifest_code_ex` 自定义

### 直接改清单 ID 不能锁版本（查证）

- 改 depotcache 文件：只是缓存，不触发更新决策
- 改 `.acf` gid 为旧值：Steam 判定落后反而强制更到最新；社区做法是改成最新值骗过防更新，不是锁旧版
- 锁版本唯一正道：内核 hook（见 §4）

## 4. 固定版本体系

- `setManifestid` → `ManifestOverrides` → 内核 `BuildDepotDependency` 直接 patch depot 条目的 gid；同时双向拦截 `GetManifestRequestCode`（出站换 code / 入站伪造 OK），实测可回退旧版
- 内核不写 `.acf` / depotcache，全部内存 hook；`pinApp` 已弃用
- OSTGUI 侧：
  - 入库勾选"写入固定版本配置" = 预写**注释形式** `--setManifestid(...)`，默认仍是自动更新
  - 库页切换锁定模式（`LuaConfigService.ToggleVersionModeAsync`）：auto→fixed 要求每个 depot 都有 setManifestid 覆盖（depot 全覆盖检查），缺则拒绝并列出缺失项
  - "补齐版本配置"（`RepairVersionConfigAsync`）：现取全部 depot 当前 GID 写入注释块（即始终为当前最新版）

## 5. 搜索与入库链路

- 搜索全部走 `cc=us`（AppID 详情、storesearch 主源、HTML 备源、关键词源均已统一），成人内容不再被 cn 区过滤，名称搜索可直接命中；能搜到 ≠ 能入库（还需密钥存在）
- 清单源与密钥源分离：MHub = 清单源（仅最新版），Sudama = 仅密钥源
- **GitHub (Auiowu) 源已废弃**（v1.3.x）：上游仓库 `SteamAutoCracks/ManifestHub` 自 2025-07-24 停更，本机实测 CS2 (730) / Palworld (1623730) / Deadlock (892970) / 黑神话悟空 (2358720) 四个分支全部 404，社区生态已迁移到 MHub API（Hubcap / ManifestHub3 / SteaMidra 等主流工具均不再走 GitHub 分支模式）。`DownloadFromGithubAsync` 方法、`GetSourceApiKey` / `GetSourceBaseUrl` 辅助方法、`ManifestSourceType.GitHub` / `GitHubSearch` 枚举值、`CustomGithubRepos` / `CustomZipUrls` / `CustomManifestSources` 字段、`AppConfig.GithubToken` 字段均已删除；同时清理其他 5 个从未接入的清单源预设（`sac` / `walftech` / `steamautocracks_v2` / `buqiuren` / `auto_github`）及对应枚举值（`SAC` / `Walftech` / `ManifestOnly`）。`IsImplementedSource` 仅认 `mhub` / `sudama`
- 入库勾选"下载 Manifest"（默认开）：MHub → Sudama(仅密钥) 级联；不勾则跳过清单直接生成 Lua，由内核运行时兜底取清单，成功提示注明兜底
- `LuaBuilder`：Sudama 密钥/令牌 → `MergeAllDepotsAsync` 用全量 depot 列表补全（防止只写有 manifest 的 depot 漏密钥）→ 缺密钥收集并通知。DLC 段也对每个 DLC AppID 查 `keys[dlcId]`，Sudama 收录的独立 DLC depot key 会自动写入 `addappid(dlcId, 1, "<key>")`
- 缺解密密钥警告在两种模式下都保留（无 key 无法解密下载加密内容）

## 6. Sudama 缓存（v1.3.0 现状）

- 缓存文件：`%LOCALAPPDATA%\OSTGUI\sudama_cache.json`（约 22 万条 / 16.5MB）、`token_cache.json`
- 缓存**存在即用、不自动过期**（已去掉 24h TTL，08-28 起：缓存存在即用，不再自动刷新）；仅当无缓存文件时才会自动下载；新密钥/令牌靠设置页**手动刷新**或**手动导入本地文件**（浏览器直连快于应用内时使用，按文件名/内容自动识别类型）才能拿到
- 下载策略：密钥与令牌并行；流式接收；单次超时 max(120, 设置值)；重试间隔 1.5s；成功日志带条数/体积/耗时
- Sudama 无按需查询接口，只有全量端点；勿每次入库实时拉全量
- 隐藏调优参数 `DownloadTimeout`（config.json，默认 120，无 UI）：同时影响清单文件下载（max(60,·)）与 Sudama 缓存下载（max(120,·)）的超时；早期版本曾有设置控件，08-14 起移除仅留字段

## 7. 日志系统（含线程教训）

- 双轨：**运行时日志**（`LogService.AddLog`，内存集合绑定设置页，可复制/清空）与**应用日志文件**（`AddAppLog` → `%LOCALAPPDATA%\OSTGUI\logs\ostgui.log`，保留 N 行）
- ⚠️ **教训**：内存集合绑定着界面，非 UI 线程写入必须在 `AddLog` 内部经 DispatcherQueue 封送（现已内置）。此前后台服务直接写集合，异常沿调用方反向炸出，表现为各种莫名其妙的 NRE 且掩盖真实错误——排查这类"错误信息看不懂"的问题时优先怀疑跨线程 UI 操作
- `ConfigureAwait(false)` 不能防止上述问题：它只决定续延跑在哪个线程，真正的防线是集合操作点的封送

## 8. 免 Steam 部署（NoSteamLauncher）

流程：选游戏 EXE + AppID → （有壳时）Steamless 脱壳替换 EXE → GSE(Goldberg) 模拟器部署进游戏目录 → 可选 SteamAPICheckBypass（winmm 劫持隐藏模拟器痕迹）。原文件备份为 `.bak`，删除 `steam_settings` 并改回 `.bak` 即可还原。

- 对齐 SAC（SteamAutoCrack）的部署逻辑与 ini 配置格式；无壳游戏自动跳过脱壳不中断
- 老游戏（2016 前 SDK）会额外生成 `steam_settings/steam_interfaces.txt`，gbe_fork 需要
- **资源嵌入机制**：所有二进制（Steamless CLI/插件、GSE 模板、Bypass）以 EmbeddedResource 打进 NoSteamLauncher.dll，运行时解压到 `%TEMP%\OSTGUI_NoSteamLauncher\<版本>\` 并做关键文件完整性校验，缺失自动重解压
- ⚠️ **教训**：.gitignore 里全局 `*.dll/*.exe` 曾差点把这些资源挡在版本控制外——凡"运行必需的二进制"入库时务必显式反向规则确认
- 已知冲突：自带 winmm 依赖或反作弊的游戏对 Bypass 可能不适配（默认关闭）

## 9. Denuvo / 授权（查证）

- 需 **AppTicket + ETicket 双票**，不可本地伪造；ETicket 由 Steam 实时签发，**30 分钟有效**（报错 88500005）
- 提取授权存在"本地缓存的过时授权"假象且无法程序化验证账号正伪（ETicket 请求会被内核拦截）→ 采用导出时弹窗提醒策略
- `.ost` 为明文 JSON：AppTicket / ETicket / Source(Steam 用户名) / CreatedAt / ExpiresAt / UseCount / ExporterVersion
- 导入写注册表，本机任意 Steam 账号可用；部分游戏 DLC 也受 D 加密，只带主游戏票时 DLC 可能解锁失败
- 每账号每天最多 5 台新机器激活；已激活机器不消耗
- 与 .cw/.shiki（流畅入库私有格式）不兼容是刻意选择

## 10. WinUI 3 踩坑合集（两轮合并）

- 分段切换用 CommunityToolkit **Segmented**（Pivot/TabView 效果不对）；`SymbolIcon` 无 FontSize 属性（WMC0011），统一 `FontIcon Glyph`
- 桌面应用没有 `Windows.UI.Colors` → 用 `Microsoft.UI.Colors`
- 附加属性 C# 侧用 `ToolTipService.SetToolTip()`，对象初始化器赋值编译不过
- ContentDialog 必须设 `XamlRoot`（Page 用 `this.XamlRoot`，Window 用 `RootGrid.XamlRoot`）
- 纯图标透明按钮：`Background=Transparent` + `BorderThickness=0` + `Padding=8,4`
- **点击空白取消输入框激活（09-02 起有解，此前结论作废）**：两个 WinUI 已知 bug 叠加——① [#4364](https://github.com/microsoft/microsoft-ui-xaml/issues/4364) 点击空白把焦点投给 ScrollViewer 内第一个可聚焦控件（→ 误激活首个输入框）；② [#10051](https://github.com/microsoft/microsoft-ui-xaml/issues/10051) 输入框已聚焦时点空白不转移焦点（→ 无法取消激活）。**方案=两层兜底**：
  - **页面锚点**：每个含输入框页面的 ScrollViewer 内容首位放 1×1 透明可聚焦 Grid（`IsTabStop=True, TabIndex=0, Opacity=0`）→ 拦截 #4364 的 fallback（目标变成隐形锚点而非输入框）。已覆盖：设置页、联机页、D加密页(Transfer 面板)、S.A.C 页。
  - **全局回收**：`MainWindow.RootGrid` 挂 `PointerPressed`（`handledEventsToo:true`），点击处不在交互控件内时 `DispatcherQueue.TryEnqueue` 把焦点拉回全局锚点 `GlobalFocusAnchor`——**排队保证晚于框架指针处理**，从而覆盖 #10051（已聚焦也生效）与 #4364（即便 fallback 先触发也被覆盖）。
  - **交互判定** `IsInteractive` 白名单：输入控件 + ComboBox/Slider/ToggleSwitch/ListViewBase/ScrollBar/ButtonBase；`ponytail:` 注释标注——未来新增交互控件类型需补清单，否则点击它们会被当空白抢焦点。
  - **已知副作用**：点交互控件后键盘焦点回全局锚点（键盘 Tab 从头开始，鼠标无感）；锚点是隐形 Tab 停靠点；ContentDialog 等弹层不冒泡到 RootGrid 故不生效；仅指针（鼠标/触摸/笔）触发，键盘操作不受影响。
  - ⚠️ 历史记录"PointerPressed/Tapped 失焦方案全部无效"**作废**：当年失败根因是**没有可聚焦的焦点目标**（`Focus()` 到 TextBlock 无效）；引入"可聚焦锚点"后主动拉焦成立。
- 浅色主题下按钮图标/文字需适配 `TextFillColorPrimaryBrush` 等 ThemeResource
- ⚠️ 强调色的主题陷阱：`SystemAccentColor` 基础色**不随应用深浅主题翻转**；深色模式下需要"提亮版强调填充"的场景应使用 `AccentFillColorDefaultBrush` 等画刷（自动按主题选择正确变体），手写浅色主题的色值在深色模式下会显得突兀
- **ContentDialog 恒为深色是刻意行为**：弹窗位于弹出层，不继承应用 `RequestedTheme`，浅色模式下也渲染成深色。曾尝试显式同步主题，但 WinUI 浅色弹窗对比度差、观感不佳，遂回退保留深色——勿当 bug 修复
- **构建**：只能用 VS MSBuild（`dotnet build/publish` 缺 PRI 任务必挂）；首次 Release 自包含发布需先带 RID Restore（运行时包要从源下载，直连 nuget.org 失败时可切国内镜像）；旧实例不关会 MSB3021 锁 exe
- 版本号只在 csproj 维护三处（Version/AssemblyVersion/FileVersion），运行时从程序集读取

## 11. 服务索引（当前）

| 服务 | 职责 |
|---|---|
| `GameSearchService` / `SteamSearchProvider` | 搜索编排 / Steam 官方 API 搜索源 |
| `SteamGameInfoService` | 统一查询：depot + manifest gid + DLC 列表与名称（优先走社区非官方 API `api.steamcmd.net`——注意并非 Valve 官方，由 github.com/steamcmd/api 项目运营；失败回退官方 `store.steampowered.com/api/appdetails`，大陆网络下通常不可达）|
| `ManifestDownloadService` | 多源清单下载 + 生成 Lua（门面已移除）|
| `LuaBuilder` / `LuaConfigService` | Lua 生成（补全 depot/key/token/DLC/固定版本）；Lua 读写与版本模式切换 |
| `SudamaKeyCache` | 密钥/令牌缓存（存在即用不自动过期、并行下载、手动刷新与本地导入）|
| `LibraryScanner` | 扫描 Lua 目录、检测错误 |
| `NoSteamLauncherService` / `NoSteamLaunchOrchestrator` | 免 Steam 部署封装 / 编排（Steamless + GBE + Bypass）|
| `OnlineFixService` | 480 联机（`steam.exe -applaunch 480 -onlinefix`，PEB 读命令行检测）|
| `TicketService` / `OstFileService` / `SteamTicketExtractor` | Denuvo 授权管理 / .ost 导入导出 / 在线提取 |
| `ConfigService` / `LogService` / `ToastService` / `GameNameCacheService` | 配置 / 日志 / 通知 / 名称缓存 |

## 12. 已知限制（仍然有效的）

- MHub 只支持最新清单
- 补齐版本配置写入的是当前 GID
- 名称缓存 30 天 TTL，游戏改名后会显示旧名（08-28 起机制：仅主游戏入缓存；库页显示纯缓存读 + 后台静默补名；联机/DLC 等显示名处实时查询不参与缓存）
- DLC token 无按需查询渠道：生成 Lua 时会逐个查 Sudama 全量缓存并写入命中的 `addtoken`，但 dump 未收录的受限 DLC 没有补充途径
- 480 联机同一时间只能运行一个
- Sudama 大文件下载速度取决于服务器线路，慢时走浏览器下载 + 手动导入

## 13. 创意工坊（Workshop）下载（Phase 1 已落地）

- 创意工坊内容走 depot 加密管线：Steam 客户端把订阅的 item 当作 **depot = consumer_app_id（游戏 AppID）** 下载（depotcache 命名 `<AppID>_<manifestID>.manifest`），解密密钥按 ConfigStore `...\<AppID>\DecryptionKey` 读取
- OST 内核已覆盖两环：manifest code（`GetManifestRequestCode` 劫持 + 第三方源仅按 gid 查询，workshop gid 被收录即可）与密钥注入（`ConfigStoreGetBinary` hook 从 Lua 的 DepotKeySet 取 key）
- 缺口曾是 Lua 主游戏行 `addappid(appid)` 不带密钥 → 内核无 key 可喂 → 订阅下载报"内容仍处于加密"
- Phase 1（v1.3.x）：`LuaBuilder` 主游戏行自动带上 Sudama depotkeys 中 AppID 自身的密钥（社区称"创意工坊密钥"，须恰好 64 位 hex）→ **新入库**即具备工坊下载解密能力；已入库游戏需重新入库或手动把主行改为 `addappid(appid, 1, "<key>")`
- 边界：**限制匿名的工坊**（部分游戏/新 manifest 的 code 请求被服务器拒绝）无法绕过，需真实拥有该游戏的账号；**老式独立 workshop depot**（SteamDB 标注 Workshop 的 depot，如 Dying Light）需该 depot 单独密钥，Phase 1 不覆盖

## 14. 480 联机邀请失效：调研、直启实验（已移除）与既定方向

- **症状**：`-onlinefix` 下 Steam 正确显示在玩 480，但被好友邀请时游戏无反应、不进房（PEAK 实测；双方均为 OSTGUI 同版本）。
- **根因分析**：内核把 `GetAppID` 响应从 480 还原成真实 AppId（为 DLC/成就/自身校验），而网络侧好友状态、邀请、大厅全是 480 → 身份分裂。游戏做 `invite.gameID == GetAppID()` 类自检时邀请被静默丢弃。内核 `SendCallbackToPipe` 的回调修改分发器目前是空的——邀请链路的数据从未做过 480↔X 翻译。
- **环境变量直启实验（已实现后移除）**：设 `SteamAppId/SteamGameId=480` 直接启动游戏 exe 形成全一致世界，理论上邀请校验天然通过（经典盗版联机/Cai Install BAT 模式即此法）。实测两处硬伤：① **叠加层必丢**——gameoverlayui 由 Steam 启动链注入，直启进程 Steam 不感知；② **带自检的游戏直接放弃联机**——`RestartAppIfNecessary` 或 Facepunch 式 `GetAppID()==硬编码` 校验在 480 身份下失败。"自身身份自检"与"邀请一致性校验"两条要求互相矛盾，纯直启路线被夹死，仅对无自检游戏有效。
- **既定方向（已实施，待好友实测）**：内核微补丁——保留 `GetAppID` 还原（自身自检通过），在 `SendCallbackToPipe` 加 `LobbyInvite_t` 分支把 `m_ulGameID` 从 480 改回真实 AppId（该分发器即为此类用途预留，原本为空）。可选补 `IClientFriends::GetFriendGamePlayed` 同步改写，若实测发现卡点在游戏内好友过滤再追加。
  - 改动：内核开发驻地 `D:/Projects/OSTGUI/ZSteamTool`（自 RefProjects 迁出，作二次开发独立目录），分支 `fix/onlinefix-lobby-invite`（提交 94a80b8）：`Steam/Callback.h` 加 `k_iSteamMatchmakingCallbacks=300` + `LobbyInvite_t`（公开 SDK 布局）；`Hook/Hooks_Misc.h/.cpp` 暴露 `IsOnlineFixActive()`；`Hook/Hooks_CallBack.cpp` 分发器加 LobbyInvite 分支（命中时 LOG_ONLINEFIX_INFO）。
  - 构建：VS18 自带 CMake + MSVC，Debug 配置产出 `build/Debug/{OpenSteamTool,dwmapi,xinput1_4}.dll`。依赖经 FetchContent 缓存 `.deps/`（lua/spdlog/protobuf/tomlplusplus/detours 手动预填，因本机 TLS 被 Steam++ 加速器中间人拦截，schannel 全线不可用；git 需 `http.sslBackend=openssl` + 合并 SteamTools 根证书的 CA bundle）。
  - 部署：已替换 `d:/steam/` 下三个 DLL（原内核备份在 `D:/Projects/OSTGUI/ost-backups/20260825-kernel-480fix/`，回滚=拷回）。Debug 内核默认日志可用（toml 未设 [log] 时走 Debug 级）。
  - 验证清单：① 重启 Steam 后正常加载、入库/游玩无回归；② `-onlinefix` 启动游戏照常；③ 好友发邀请时 `onlinefix.log` 出现 `LobbyInvite: gameID 480 -> <真实>` 且游戏弹出邀请可入房。
- 教训：GreenLuma/SteamTools 类 DLL 注入只解决入库（客户端层伪造所有权），Valve 服务端按账号验证匹配请求——"假入库不能联机"是系统性死穴；能联机的通用解只有"在拥有许可的 AppID 下做匹配"，即 480 一致世界。

### 实证轮（v1/v2/v3，PEAK + 日志探针）

- **v1（仅 LobbyInvite_t 改写）实测结果**：补丁生效（`SpawnProcess: 3527290 -> 480`、`OnlineFix: 480 -> name 'PEAK'` 均出现），但**全程零 `LobbyInvite:` 日志、零 MMS/大厅流量、零 JoinLobby 尝试**——邀请根本没有走大厅邀请通道，只收到 2 条旧式 `InviteToGame(7005)`。LobbyInvite 假设不成立。
- **v2（增 Persona 好友改写 + 全回调探针）实测**：回调探针显示 21 次 cb=304 等（内部回调号无法直接映射）；但 `Persona friend` 改写一次未触发——两处缺陷：① 改写块放在 `if (!selfEntry) return false` 之后，而好友增量推送常不含 self 条目被提前拦掉；② 好友的 480 状态若在启动游戏前就已推送进客户端缓存，后续不再有推送 → 改写永远无机会执行。
- **v3（真凶落点）**：结合用户观察"邀请时弹出的是普通好友界面而非邀请界面"，定位到内核提交 **#40（Restore controllers and overlay identity）**：`BuildSpawnEnvBlock` 把 `SteamOverlayGameId` 还原成真实 AppId → 叠加层身份与 480 空间大厅不匹配 → `ActivateGameOverlayInviteDialog` 降级为普通好友列表 → 邀请退化为 7005，游戏无处理。**v3 撤销 #40 的叠加层还原**（保留 OptedInMask 手柄还原），叠加层回到 480，邀请对话框正确绑定 480 大厅；代价仅截图标签/社区链接显示 Spacewar。同时把 Persona 改写移到 selfEntry 早退之前。
  - 提交：内核分支 `fix/onlinefix-lobby-invite`（v1: 94a80b8，v3: 36708b9）。部署备份：`ost-backups/20260825-kernel-480fix`（原版）、`20260825-kernel-v2-friendpatch`、`20260826-kernel-v2-persona`。
  - 验证判据（v3，待好友实测）：邀请时应弹出**真正的邀请对话框**（每好友带"邀请"按钮）；命中后 `onlinefix.log` 出现 `Persona friend ... gameid 480 -> 3527290`（好友上线后）或叠加层行为正常；邀约发出/接受后有 MMS/大厅流量与 `LobbyInvite` 改写日志。
