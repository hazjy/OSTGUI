# REF-免Steam部署（GUI 侧）

> 只收 免 Steam 部署（GSE / SAC 对齐）与一键还原；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26，2026-09-26 按领域重划）。

## 免 Steam 部署（GSE / SAC 对齐，GUI 侧实现事实）

- 流程：选游戏 EXE + AppID →（有壳时）Steamless 脱壳替换 EXE → GSE(Goldberg) 模拟器部署进游戏目录
  → 可选 SteamAPICheckBypass（winmm 劫持隐藏模拟器痕迹）。原文件备份为 `.bak`，删 `steam_settings` 并改回 `.bak` 即还原
- **备份不覆盖**：EXE 与 DLL 一致——`.bak` 已存在就保留（跳过备份），重复部署不会用脱壳后的 EXE 覆盖唯一原件（对齐 SAC 的判定）
- **游戏自带的 `Plugins` 目录不会被删**：脱壳要往 `<游戏目录>\Plugins` 拷插件（Steamless 按目标目录找插件），
  该目录已存在且**不是我们留下的**（无 `Steamless.API.dll` + `Variant*.dll` 标记）→ 就地改名成 `Plugins.ostgui-bak`
  暂存、脱壳结束原名还原；只有确认是我们的才整目录删。暂存失败（占用 / 权限）直接中止脱壳并说明，绝不硬删
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
- **部署根 = 游戏根，不是 exe 所在目录**：UE 布局的 `steam_api64.dll` 在 `<根>\Engine\Binaries\ThirdParty\Steamworks\Steamv<NNN>\Win64\`，与 exe（`<根>\<项目>\Binaries\Win64\`）**不同枝**——`GameRootResolver` 向上最多找 3 层、命中「含 `Engine\Binaries\ThirdParty\Steamworks`」的那层即为根（绝不上溯到盘根或 `steamapps\common`），找不到就仍用 exe 目录（普通游戏行为不变）。**部署与还原走同一套根解析**，否则 UE 布局会「部署到 Engine、还原只扫 exe 目录」留残留
- 还原**只动游戏目录、不解压资源**（不走 `EnsureExtracted()`）；逐项日志 + 还原后复查残留：被占用（游戏在跑）时逐条报失败并输出"还原未完成"，不报假成功；每个文件操作先按 **4×250 ms 重试**——刚写下去的 11 MB 模拟器 DLL 常被杀软 / 索引器短暂映射住，此时覆盖式 `File.Move` 报 `Access denied`，重试即可自愈（2026-10-05 合成 UE 目录树实测：无重试必失败、有重试 571 ms 后成功）；`%APPDATA%\GSE Saves\<appid>` 只提示路径不删；
  `steam_appid.txt` 归 AppID Changer 台账管，还原不碰；幂等（无残留时输出"未发现模拟器残留（可能已还原）"）

## 归档：免 Steam 部署夹具实测

- 实测：夹具两轮（完整产物正常还原；`.bak` 被独占锁定时只该项失败、其余照做、报"还原未完成"）
  + **用户真机 Ib 部署 → 还原后正常**。
