# REF-联机（GUI 侧）

> 只收 480 联机邀请失效 / 会话身份 / 宿主 480 路线 / 内核原生路线 / AppID Changer；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26）。

## 480 联机邀请失效：调研、直启实验与既定方向

- **症状**：`-onlinefix` 下 Steam 正确显示在玩 480，但被好友邀请时游戏无反应、不进房（PEAK 实测；双方均为 OSTGUI 同版本）。
- **根因分析**：内核把 `GetAppID` 响应从 480 还原成真实 AppId（为 DLC/成就/自身校验），
  而网络侧好友状态、邀请、大厅全是 480 → 身份分裂。游戏做 `invite.gameID == GetAppID()` 类自检时邀请被静默丢弃。
- **环境变量直启实验（旧结论已修正）**：设 `SteamAppId/SteamGameId=480` 启动游戏 exe 可形成全一致世界。
  旧记录的两条"硬伤"里，**①"叠加层必丢"已于 2026-09-17 实测推翻**——客户端只要看到进程以该身份初始化成功，
  就照常登记并注入叠加层（证据见下节 v4）；②"带自检的游戏可能放弃联机"仍成立
  （`RestartAppIfNecessary` 或 Facepunch 式 `GetAppID()==硬编码` 校验在 480 身份下可能失败），
  改由"宿主先注册成同一 AppID"解决，即 v4 路线。
- **既定方向（已实施）**：内核微补丁——保留 `GetAppID` 还原（自身自检通过），
  在 `SendCallbackToPipe` 加 `LobbyInvite_t` 分支把 `m_ulGameID` 从 480 改回真实 AppId。
- 教训：GreenLuma/SteamTools 类 DLL 注入只解决入库（客户端层伪造所有权），Valve 服务端按账号验证匹配请求
  ——"假入库不能联机"是系统性死穴；能联机的通用解只有"在拥有许可的 AppID 下做匹配"，即 480 一致世界。
- **会话身份可自定义**（2026-09-10）：内核支持 `-onlinefix=<appid>`，GUI 联机页已提供「默认身份（480）／自定义身份」开关；
  内核侧 8 处一致化清单见工作区根 `doc/` 下的事实考证（内核侧）。

### 实证轮（v1/v2/v3，PEAK + 日志探针）

- v1 只改 `LobbyInvite_t`：补丁生效但**邀请不走大厅通道**（零 `LobbyInvite:` 日志、零 MMS 流量，
  只来 2 条旧式 `InviteToGame(7005)`）→ LobbyInvite 假设不成立。
- v2 加 Persona 好友改写：真凶不在好友表——改写块被 selfEntry 早退拦掉，且好友 480 状态常在启动游戏前已进客户端缓存，改写永无机会执行。
- v3 定位真凶 = 内核提交 #40（叠加层身份还原）：`BuildSpawnEnvBlock` 把 `SteamOverlayGameId` 还原成真实 AppId
  → 叠加层与 480 大厅不匹配 → `ActivateGameOverlayInviteDialog` 降级成普通好友列表 → 邀请退化为 7005。
- v3 修法 = 撤销叠加层还原（保留 OptedInMask 手柄还原）+ Persona 改写移到 selfEntry 早退之前；代价仅截图标签 / 社区链接显示 Spacewar。
- 完整日志与提交号见 `REF-缺陷与归档.md`。

## 宿主 480 路线（v4，2026-09-17 实测打通；取代上面几轮的内核思路）

- 主张：宿主进程先设 `SteamAppId/SteamGameId=<会话身份>` 并用游戏自带的 `steam_api64.dll` 初始化 Steam，
  再把游戏作为**子进程**拉起——游戏继承环境变量后，它自己的 steam_api 也以同一身份初始化。
- 实测证据：`logs\gameprocess_log.txt` 两条 `AppID 480 adding PID …`（宿主 `OnlineHost.exe` 与 `PEAK.exe` 各一条）；
  `logs\console_log.txt` `GameOverlay: started 'gameoverlayui64.exe' … for game process 161412`；
  PEAK 进程内 `gameoverlayrenderer64.dll` + `steamclient64.dll` 均在。内核日志侧另有
  `Recv k_EMsgClientMMSUserJoinedLobby(6619)` 与 `LobbyChatMsg(6614)` —— 这条路走的是**真大厅**，
  PEAK 好友邀请**由用户与好友实测成功进房**（对照 09-02 用内核原生路线测 PEAK，纯好友邀请是死路：能弹邀请界面但进不去）。
- **全程不碰内核**（不需要 `-onlinefix`）：游戏自身的 appid / 大厅 / P2P 证书 / 叠加层天然一致，没有身份分裂可补。
  实现：GUI 联机页「其他 → DLL 注入」（`OnlineHost/Program.cs` + `OnlineFixService.StartViaHost`）。
- 两个实现坑：① 现代 Valve `steam_api64.dll` 只导出 `SteamAPI_InitFlat`/`InitSafe`/`SteamInternal_SteamAPI_Init`，
  **没有 `SteamAPI_Init`**（PEAK 自带那份 1089 个导出可证），按老名字找会全部落空；
  ② `steam_appid.txt` 不再从 CWD 生效（报错原文 `No appID found…`），必须用环境变量或放在 exe 同目录。
- **回退链 `SteamAPI_InitFlat` → `SteamAPI_InitSafe`**：2026-09-21 实测部分游戏自带的垫片只导出 `InitSafe`（星露谷那份），
  只认 InitFlat 会白白跳过自注册；两个都没有的老垫片才打"跳过自注册"。
- 游戏 exe 由 GUI 按 AppID 自动解析：`libraryfolders.vdf` 找库 → `appmanifest_<appid>.acf` 的 `installdir`
  → 目录同名 exe，否则取目录里最大的 exe（排除 CrashHandler / vcredist / unins）。
- 停止链：先从宿主命令行里取**最后一个带引号的 `.exe`**（第一个可能是宿主自身）→ 按进程名结束游戏
  → 再结束宿主（给宿主 3 秒自己收尾再强杀）。
- **宿主日志 `%LOCALAPPDATA%\OSTGUI\logs\onlinehost.log`**（与 `ostgui.log` 同目录，设置页「打开日志文件」落在同一个文件夹）：
  每次会话写会话头（身份 + 游戏 exe）→ 垫片路径 → 自注册结果；文件法另写写入 / 还原两行。
  `SteamAPI_InitFlat` 已改传 `SteamErrMsg` 缓冲区，失败打印结果码 + 错误串：
  **0=OK / 1=FailedGeneric / 2=NoSteamClient（Steam 没跑或没登录）/ 3=VersionMismatch（垫片与客户端版本不匹配）**
  ——"联机点了没反应"先看这行。

## AppID Changer（环境变量 + steam_appid.txt）实测（2026-09-19 起，2026-09-20 修正）

- **做法（修正后）**：宿主（`OnlineHost.exe --appid-txt "<游戏 exe>" <AppID>`）写 `<游戏 exe 同目录>\steam_appid.txt`，
  **并设 `SteamAppId`/`SteamGameId`/`SteamOverlayGameId`=会话 AppID、自己也先用游戏自带 shim 自注册一次**，
  然后**以该目录为工作目录**拉起游戏，退出后按台账还原。不碰内核。
  CWD == exe 目录，"文件放 CWD"和"放 exe 同目录"两种口径重合，不必赌 SDK 读哪个。
- **⚠️ 原设计（只写文件、不设环境变量）已被实测推翻**：那条路 Steam 会给 `AppID 480 adding PID …PEAK.exe`、
  PEAK 进程内也齐了 `steam_api64/steamclient64/gameoverlayrenderer64`，但 `console_log.txt` 里
  **一条 `GameOverlay: started … for game process` 都没有**，用户真机实测判为"不生效"。
  所以"只写文件就能当 480"这个结论作废——**文件不是关键，进程环境才是**。
- **别走这条路**：AppID Changer 第一版"只写文件、不设环境变量、不加载垫片"→ 实测不生效，关键在进程环境而非文件。
- **对照物：闭源工具 `D:\入库工具\caiNG\CaIInstallNext gen1.05.exe`（2026-09-20 实测提取）**。
  它点「AppID Changer 联机」时再起一个自己的副本（父进程是它的 GUI，GUI 自身**没有**任何 Steam 变量），
  给副本灌上整套 Steam 启动环境，由副本注册并拉起游戏：
  - 游戏（PEAK PID 17228）继承到的：`SteamAppId=480`、`SteamGameId=480`、**`SteamOverlayGameId=480`**、`SteamEnv=1`、
    `SteamAppUser=zjypl07`、`SteamVirtualGamepadInfo=<steam>\config\virtualgamepadinfo.txt`、
    `STEAM_COMPAT_MEDIA_PATH=<steam>\steamapps\shadercache\480/fozmediav1`
    （+ `SteamGenericControllers` 与另两条 `STEAM_COMPAT_*`/`STEAM_FOSSILIZE_*`）
  - Steam 侧证据链：`Game process added : AppID 480 ""…CaIInstallNext gen1.05.exe"", ProcID 10964` →（写文件）→
    `Game process updated … ProcID 17228`（PEAK）→
    `GameOverlay: started 'gameoverlayui64.exe' … for game process 17228`（多条）
    → 控制器配置与 `CAPIJobRequestUserStats` 走的是 480 的 schema
  - 它目录里留着 `temp\SysCache_*\steam_api64.dll` → 先加载 shim 自注册，再拉游戏（与我们的路线 B 同构）
  - **我们跟进的**：`SteamAppId`/`SteamGameId`/`SteamOverlayGameId` + 宿主自注册 + 文件；
    **没跟的**：`SteamEnv`/`SteamAppUser`/`SteamVirtualGamepadInfo`/`SteamGenericControllers`/`STEAM_COMPAT_*`
    （账号名、手柄信息、着色器缓存、设备清单，与联机无关；`SteamEnv` 语义不明）
  - 顺带纠正一条旧结论：我们原先以为"宿主先注册成同一 AppID"是自检游戏的关键，闭源工具**同样是先注册再拉游戏**，与此一致
- **还原机制（没变）**：台账 `%LOCALAPPDATA%\OSTGUI\appid-changer.txt`（游戏目录 / 原本有无该文件 / 原内容，
  LF 分行、原内容原样不 Trim）；宿主先写台账再动文件 → 中途被杀也能还原；
  宿主被杀留台账，GUI 启动时补还原（有台账且没有 `OnlineHost` 在跑才动手）。
- 六项本机自测（含 PEB 直读子进程环境块）见 `REF-缺陷与归档.md`。
- **未测（留给好友实测）**：真机 480 联机进房；"启动器 → 另起的 exe" 场景下的还原时机（代码已标 `ponytail:`）；
  残留文件若长期不还原对"之后从 Steam 正常启动"的实际影响（按最坏情况强制还原 + 启动巡检）。

## 联机路线 A（内核原生）实现事实

- 启动：`steam.exe -applaunch <游戏 AppID> -onlinefix=<会话 AppID>`；其它 `-onlinefix*` 写法一律回落 480
  （内核侧事实见工作区根的内核侧事实考证）
- 检测与停止：GUI 扫描**命令行含 `-onlinefix` 的进程**（排除 `steam.exe`）——命令行是通过读 **PEB** 拿的
  （x64 布局：PEB +0x20 → `RTL_USER_PROCESS_PARAMETERS`，+0x70 → CommandLine 的 `UNICODE_STRING`）；
  读进程环境另见 `REF-日志与诊断.md`（PEB +0x80 = Environment）。停止即 Kill 这些进程
- 前提：Steam 已启动并登录（在线模式）
- 内核侧**会话状态只有一份**（`-onlinefix` 语义），故同一时间只能有一个 480 会话；
  **v1.1.3 起随游戏进程退出即清空**。残留会污染入站 persona 改写 ——
  症状：好友（如 PEAK）被显示成真实游戏名而不是 480，日志里能抓到 `Patched friend persona entries (480 -> 3527290)`
