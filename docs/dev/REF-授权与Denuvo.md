# REF-授权与Denuvo（GUI 侧）

> 何时读：改 `.ost` 导入导出、D 加密身份模式读写，或排查 88500xxx 报错时。
> 管什么：Denuvo 授权（`.ost` 导入导出）与 D 加密身份模式读写。
> 不写什么：身份模式语义与协议层边界见 `ZSteamTool/docs/dev/REF-内核-Denuvo与授权.md`。

## Denuvo / 授权

- 需 **AppTicket + ETicket 双票**，不可本地伪造；ETicket 由 Steam 实时签发，**30 分钟时效**，仅"需要重新激活"时才校验（设备许可在则不重复验票）。注意：`88500005` 属 Denuvo 的反篡改 / 滥用处罚族（约 2 周），票据过期多表现为 `88500012`；勘误见 `ZSteamTool/docs/dev/REF-内核-Denuvo与授权.md` §授权窗口。
- 提取授权存在"本地缓存的过时授权"假象，且无法程序化验证账号正伪（ETicket 请求会被内核拦截）→ 采用导出时弹窗提醒策略。拦截的具体形态：lua 在场时内核按目标 appid 判 `HasDepot`，把该 app 的所有权票 / 加密票换成伪造票或池账号票；**导出仍会 `success=True`，但票不是原生票**。
- `.ost` 为明文 JSON：AppTicket / ETicket / Source(Steam 用户名) / CreatedAt / ExpiresAt / UseCount / ExporterVersion。
- 导入写注册表（`HKCU\Software\Valve\Steam\Apps\<appid>` 的 `AppTicket` / `ETicket`；GUI 导入不写 `SteamID`，内核在真许可会话会自动补），本机任意 Steam 账号可用；部分游戏 DLC 也受 D 加密，只带主游戏票时 DLC 可能解锁失败。
- 每账号每天最多 5 台新机器激活；已激活机器不消耗。
- 与 .cw/.shiki不兼容是刻意选择（私有格式）。
- 身份模式（`[denuvo] mode`）的两种语义、切换后果与协议层边界见 `ZSteamTool/docs/dev/REF-内核-Denuvo与授权.md`；GUI 侧读写实现见本文件 §D 加密模式读写。

- 提取 / 导出授权前后，先在入库页「更多 → 禁用解锁」停掉该 app 的 lua：有真许可时内核的伪造会造出第二条所有权来源。`.lua` ↔ `.lua.disabled` 改名即热重载，不用重启 Steam。做法与判据见 `ZSteamTool/docs/dev/REF-内核-Denuvo与授权.md` §什么时候不要让内核伪造所有权。家庭共享的**运行时启动**已由自动落盘覆盖（见下节），不再需要为进游戏而禁用 lua；导出前仍必须禁用。

## 票据自动落盘（2026-11-20，修切号后 88500000）

- **背景**：客户端 11.23.65.88 起，Steam 把所有权票缓存到 `userdata\<账号>\config\localconfig.vdf` 的 `apptickets`（按账号一份），不再写 `HKCU\...\Apps\<appid>`。内核只读 HKCU；切号后新账号库为空 → `858` 被拒 → 只能伪造 → `88500000`。Steam 的 `apptickets` 内核只用来取供体票，不作为目标票来源。
- **修复**（内核 `95725b4`）：`858` 成功分支与 IPC `GetAppOwnershipTicketExtendedData` 的有效许可分支，把服务端真票写入 HKCU 的 `AppTicket` + `SteamID`。条件是 `HasDepot` 且 `HasValidLicense`（真拥有或家庭共享），假入库账号拿不到 OK 票、不会写入。
- **效果**：正版 / 家庭共享账号启动一次即完成“落票”；之后切到无许可账号，授权窗口内直接出这张票、身份伪装成票内 SteamID，Denuvo 交叉校验通过，不再退化到伪造票。
- **实测**（2026-11-20）：gao819（家庭共享）启动后 HKCU 得到 184 字节、SteamID `76561198767076542` 的真票；切 ZJY 启动成功，ZJY 的 `apptickets` 里落下同一张 184 字节票（Steam 把内核替换后的 858 响应当成票缓存）。
- **不再依赖 lua 的 `setAppTicket`**：手动导入 `.ost` / lua 脚本仍可用，作为离线、换机时的兜底。
- **限制**：Release 构建把 `LOG_INFO` 裁掉，日志里没有 `captured real ticket` 行；验证看 HKCU 与 `apptickets` 的字节和 SteamID，或换 Debug 版内核看 `main.log`。

## 提取链路（GUI 侧）

- `DenuvoViewModel.ExtractAsync` → `SteamTicketExtractor.ExtractInSubprocessAsync`：短命子进程 `OSTGUI.exe --extract-ticket <appid> <outfile>`，避开 GUI 自身已加载的 Steam DLL。
- 子进程设 `SteamAppId` / `SteamGameId`，`LoadLibraryEx` 加载 `<Steam>\steamclient64.dll`，`CreateInterface("SteamClient023")` 建 pipe 并 `ConnectToGlobalUser`。
- 先 `RequestEncryptedAppTicket`（回调 154，最多等 15 s）拿 ETicket，再 `ISteamAppTicket001::GetAppOwnershipTicketData` 拿 AppTicket；失败统一提示"请确认已登录正版账号且在线，该账号需拥有此游戏"。

## D 加密模式读写（GUI 侧事实）

- **唯一数据源** = 内核配置文件 `<Steam 目录>\opensteamtool.toml` 的 `[denuvo] mode`（**不是** GUI 自己的 `%LOCALAPPDATA%\OSTGUI\config.json`）。
- 读写实现：`SteamDllService.GetConfigPath()/GetDenuvoMode()/SetDenuvoMode()`；写入**保留注释只改 mode 行**（段 / 键缺失自动追加、行内注释可解析、CRLF 保持、非法值拒绝）。
- 生效方式：内核 `ConfigFileWatcher` **整文件变更即热重载** → 改完无需重启 Steam；进设置页会从文件回读（外部手改可见）。
