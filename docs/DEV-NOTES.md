# OSTGUI 开发笔记（DEV-NOTES）

> 本文合并自原 plan1.md / plan2.md 两份沉淀文档，并更新至 v1.3.0 现状。
> 收录原则：领域事实与踩坑经验长期保留；工程状态快照（构建命令产物、临时数字、"待提交"类状态）不再收录，避免过时。

## 1. 架构总览

三层模型：

1. **Steam 客户端**：下载/解密/运行；向服务器要三样东西——所有权、depot 解密密钥、manifest（文件清单）
2. **OST 内核**（OpenSteamTool DLL，注入 Steam）：劫持这些请求，用 `Steam\config\lua\*.lua` 的配置作答
3. **OSTGUI**：生成 Lua + 预下载 manifest 进 depotcache + 提供密钥/令牌；另含免 Steam 部署（NoSteamLauncher 类库）

仓库结构：`OSTGUI/`（WinUI 3 主程序）+ `NoSteamLauncher/`（免 Steam 部署类库，运行时二进制以 EmbeddedResource 内嵌）+ `docs/`（声明与更新说明）。

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

- 搜索按 AppID 走官方 `appdetails?appids={id}&l=schinese&cc=us`；按名称不带 cc 会按 IP 判 cn 区过滤成人内容 → **搜不到黄油**，界面提示用 AppID；能搜到 ≠ 能入库（还需密钥存在）
- 清单源与密钥源分离：MHub = 清单源（仅最新版），Sudama = 仅密钥源；SteamAutoCracks/ManifestHub 的分支式 GitHub 源已失效（404），GitHub(Auiowu) 分支源仍实现着
- 入库勾选"下载 Manifest"（默认开）：MHub → GitHub → Sudama(仅密钥) 级联；不勾则跳过清单直接生成 Lua，由内核运行时兜底取清单，成功提示注明兜底
- `LuaBuilder`：Sudama 密钥/令牌 → `MergeAllDepotsAsync` 用全量 depot 列表补全（防止只写有 manifest 的 depot 漏密钥）→ 缺密钥收集并通知
- 缺解密密钥警告在两种模式下都保留（无 key 无法解密下载加密内容）

## 6. Sudama 缓存（v1.3.0 现状）

- 缓存文件：`%LOCALAPPDATA%\OSTGUI\sudama_cache.json`（约 22 万条 / 16.5MB）、`token_cache.json`
- 24h TTL；失败回退任意旧缓存；设置页可强制刷新，也支持**手动导入本地文件**（浏览器直连快于应用内时使用，按文件名/内容自动识别类型）
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
- 输入框失焦方案（PointerPressed/Tapped/handledEventsToo/页面级）全部无效，已回退——别再浪费时间
- 浅色主题下按钮图标/文字需适配 `TextFillColorPrimaryBrush` 等 ThemeResource
- **构建**：只能用 VS MSBuild（`dotnet build/publish` 缺 PRI 任务必挂）；首次 Release 自包含发布需先带 RID Restore（运行时包要从源下载，直连 nuget.org 失败时可切国内镜像）；旧实例不关会 MSB3021 锁 exe
- 版本号只在 csproj 维护三处（Version/AssemblyVersion/FileVersion），运行时从程序集读取

## 11. 服务索引（当前）

| 服务 | 职责 |
|---|---|
| `GameSearchService` / `SteamSearchProvider` | 搜索编排 / Steam 官方 API 搜索源 |
| `SteamGameInfoService` | 统一查询：depot + manifest gid + DLC 列表与名称（优先走社区非官方 API `api.steamcmd.net`——注意并非 Valve 官方，由 github.com/steamcmd/api 项目运营；失败回退官方 `store.steampowered.com/api/appdetails`，大陆网络下通常不可达）|
| `ManifestDownloadService` | 多源清单下载 + 生成 Lua（门面已移除）|
| `LuaBuilder` / `LuaConfigService` | Lua 生成（补全 depot/key/token/DLC/固定版本）；Lua 读写与版本模式切换 |
| `SudamaKeyCache` | 密钥/令牌缓存（并行下载、24h TTL、手动刷新与本地导入）|
| `LibraryScanner` | 扫描 Lua 目录、检测错误 |
| `NoSteamLauncherService` / `NoSteamLaunchOrchestrator` | 免 Steam 部署封装 / 编排（Steamless + GBE + Bypass）|
| `OnlineFixService` | 480 联机（`steam.exe -applaunch 480 -onlinefix`，PEB 读命令行检测）|
| `TicketService` / `OstFileService` / `SteamTicketExtractor` | Denuvo 授权管理 / .ost 导入导出 / 在线提取 |
| `ConfigService` / `LogService` / `ToastService` / `GameNameCacheService` | 配置 / 日志 / 通知 / 名称缓存 |

## 12. 已知限制（仍然有效的）

- MHub 只支持最新清单
- 补齐版本配置写入的是当前 GID
- 名称缓存 30 天 TTL，游戏改名后会显示旧名
- DLC token 无按需查询渠道：生成 Lua 时会逐个查 Sudama 全量缓存并写入命中的 `addtoken`，但 dump 未收录的受限 DLC 没有补充途径
- 480 联机同一时间只能运行一个
- Sudama 大文件下载速度取决于服务器线路，慢时走浏览器下载 + 手动导入
