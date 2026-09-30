# REF-成就（GUI 侧）

> 只收：成就链路的成因、成就页实现、游玩时长的归属。分工与文档地图见工作区根 `README.md`。

## 为什么假入库游戏的成就显示不出来

- **服务端拒答**：客户端拉成就走 818 → 819（旧路径 `Player.GetUserStats#1`，151 → 147）。未拥有的 app，Valve 回 `eresult: 2` + 空 body——没有 schema，也没有任何解锁数据。
- **客户端本地不留**：现代客户端 `userdata\<id>\<appid>\` 下没有 `stats` 目录（旧版 `achievements.bin` 已消失）。成就完全服务端权威，SAM 式"改本地文件"因此不通。
- **内核的 819 兜底会遮住显示**：对配置里命中该 depot 的 app（`LuaConfig::HasDepot`），内核会 `clear_stats()` + `clear_achievement_blocks()` 并置 `eresult=OK`，随后用 CloudRedirect 的云端成就覆盖；CR 没数据就是"清空 + 盖空"。客户端拿成就只走这条 819，所以刚改过成就的游戏下次启动会看不到（下一次往返或内存态又会盖回来）。实现见内核侧 `REF-内核-*`。
- **写入有效，服务端也存**：`SetAchievement` + `StoreStats` 走 5466 → Valve 回 821，重启 Steam 后成就仍在。所以"假入库的成就留不住"只对**显示层**成立，对服务端不成立。

## 成就页实现

- **路线**：与 SAM 相同——只走 Steam 客户端接口，不改内核。
- **定义**：读本地 `<Steam>\appcache\stats\UserGameStatsSchema_<appid>.bin`（Valve 二进制 KeyValues），由 `Services/SteamStatsSchema.cs` 自解析（条目类型字节见该文件顶部注释）。本地缺这个文件的 app，要先启动一次游戏让客户端缓存下来。
- **列表**：入库档（lua）+ 客户端认为拥有的游戏；重开秒开，lua 档或 `appinfo.vdf` 有变化会自动重扫（`Services/AchievementListCache.cs`）。
- **读写**：`Services/SteamStatsChild.cs` 在**短命子进程**（`OSTGUI.exe --stats-dump` / `--stats-apply`）里经 `steamclient64.dll` 的 `CreateInterface` 调 `ISteamUserStats013`：`RequestUserStats` → `GetAchievementAndUnlockTime` / `SetAchievement` → `StoreStats`。
  必须在独立进程里做的三个原因：`SteamAppId` 要在 steamclient 首次加载前设定、一个进程只能锁一个 appid、加载它的进程会被 Steam 当作游戏进程。
- **接口声明**：vtable 顺序取自 `SteamApi/Interfaces/ISteamUserStats013.cs`（配套 `SteamApi/Wrappers/SteamUserStats013.cs`）。**不要手写 vtable 索引**——按"偏移"猜函数指针会调到别的函数并必然越界（0xC0000005，catch 拦不住）。
- **留底**：`%LOCALAPPDATA%\OSTGUI\achievements\<appid>.json`（勾选即原子写：tmp → Move），是唯一可靠副本。
- **离线自检**：`--stats-schema-dump` 只解析 schema，不连 Steam。
- **边界（别当 bug）**：
  - 不能自定义解锁时间：`SetAchievement` 只有解锁 / 回锁两态，时间由 Steam 自己记。
  - 以 480 启动的游戏（联机路线），其成就属于 480。
  - 读路径不回拉：客户端已有状态时不再 `RequestUserStats`（那会重拉一次，并可能抹平已有状态）。

## 游玩时长

- 唯一副本在 `userdata\<id>\config\localconfig.vdf` 的 `Apps\<appid>`（`Playtime` / `Playtime2wks` / `LastPlayed`）。未拥有 app 的时长不上传 Valve，Steam 清掉这条就永久丢。
- **GUI 目前不读写它**：留底与恢复都还没做。

## 待办

- 显示层要稳需从内核侧下手：CR 无数据时不要清空 819，或改成叠加我们自己的留底；也可走已由内核托管的 CloudRedirect（其 `config.json` 有 `sync_achievements` / `sync_playtime`）。
