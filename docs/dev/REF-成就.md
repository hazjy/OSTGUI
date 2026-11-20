# REF-成就（GUI 侧）

> 何时读：核对假入库游戏的成就为何显示不出来、改成就页读写或游玩时长归属时。
> 管什么：假入库游戏成就为何显示不出来、成就页实现、游玩时长归属。
> 不写什么：内核 819 / CloudRedirect 侧实现见 `ZSteamTool/docs/dev/DEV-NOTES.md` 与内核 `REF-内核-*`；UI 偏好不记。

## 为什么假入库游戏的成就显示不出来
- **服务端拒答**：未拥有的 app，Valve 对 stats 读取回 `eresult: 2` + 空 body：没有 schema，也没有任何解锁数据。2026-11-14 起客户端改走 `Player.GetUserStats#1`（151 → 147），`achievement.log` 里不再出现 818/819。
- **客户端本地不留**：现代客户端 `userdata\<id>\<appid>\` 下没有 `stats` 目录（旧版 `achievements.bin` 已消失）；成就完全服务端权威，SAM 式"改本地文件"因此不通。
- **内核的兜底会遮住显示**：对配置里命中该 depot 的 app（`LuaConfig::HasDepot`），内核会 `clear_stats()` + `clear_achievement_blocks()` 并置 `eresult=OK`，819 随后用 CloudRedirect 的云端成就覆盖；CR 没数据就是"清空 + 盖空"。147 路径只 `clear_stats()`、不回填，且带 `sha_schema` 的请求会跳过捐赠号改写（`Hooks_NetPacket.cpp:274`）。
- **写入有效，服务端也存**：`SetAchievement` + `StoreStats` 走 5466 → Valve 回 821，重启 Steam 后成就仍在；所以"假入库的成就留不住"只对**显示层**成立。

## 成就页实现
- **路线**：与 SAM 相同：只走 Steam 客户端接口，不改内核。
- **定义**：读本地 `<Steam>\appcache\stats\UserGameStatsSchema_<appid>.bin`（Valve 二进制 KeyValues），由 `Services/SteamStatsSchema.cs` 自解析（条目类型字节见该文件顶部注释）；本地缺这个文件的 app 要先启动一次游戏让客户端缓存下来。
- **列表**：入库档（lua）+ 客户端认为拥有的游戏；重开秒开，lua 档或 `appinfo.vdf` 有变化会自动重扫（`Services/AchievementListCache.cs`）。
- **读写**：`Services/SteamStatsChild.cs` 在**短命子进程**（`OSTGUI.exe --stats-dump` / `--stats-apply`）里经 `steamclient64.dll` 的 `CreateInterface` 调 `ISteamUserStats013`：`RequestUserStats` → `GetAchievementAndUnlockTime` / `SetAchievement` → `StoreStats`。必须在独立进程里做的三个原因：`SteamAppId` 要在 steamclient 首次加载前设定、一个进程只能锁一个 appid、加载它的进程会被 Steam 当作游戏进程。
- **接口声明**：vtable 顺序取自 `SteamApi/Interfaces/ISteamUserStats013.cs`（配套 `SteamApi/Wrappers/SteamUserStats013.cs`）；**不要手写 vtable 索引**：按"偏移"猜函数指针会调到别的函数并必然越界（0xC0000005，catch 拦不住）。
- **留底**：`%LOCALAPPDATA%\OSTGUI\achievements\<appid>.json`（勾选即原子写：tmp → Move），是唯一可靠副本；**离线自检** `--stats-schema-dump` 只解析 schema，不连 Steam。
- **边界（别当 bug）**：不能自定义解锁时间（`SetAchievement` 只有解锁 / 回锁两态，时间由 Steam 自己记）；以 480 启动的游戏其成就属于 480；读路径不回拉（客户端已有状态时不再 `RequestUserStats`，那会重拉一次并可能抹平已有状态）。

## 游玩时长
- 唯一副本在 `userdata\<id>\config\localconfig.vdf` 的 `Apps\<appid>`（`Playtime` / `Playtime2wks` / `LastPlayed`）；未拥有 app 的时长不上传 Valve，Steam 清掉这条就永久丢。
- **GUI 目前不读写它**：留底与恢复都还没做。

## 待办
- 显示层修复已立项：见 `doc/TODO/待办/261120-CR成就接入.md`（147 回填、恢复 CR 部署、补齐 8-18 之后的解锁）。
