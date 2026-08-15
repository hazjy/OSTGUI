# OSTGUI 开发沉淀（迁移参考）

> 本文档汇总项目关键逻辑、依赖与踩坑查证的事实，供迁移/重构对话框时对照，避免重新走弯路。

## 1. 项目速览

- 仓库：`D:\Projects\OSTGUI`，C# + WinUI 3（Windows App SDK），目标框架 `net10.0-windows10.0.19041.0`，RID `win-x64`
- 构建：`run_debug.bat`（VS MSBuild，`/v:m` 日志最少）；编译前必须先杀运行中的 `OSTGUI` 进程
- 打包：`MSBuild OSTGUI.csproj /t:Publish /p:Configuration=Debug /p:RuntimeIdentifier=win-x64 /p:SelfContained=true`
  - 注意：`dotnet publish` 会因找不到 VS 的 `Microsoft.Build.Packaging.Pri.Tasks.dll` 报错，**必须用 VS MSBuild**（先 VsDevCmd）
  - 体积：框架依赖约 30 MB；.NET 自包含约 64 MB（解压即用，群友不用装 .NET 10）
- git 存档习惯：功能完成+编译验证后提交；`bin/ obj/ *.zip` 已 gitignore
- 内部默认值：`DownloadTimeout=120`（无 UI，仅服务内部使用）；Sudama 下载超时保底 300 秒

## 2. 核心架构：三层

1. **Steam 客户端**：下载/解密/运行，向服务器要三样东西——所有权、depot 解密密钥、manifest（文件清单）
2. **OST 内核**（OpenSteamTool DLL，注入 Steam）：劫持这些请求，用 `Steam\config\lua\*.lua` 的配置回答
3. **OSTGUI**：生成 Lua + 预下载 manifest 进 depotcache + 提供密钥/令牌

## 3. Lua 结构与语义（内核事实）

```lua
addappid(2001760)                                  -- 解锁主游戏（假装拥有）
addappid(2001761, 1, "64位hex")                   -- 解锁 depot + 注入解密密钥
addappid(2827030)                                 -- DLC：只解锁不带密钥（多数复用本体 key）
addtoken(2001760, "令牌")                          -- 受限 app 获取 appinfo 需要
setManifestid(2001761, "gid", 大小)               -- 固定版本（锁 depot 的 manifest）
```

- `addappid` 第二参数（`1`）**内核实际忽略**（源码只读第 1、3 参数）；密钥必须恰好 64 字符，否则不生效
- `--` 开头是注释，内核忽略；函数名不区分大小写；文件放 `config\lua\{AppId}.lua`，内核热重载
- `setManifestid` 有则固定版本，无则自动更新；大小可省略

## 4. depot / manifest / gid / key 关系（查证过）

- **depot**：游戏内容分区（本体/DLC/语言包各一），内容在 CDN，depot ID 固定不随版本变
- **manifest**：某 depot 的文件清单（文件名、chunk、哈希）；“下哪些 depot”由 appinfo 决定，manifest 管“下哪些文件块”
- **gid**：manifest ID，代表该 depot 某个内容快照；**每次更新换新 gid**；游戏完整版本 = 全部 depot 的 gid 组合
- **key**：AES-256 解密密钥，per-depot 且对所有用户相同（可共享）→ 这是第三方密钥库可行的根本原因
- **所有 depot 内容都是 AES-256 加密**，不存在“未加密 depot”；裸 `addappid(depotId)` 是碰运气的降级写法

## 5. 搜索 → 入库全链路

### 搜索
- 按 AppID：Steam 官方 `appdetails?appids={id}&l=schinese&cc=us`
- 按名称：storesearch 等仍走 `cc=cn` → **搜不到黄油**，界面提示“限制级作品请用 AppID 搜索”
- 黄油根因：appdetails 不带 `cc` 按 IP 判地区，cn 区过滤成人内容；`cc=us` 解决
- 注：能搜到 ≠ 能入库，入库还需 Sudama 有该游戏密钥，否则下载报“内容加密”

### 入库（多源级联）
`MHub（配 key）→ GitHub（SteamAutoCracks/ManifestHub 分支）→ Sudama 兜底`
- 各源流程：拿 depot+gid（SteamCMD API 优先，官方 appdetails 回退）→ 下载 manifest → 拷 depotcache（`config\depotcache` + `depotcache` 双份，文件名 `{depotId}_{gid}.manifest`）→ `LuaBuilder` 生成 Lua → 原子写 `config\lua\{AppId}.lua`
- `LuaBuilder`：Sudama 密钥/令牌 → `MergeAllDepotsAsync` 用全量 depot 列表补全（修复“只写下载到 manifest 的 depot 漏密钥”）→ 缺密钥收集 → 入库异常系统通知（标题“入库异常”，列出缺失 depot）
- 固定版本勾选时对有 gid 的 depot 写 `setManifestid`
- “未下载到清单也能入库”：OST 内核支持上游 API 自动取 manifest code（`manifest.opensteamtool.com/{gid}` / wudrm / steamrun），但不确定、不能锁版本

### manifest 获取的官方机制（查证过）
- 两步：向 CM 发 `GetManifestRequestCode`（需登录且**拥有该 depot**）→ 拿 code 向 CDN 拉 `depot/{id}/manifest/{gid}/5/{code}`
- 无权限账户拿不到 code → 第三方清单库本质是有人用正版账户批量抓取共享
- OST 内核拦截 code 请求并伪造响应喂回客户端，manifest 本体仍从 Steam CDN 直连

### addtoken（查证过）
- 场景：**受限 app** 获取 appinfo（PICS `CMsgClientPICSProductInfoRequest`，eMsg 8903）需要 access token
- Steam 不主动告知，现象是权限不足/返回信息缺 depots 字段
- 内核拦截请求，把 `access_token` 塞进请求；Sudama `appaccesstokens.json` 收录 `appId→token`，LuaBuilder 只按**主游戏 AppID** 查（DLC 不查）

## 6. Sudama 缓存（当前机制）

- 缓存文件：`%LOCALAPPDATA%\OSTGUI\sudama_cache.json`（21.9 万密钥，约 16.8 MB）、`token_cache.json`
- 机制：24h TTL；过期才重新下载；失败回退旧缓存（任意旧）；设置页“刷新缓存”按钮强制刷新
- 加固：下载用独立 HttpClient（超时 `max(300, DownloadTimeout)`）、失败自动重试一次、`ConfigureAwait(false)` 避免 UI 线程解析大 JSON
- 服务器实测：密钥 3 秒/16.8MB、token 0.8 秒，均 200；曾出现“小文件成功大文件失败”的网络抖动 → 重试解决
- 注意：Sudama 无按需查询接口，只有全量端点；勿每次入库实时拉全量

## 7. 日志与通知

- 两栏各司其职：**运行时日志**（内存 `LogService.AddLog`，UI 展示/复制/清空）；**应用日志文件**（`%LOCALAPPDATA%\OSTGUI\logs\ostgui.log`，`AddAppLog`，保留 N 行）
- 系统通知：`ToastService`（Microsoft.Toolkit.Uwp.Notifications），队列 + 500ms 间隔；受设置“系统通知开关”控制
- 所有设置实时保存（PropertyChanged → SaveAllToConfig），无保存按钮

## 8. 关键服务/文件索引

| 服务 | 职责 |
|---|---|
| `SteamSearchProvider` | 搜索（AppID 走官方 API cc=us） |
| `SteamGameInfoService` | depot+gid（SteamCMD 优先，appdetails 回退） |
| `ManifestDownloadService` | 三源级联下载 manifest + 生成 Lua |
| `LuaBuilder` | 生成 Lua（补全 depot、密钥、token、DLC、固定版本） |
| `SudamaKeyCache` | 密钥/令牌缓存（24h TTL + 手动刷新） |
| `ManifestRepairService` / `LuaRepairService` | 修复 Lua/补清单/补齐版本配置 |
| `LibraryScanner` | 扫描 Lua 目录、检测错误（缺清单按 **Lua 里的 gid** 检测） |
| `OnlineFixService` / `OnlineViewModel` | 480 联机（`steam.exe -applaunch 480 -onlinefix`，PEB 读命令行检测） |
| `OstFileService` / `SteamTicketExtractor` / `TicketService` | .ost 导入导出、在线提取 |
| `ToastService` | 系统通知 |
| `LogService` | 运行时日志 + 文件日志 |

## 9. NuGet 依赖

| 包 | 版本 | 用途 |
|---|---|---|
| Microsoft.WindowsAppSDK | 1.6.250108002 | WinUI 3（`WindowsAppSDKSelfContained=true`） |
| CommunityToolkit.WinUI.Controls.Segmented | 8.2.251219 | 分段切换条（Denuvo 页/联机页用，Pivot/TabView 不可用） |
| CommunityToolkit.WinUI.Controls.SettingsControls | 8.2.251219 | 设置控件 |
| CommunityToolkit.Mvvm | 8.2.2 | ObservableProperty/RelayCommand |
| Microsoft.Toolkit.Uwp.Notifications | 7.1.3 | 系统通知 Toast |
| Microsoft.Extensions.DependencyInjection | 8.0.0 | DI 容器（App.xaml.cs 注册） |
| System.Drawing.Common | 10.0.10 | 图像处理 |
| WindowStateSaver.WinUi3 | 0.0.1 | 窗口状态记忆 |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.1742 | 构建 |

## 10. Denuvo / 授权（查证过）

- D 加密需要 **AppTicket + ETicket 双票**，均不可本地伪造；ETicket 由 Steam 服务器实时签发，**30 分钟有效**（报错 88500005）
- 提取授权：**未删除本地缓存途径**（被授权机仍可能提取到缓存的过时授权）——采取“导出时弹窗提醒”策略：提示用户务必用正版账号，非正版可能提取到缓存的过时授权、无法生效；提醒原因是 ETicket 请求会被 OST 内核拦截、无法用“请求是否成功”来验证账号是否正版
- .ost 格式：**明文 JSON 无加密**，含 AppTicket / ETicket / Source(Steam 用户名) / CreatedAt / ExpiresAt / UseCount / ExporterVersion
- 导入写入注册表，本机任意 Steam 账号可用；部分游戏 DLC 也受 D 加密，.ost 只带主游戏时 DLC 可能解锁失败
- 与 .cw/.shiki（流畅入库作者 pvzcxw 私有闭源格式）不兼容是刻意选择
- 每账号每天最多 5 台新机器激活额度；已激活机器不消耗

## 11. UI 经验

- Denuvo/联机页用 CommunityToolkit **Segmented** 做切换（Pivot/TabView 效果不对）
- 标题栏/侧边栏：WinUI 材质（Mica/Acrylic），标题栏透明色需与侧边栏一致处理
- 窗口最小尺寸锁定（防页面缩太小崩溃）；窗口位置记忆由 WindowStateSaver 提供
- 输入框失焦方案（PointerPressed/Tapped/handledEventsToo/页面级）**全部无效，已回退**——迁移时别在这上面浪费时间
- 浅色主题修复过：按钮图标/文字需适配 `TextFillColorPrimaryBrush` 等 ThemeResource

## 12. 已知限制 / 未竟事项

- 修复清单逻辑的 gid 与扫描检测不一致：扫描按 Lua 里的 gid，修复按 API 当前 gid → 手写旧 gid 时修不好
- DLC 的 token 未单独查询（只查主游戏 AppID）
- Sudama 无按需接口，只能全量
- 480 联机同一时间只能运行一个
- 瓦特工具箱（steampp.net）加速提示已加入主页；Steam 官方 API 不稳
