# REF-免Steam部署（GUI 侧）

> 只收 免 Steam 部署（GSE / SAC 对齐）与一键还原；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26，2026-09-26 按领域重划）。

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

## 归档：免 Steam 部署夹具实测

- 实测：夹具两轮（完整产物正常还原；`.bak` 被独占锁定时只该项失败、其余照做、报"还原未完成"）
  + **用户真机 Ib 部署 → 还原后正常**。
