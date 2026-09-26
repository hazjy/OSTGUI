# REF-归档-测试与采样（GUI 侧）

> 只收 实测流水账与采样数字（过程料，结论已归各域 REF）；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26）。

## 480 联机实证轮（v1/v2/v3，PEAK + 日志探针）

- **v1（仅 LobbyInvite_t 改写）实测**：补丁生效（`SpawnProcess: 3527290 -> 480`、`OnlineFix: 480 -> name 'PEAK'` 均出现），
  但**全程零 `LobbyInvite:` 日志、零 MMS/大厅流量、零 JoinLobby 尝试**——邀请根本没走大厅通道，只收到 2 条旧式 `InviteToGame(7005)`。
  LobbyInvite 假设不成立。
- **v2（增 Persona 好友改写 + 全回调探针）实测**：探针显示 21 次 cb=304 等；但 `Persona friend` 改写一次未触发——两处缺陷：
  ① 改写块放在 `if (!selfEntry) return false` 之后，好友增量推送常不含 self 条目被提前拦掉；
  ② 好友的 480 状态若在启动游戏前已进客户端缓存，后续无推送 → 改写永远无机会执行。
- **v3（真凶落点）**：结合"邀请时弹出的是普通好友界面"定位到内核提交 **#40（Restore controllers and overlay identity）**：
  `BuildSpawnEnvBlock` 把 `SteamOverlayGameId` 还原成真实 AppId → 叠加层身份与 480 空间大厅不匹配
  → `ActivateGameOverlayInviteDialog` 降级为普通好友列表 → 邀请退化为 7005。**v3 撤销叠加层还原**
  （保留 OptedInMask 手柄还原），叠加层回到 480，邀请对话框正确绑定 480 大厅；代价仅截图标签/社区链接显示 Spacewar。
  同时把 Persona 改写移到 selfEntry 早退之前。
  - 提交：内核分支 `fix/onlinefix-lobby-invite`（v1: 94a80b8，v3: 36708b9）。当时的三份备份
    （`20260825-kernel-480fix` / `20260825-kernel-v2-friendpatch` / `20260826-kernel-v2-persona`）
    **已随备份目录整理删除**；现存内核备份见工作区 `内核备份/`（2026-09-20 清理并更名，原 `ost-backups/`）。

## AppID Changer 本机自测（六项）

- **本机自测**：① 原本无该文件 → 退出后删除；② 原本是 `3527290`（7 字节、无换行）→ 逐字写回；
  ③ 杀掉宿主 → 文件 480 + 台账残留、游戏照跑；④ 原文件被独占锁 → 宿主退出码 5、什么都不动、游戏不启动；
  ⑤ 带残留开 GUI → 自动还原并清台账；⑥（修正后新增）子进程确实继承到
  `SteamAppId`/`SteamGameId`/`SteamOverlayGameId=480`（PEB 直读子进程环境块）。

## 显示效果采样（本机 Windows 11 build 26200 / 25H2，窗口 2398×1695 @2.25x，采样窗口内三处背景像素）

| 应用主题 | 档位 | 内容区右上 | 内容区右下 | 导航栏中部 |
|---|---|---|---|---|
| 浅色 | 无 | `#F9F9F9` | `#FDFDFD` | `#F3F3F3` |
| 浅色 | 云母 | `#F8F9FC` | `#FDFDFE` | `#F0F3F9` |
| 浅色 | 亚克力 | `#EEEFF2` | `#FAFAFA` | `#E1EAE8`（带壁纸色偏） |
| 深色 | 无 | `#272727` | `#272727` | `#202020` |
| 深色 | 亚克力 | `#2C2F41` | `#3A3A3C` | `#2A3130`（带壁纸冷色偏） |

## 免 Steam 部署夹具实测

- 实测：夹具两轮（完整产物正常还原；`.bak` 被独占锁定时只该项失败、其余照做、报"还原未完成"）
  + **用户真机 Ib 部署 → 还原后正常**。

## Sudama 内存对比

- 三种取键做法的实测内存对比、"峰值后压大对象堆"见 `doc/开发踩坑-环境.md` 的 Sudama 取键内存记录；
  正文结论见 `REF-入库与密钥.md`。
