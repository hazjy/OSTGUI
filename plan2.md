# OSTGUI 开发沉淀（第二阶段）

> 承接 [plan1.md](plan1.md)。本轮覆盖：清单/版本机制联网查证、固定版本体系重做、入库选项演进、名称缓存机制确认、WinUI 3 第二轮踩坑。开发状态：本轮大量改动尚未提交，建议先 `git commit` 存档。

## 1. 本轮变更总览（相对 plan1）

- 修复类服务已全部删除：`ManifestRepairService` / `LuaRepairService` 已不存在；页面状态里的 Lua 检测、修复系统通知、刷新页面自动修复等均已移除
- 清单源与密钥源彻底分离：MHub = 清单源（API 网站），Sudama = 仅密钥源；GitHub `SteamAutoCracks/ManifestHub` 分支源已失效（404，仓库仅剩 main + depotkeys/appaccesstokens，无按 AppID 分支）
- CaiGames 备用源彻底移除（AppID 搜索 + 名称搜索两处调用、常量、响应模型全删）
- 新增"下载 Manifest"入库勾选项，可跳过清单下载由内核运行时兜底
- 固定版本体系重做（见 §3）：锁定版本改为"全部 depot 必须有 setManifestid 覆盖"，不再检查 depotcache 清单文件
- 新增侧边栏"信息"弹窗（版本号 + 制作者 ZJY）、搜索页跳转弹窗（AppID + 复制图标）

## 2. 关键查证：清单与版本机制（联网 + 核心源码）

### 2.1 清单的意义与"为什么总是最新版"

- manifest = 某 depot 某版本的文件清单（文件名/chunk/哈希）；客户端必须拿到对应 gid 的 manifest 才能向 CDN 请求内容
- 版本决策链：服务器 appinfo（当前 branch 的 depot→gid）→ 与本地 `.acf` 的 `InstalledDepots` gid 对比 → 不一致触发更新
- `depotcache` 只是清单本地缓存（客户端"先查缓存，没有再申请 code 下载"），**没有版本决策权**
- 入库总是最新版是双重保证：工具每次现取 Steam 官方 API 的 `manifests.public.gid`（当前最新）；MHub 本身只支持最新清单（原 ManifestHub README 原话 *"Supports latest manifests and workshop manifests"*）
- MHub 结构（查证 `G-TTYg/SSMGAlt-ManifestHub2-copy` fork）：根目录仅 README / depotkeys.json / appaccesstokens.json，其余全是按 AppID 命名的分支，分支内放 manifest

### 2.2 manifest 获取门槛

- `GetManifestRequestCode`：2021 年加入；请求参数（appid/depotid/manifestid/branch/password）必须与当前 appinfo 匹配，且账号拥有该游戏，否则 AccessDenied；code 约 5 分钟轮换
- 旧 gid 更新后不在当前 appinfo → 正版账号也拿不到旧 code → 第三方清单库只能有最新 + 早期留档
- 第三方清单库本质：有人用拥有权限的正版账号批量抓取共享（ManifestHub README、pjy612 讨论 #115、52pojie wxy1343 帖佐证）

### 2.3 直接改清单 ID 不能锁版本

- 改 depotcache 文件：只是缓存，不触发更新、不改版本
- 改 `.acf` 的 gid 为旧值：Steam 判定落后 → 更新到服务器当前（最新）版；社区做法是把 gid/buildid 改成**最新值**骗过更新器防更新（CHN-STUDENT repo），不是锁旧版
- 锁版本唯一正道：OST 内核 hook（见 §3.1）

## 3. 固定版本 / 锁定版本体系（重做后）

### 3.1 OST 内核机制（核心源码查证）

- `setManifestid` → `ManifestOverrides` → [Hooks_Manifest.cpp] 的 `BuildDepotDependency` 把 Steam 构建的 depot 条目 gid 直接 patch（size=0 时保留原 size）
- 拦截 `GetManifestRequestCode` 出站/入站（Hooks_NetPacket_Manifest）：出站时异步向上游换 code，入站时伪造 `eresult=OK` + 填真实 code 喂回客户端
- 上游 provider（ManifestClient.cpp）：opensteamtool（`manifest.opensteamtool.com/{gid}`）→ wudrm → steamrun，也可用 Lua `fetch_manifest_code_ex(appId, depotId, gid)` 自定义
- 客户端拿 code 从 CDN 拉**任意 gid** 的清单 → 已实测成功回退旧版本（用户验证）
- 内核不写 `.acf` / depotcache 文件，全部内存 hook；`pinApp` 新版 README 已标注"不再支持"

### 3.2 OSTGUI 侧

- `LuaBuilder.fixedVersion` 只决定是否预写**注释形式** `--setManifestid(...)`（对应"写入固定版本配置"勾选），不决定版本模式；写入后仍是自动更新
- 锁定版本切换（`LuaConfigService.ToggleVersionModeAsync`）：
  - auto→fixed：必须存在注释 setManifestid；**depot 全覆盖检查**——第一个 addappid 视为主 AppID、`-- 所有 DLC` 注释段之后的视为 DLC、其余视为 depot，要求每个 depot 都有（已激活或注释形式的）setManifestid，缺则拒绝并列出缺失 depot
  - fixed→auto：直接注释掉 setManifestid
  - **不再检查 depotcache 清单文件存在**（内核可运行时现抓）
- 补齐版本配置（更多菜单 → `LuaConfigService.RepairVersionConfigAsync`）：从 Steam API 现取全部 depot + 当前 GID，写入/整体替换注释 setManifestid 块；不下载清单、不碰 depotcache
- 注意：补齐写入的是**当前最新 GID**；要锁旧版需手动把 gid 改成 SteamDB 历史值，且上游能提供该 gid 的 code

## 4. 入库流程现状

- "下载 Manifest"勾选项（`StDownloadManifestDefault`，默认 true，持久化）：
  - 勾选：MHub（配 key 时）→ GitHub → Sudama（仅密钥、不下载清单）级联
  - 不勾选：跳过清单源，直接 Sudama 密钥源生成 Lua，清单由内核运行时兜底；"未下载到清单文件"不再误报为入库异常，成功提示注明兜底
- 缺解密密钥警告两种模式都保留（无 key 无法解密下载加密内容）
- 搜索页右上角三个勾选项：添加所有DLC / 写入固定版本配置 / 下载 Manifest，对应配置 `DefaultAddAllDlc` / `StFixedVersionDefault` / `StDownloadManifestDefault`，勾选即存

## 5. 入库管理页缓存机制

- 列表不缓存：每次打开/刷新重扫 Lua 目录；视图/排序模式持久化到配置（设置记忆，非缓存）
- 名称缓存 `GameNameCacheService`：`%LOCALAPPDATA%\OSTGUI\name_cache.json`，`AppID → (名称, 时间)`，TTL 30 天，加载时丢弃过期条目
- 批量取名 `GetGameNamesBatchAsync`：缓存命中不联网，只对缺失联网，并发限制 6（SemaphoreSlim 防限流）；写入点在 SteamSearchProvider 5 处 + OnlineViewModel
- DLC 信息不预加载、不缓存：点"入库信息"时按需 `LoadDlcInfoAsync`
- MainViewModel 5 秒库刷新定时器只刷统计（ScanLibraryAsync 计数），不重建列表、不碰名称缓存

## 6. 本轮新增 UI

- 侧边栏"设置"下方"信息"（`NavInfo`，FontIcon E946）：ContentDialog 显示版本号（`Assembly.GetExecutingAssembly().GetName().Version.ToString(3)` 动态读取）+ 制作者 ZJY + GitHub（Primary 蓝色，打开项目主页）+ 退出
- 搜索页结果行"跳转"按钮：WinUI 分享图标 E72D、透明底、ToolTip="跳转"；弹窗内容含 `AppID：xxx` 行 + 透明复制图标（E8C8，横向 StackPanel 固定 8px 间距、随 AppID 长度自动右移，点击复制）
- 首页分享按钮方案已废弃（位置/透明底反复调整后弃用），代码已删除干净

## 7. WinUI 3 开发心得（第二轮踩坑）

- `SymbolIcon` 无 `FontSize` 属性 → XamlCompiler WMC0011；统一用 `FontIcon Glyph`（分享 E72D / 复制 E8C8 / 信息 E946 / 更多 E712 / 删除 E74D / 刷新 E72C）
- WinUI 3 桌面应用 `Windows.UI.Colors` 不存在 → 用 `Microsoft.UI.Colors.Transparent`
- 附加属性在 C# 里用 `ToolTipService.SetToolTip(obj, "...")`，对象初始化器直接赋值编译不过
- ContentDialog 必须设 `XamlRoot`：Page 用 `this.XamlRoot`，Window 用 `RootGrid.XamlRoot`
- 纯图标透明按钮：`Background=Transparent` + `BorderThickness=0` + `Padding=8,4`
- 构建：`run_debug.bat`（VS MSBuild + VsDevCmd）；**旧实例不关会 MSB3021（exe 被锁）**；`dotnet build` 缺 VS PRI 任务不可用
- 版本显示：csproj 的 `Version` / `AssemblyVersion` / `FileVersion` 同步维护，运行时从程序集读取，改版本只动 csproj

## 8. 关键代码索引（本轮新增/改动）

| 位置 | 说明 |
|---|---|
| `LuaConfigService.ToggleVersionModeAsync` | 锁定版本切换 + depot 全覆盖检查 |
| `LuaConfigService.RepairVersionConfigAsync` | 补齐版本配置（写注释 setManifestid，不管清单） |
| `SearchViewModel.AddGameAsync` | DownloadManifest 分支入库流程 |
| `GameNameCacheService` | 名称缓存（30 天 JSON） |
| `LibraryViewModel.RepairVersionConfigAsync` | 更多菜单入口：取 depot+GID → 写配置 |
| `MainWindow.NavInfo_Tapped` | 侧边栏信息弹窗 |
| `SearchPage.ShowShareDialog` | 跳转弹窗 + AppID 复制图标 |
| 内核：`Hooks_Manifest.cpp` / `Hooks_NetPacket.cpp`（Manifest 段）/ `ManifestClient.cpp` / `LuaConfig.cpp` | gid patch、code 拦截、上游 provider、Lua 解析 |

## 9. 已知限制 / 未竟事项

- MHub 只支持最新清单；历史清单依赖内核 code 上游，上游服务的可用性与覆盖范围是外部变量
- 补齐版本配置写入的是当前 GID；锁旧版需手动改 gid，且旧 gid 必须能向上游要到 code
- 名称缓存 30 天：游戏改名后可能显示旧名
- GitHub ManifestHub 分支源已失效，代码保留但实际 404
- 联机/D加密等模块见 plan1 §10，未在本轮变动
