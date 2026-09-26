# REF-免Steam部署（GUI 侧）

> 只收 Denuvo 授权与 .ost / 免 Steam 部署与一键还原；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26）。

## Denuvo / 授权（查证）

- 需 **AppTicket + ETicket 双票**，不可本地伪造；ETicket 由 Steam 实时签发，**30 分钟有效**（报错 88500005）
- 提取授权存在"本地缓存的过时授权"假象且无法程序化验证账号正伪（ETicket 请求会被内核拦截）
  → 采用导出时弹窗提醒策略
- `.ost` 为明文 JSON：AppTicket / ETicket / Source(Steam 用户名) / CreatedAt / ExpiresAt / UseCount / ExporterVersion
- 导入写注册表（`HKCU\Software\Valve\Steam\Apps\<appid>` 的 `AppTicket` / `ETicket`；**GUI 不写 `SteamID`**），
  本机任意 Steam 账号可用；部分游戏 DLC 也受 D 加密，只带主游戏票时 DLC 可能解锁失败
- 每账号每天最多 5 台新机器激活；已激活机器不消耗
- 与 .cw/.shiki（流畅入库私有格式）不兼容是刻意选择
- **身份模式（`[denuvo] mode`）的两种语义、切换后果与协议层边界**：内核侧事实见工作区根 `doc/` 下的事实考证（内核侧）；
  GUI 侧读写实现见 `REF-版本锁定与Denuvo模式.md` §D 加密模式读写

## 免 Steam 部署（GSE / SAC 对齐，GUI 侧实现事实）

- 流程：选游戏 EXE + AppID →（有壳时）Steamless 脱壳替换 EXE → GSE(Goldberg) 模拟器部署进游戏目录
  → 可选 SteamAPICheckBypass（winmm 劫持隐藏模拟器痕迹）。原文件备份为 `.bak`，删 `steam_settings` 并改回 `.bak` 即还原
- 对齐 SAC（SteamAutoCrack）的部署逻辑与 ini 配置格式；无壳游戏自动跳过脱壳不中断；
  2016 前 SDK 的老游戏会额外生成 `steam_settings/steam_interfaces.txt`（gbe_fork 需要）
- **资源嵌入**：所有二进制（Steamless CLI/插件、GSE 模板、Bypass）以 EmbeddedResource 打进 `NoSteamLauncher.dll`，
  运行时解压到 `%TEMP%\OSTGUI_NoSteamLauncher\<版本>\` 并做关键文件完整性校验，缺失自动重解压
- 已知冲突：自带 winmm 依赖或有反作弊的游戏对 Bypass 可能不适配（默认关闭）
- **一键还原**照抄 SAC `SteamAutoCrack.Core/Utils/Restore.cs` 四步：
  ① 目录里存在 `SteamAPICheckBypass.json` 才递归删 `version.dll` / `winmm.dll` / `winhttp.dll`（不做哈希校验）；
  ② 递归删 `steam_interfaces.txt` / `local_save.txt` / `SteamAPICheckBypass.json`；
  ③ 递归把**所有** `*.bak` 换回原名（这里用覆盖式 `File.Move`，SAC 是先删再改名，结果一致且失败不丢备份）；
  ④ 递归删所有 `steam_settings` 目录。③ 是通配，游戏自带的 `*.bak` 也会被换回原名
  ——这是照抄 SAC 的既定代价（曾试过白名单版，被要求改掉）
- 还原**只动游戏目录、不解压资源**（不走 `EnsureExtracted()`）；逐项日志 + 还原后复查残留：
  被占用（游戏在跑）时逐条报失败并输出"还原未完成"，不报假成功；`%APPDATA%\GSE Saves\<appid>` 只提示路径不删；
  `steam_appid.txt` 归 AppID Changer 台账管，还原不碰；幂等（无残留时输出"未发现模拟器残留（可能已还原）"）
- 夹具两轮实测 + 用户真机 Ib 部署 → 还原后正常，见 `REF-缺陷与归档.md`。
