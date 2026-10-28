# REF-联机（GUI 侧）

> 只收三条联机路线（宿主会话 / 文件会话 / 命令行会话）、会话身份与 PEB 读取；分工与文档地图见工作区根 `README.md`。
> 命名沿革（2026-10）：DLL 注入 → 宿主会话；AppID Changer → 文件会话；内核原生 → 命令行会话。

- **与免 Steam 部署互斥**：GBE 换掉游戏目录的 `steam_api*.dll` 后游戏不再连真 Steam，三条路线全部失效。要联机先「一键还原」；部署后想在模拟器世界里联机需自备联机补丁，本项目不提供。
- **前提**：DLL 注入类工具只在客户端层伪造所有权，匹配由服务端按账号校验，假入库无法联机；通用解是在拥有许可的 AppID（480 Spacewar）下做"一致世界"。
- **会话身份**：默认 480，`-onlinefix=<appid>` 可指定；内核侧的 8 处一致化清单见 `REF-内核-联机会话.md`。

## 宿主会话（v4，当前路线）

- 起点：以 `SteamAppId` / `SteamGameId` = 480 直接启动游戏 exe 即可形成全一致世界，叠加层照常注入（推翻了早先"叠加层必丢"的判断）；带自检的游戏改用"宿主先注册成同一身份"解决。
- 做法：宿主先设 `SteamAppId` / `SteamGameId` = `<身份>`，用游戏自带 `steam_api64.dll` 初始化 Steam，再把游戏作为子进程拉起；子进程继承环境变量后以同一身份初始化。全程不碰内核，游戏自身的 appid、大厅、P2P 证书、叠加层天然一致。实现：`OnlineHost/Program.cs` + `OnlineFixService.StartViaHost`。
- 实测：进程日志两条 `AppID 480 adding PID …`；`GameOverlay: started 'gameoverlayui64.exe' … for game process`；内核侧抓到 `ClientMMSUserJoinedLobby` 与 `LobbyChatMsg`，走的是真大厅；PEAK 好友邀请进房成功。
- 实现坑：现代 `steam_api64.dll` 只导出 `SteamAPI_InitFlat` / `SteamAPI_InitSafe` / `SteamInternal_SteamAPI_Init`，按 `SteamAPI_Init` 找会全部落空；回退链 InitFlat → InitSafe（部分垫片只导出后者）；`steam_appid.txt` 不再从 CWD 生效，要用环境变量或放在 exe 同目录。
- 游戏 exe 解析：`libraryfolders.vdf` 找库 → `appmanifest_<appid>.acf` 的 `installdir` → 目录同名 exe，否则取目录里最大的 exe（排除 CrashHandler / vcredist / unins）。
- 停止链：从宿主命令行取最后一个带引号的 `.exe`（第一个可能是宿主自身）→ 按进程名结束游戏 → 再结束宿主（给 3 秒收尾再强杀）。
- 日志：`%LOCALAPPDATA%\OSTGUI\logs\onlinehost.log`，每次会话写会话头（身份 + 游戏 exe）、垫片路径、自注册结果。`SteamAPI_InitFlat` 已传 `SteamErrMsg` 缓冲区，失败打印结果码：0 = OK / 1 = FailedGeneric / 2 = NoSteamClient（Steam 未运行或未登录）/ 3 = VersionMismatch（垫片与客户端版本不匹配）。"联机点了没反应"先看这行。

## 文件会话（原 AppID Changer）

- 做法：`OnlineHost.exe --appid-txt "<游戏 exe>" <AppID>` 写 `<游戏 exe 同目录>\steam_appid.txt`，设 `SteamAppId` / `SteamGameId` / `SteamOverlayGameId`，自己先用游戏自带 shim 自注册一次，再以该目录为工作目录拉起游戏；退出后按台账还原。
- CWD 等于 exe 目录，"文件放 CWD"与"放 exe 同目录"两种口径重合。
- 关键结论：进程环境才是关键。只写文件、不设环境变量时，Steam 日志有 `AppID 480 adding PID …`、进程内三 DLL 齐全，但没有任何 `GameOverlay: started … for game process`，真机实测判为不生效。闭源工具的同类做法也是对副本灌整套 Steam 启动环境后由它拉游戏。
- 还原：台账 `%LOCALAPPDATA%\OSTGUI\appid-changer.txt`（游戏目录 / 原本有无该文件 / 原内容，LF 分行、原内容原样不 Trim），先写台账再动文件；宿主被杀会留台账，GUI 启动时补还原（有台账且无 `OnlineHost` 在跑才动手）。
- 本机自测覆盖：原本无文件则退出后删除；原内容逐字写回；杀宿主后文件为 480 且台账残留；原文件被独占锁则宿主退出码 5、不动文件、不启动游戏；带残留开 GUI 自动还原；子进程 PEB 直读环境变量与身份一致。
- 未测：真机 480 进房（好友侧）；"启动器 → 另起的 exe"场景下的还原时机；残留文件长期不还原对"之后从 Steam 正常启动"的影响。

## 命令行会话（原内核原生）

- 启动：`steam.exe -applaunch <游戏 AppID> -onlinefix=<会话 AppID>`；其它 `-onlinefix*` 写法一律回落 480。前提 Steam 已启动并登录。
- 检测与停止：扫命令行含 `-onlinefix` 的进程（排除 `steam.exe`），停止即 Kill。
- 内核侧会话状态只有一份，同一时间只能有一个 480 会话，v1.1.3 起随游戏进程退出即清空。残留会污染入站 persona 改写，症状是好友显示成真实游戏名，日志 `Patched friend persona entries (480 -> 3527290)`。

## 读进程环境（PEB 布局）

- PEB+0x20 → `RTL_USER_PROCESS_PARAMETERS`，+0x70 = CommandLine、+0x80 = Environment。
- 环境块按 128KB 整块读再按 `\0` 拆（`EnvironmentSize` 位置随版本变）。

## 历史：480 邀请失效的三轮内核尝试

- v1 只改 `LobbyInvite_t`：补丁生效但邀请不走大厅通道（零 `LobbyInvite:` 日志，只有旧式 `InviteToGame(7005)`）。
- v2 加 Persona 好友改写：被 selfEntry 早退拦掉，且好友的 480 状态常在启动游戏前已进客户端缓存。
- v3 定位真凶：内核提交 #40 的叠加层身份还原（`BuildSpawnEnvBlock` 把 `SteamOverlayGameId` 还原成真实 AppId），叠加层与 480 大厅不匹配，`ActivateGameOverlayInviteDialog` 降级。修在内核分支 `fix/onlinefix-lobby-invite`；PEAK 好友邀请能弹界面但进不去，结论转向宿主会话。
