# REF-成就（GUI 侧）

> 只收 成就链路 / 成就页实现 / 游玩时长留底；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26）。

## 成就链路（查证 + 实现事实，2026-09-23）

**为什么假入库游戏的成就"会丢"（三条链都断）**：
1. **服务端拒答**：客户端拉成就是 `818 → 819`（旧路径 `Player.GetUserStats#1`，151→147）。
   未拥有的 app，Valve 回 `eresult: 2` + **空 body**——2026-09-23 实测：819 body 仅 **13 字节**（`game_id`+`eresult`），
   147 body 全空。**没有 schema、没有任何解锁数据**。
2. **客户端本地不留**：现代客户端 `userdata\<id>\<appid>\` 下**没有 `stats` 目录**（旧版 `achievements.bin` 已消失）
   → 成就完全服务端权威，服务端不给就是没有（**SAM 式"改本地文件"这条路因此不通**）。
3. **内核的 819 处理会"遮住"成就**（这条才是"拉不出来"的落点）：内核原有两条兜底 ——
   ① 出站把 `steamid` 换成"供体账号"（`setStat(appid,"steamid")` > `stats.opensteamtool.com/<appid>` > 硬编码 `76561198028121353`）；
   ② 入站 819 用 CloudRedirect 的本地库覆盖。**② 现在没生效**（`cloud_redirect.dll` 不在 `<Steam>`、`[cloud] enabled = false`）
   ⇒ `HandleRecv_ClientGetUserStatsResponse` 对**任何在 addappid 里的 app** 无条件 `clear_stats()` +
   `clear_achievement_blocks()` + eresult=OK，CR 没数据就是「清空 + 盖空」。
   客户端拿成就只走这条 819，被清空就什么都看不到 —— 与"刚改过成就的游戏下次启动加载不出成就"吻合
   （刚改过的 app 正是客户端下次会去拉的那个；再存一次或等一会看着又好了，是内存态/下一次往返盖回来）。
4. **写入是有效的，而且服务端会存**（2026-09-23 用户实测 + 内核日志）：`SetAchievement` + `StoreStats` 走 5466 → Valve 回 821，
   **重启 Steam 后成就仍在** ⇒ 后端不校验"是否拥有"，"假入库的成就留不住"这个早期判断**只对显示层成立、对服务端不成立**。
   客户端本地也确实不缓存：`userdata\<id>\<appid>\` 下始终**没有 `stats` 目录**（2026-09-23 复查仍是空的）。

> **更正（2026-09-23 晚）**：本节早上写的"出站 818/5466 今天一次都没出现"只是**那一个 Steam 会话**的现象；
> Steam 重启后（22:49，新 tid）内核日志里 `Send eMsg k_EMsgClientStoreUserStats2(5466)` 与
> `Recv ...StoreUserStatsResponse(821)` 都正常出现，"断链"说法作废。

**游玩时长**：客户端写在 `userdata\<id>\config\localconfig.vdf` 的 `Apps\<appid>`（`Playtime` / `Playtime2wks` / `LastPlayed`，
实测有值：3321460=541 分钟、1091500=53 分钟）；未拥有 app 的时长**不上传 Valve**，本地是唯一副本
→ app 从库里消失后 Steam 清掉这条就永久没了（用户反馈；机制上无第二副本）。留底/恢复属下一步。
**`localconfig.vdf` 的 `Apps\<appid>` 是游玩时长唯一副本**，Steam 清掉即永久丢。

**本项目实现（成就页，2026-09-23）**：GUI 侧走 **SAM 同款路线**（对照 `RefProjects/1-在用/Fluent-Steam-Lua`，它同样没改内核）——
- **定义**：读本地 `<Steam>\appcache\stats\UserGameStatsSchema_<appid>.bin`（Valve 二进制 KeyValues；
  `Services/SteamStatsSchema.cs` 自己解析，类型字节 0x00 子表 / 0x01 字符串 / 0x02 int32 / 0x03 float / 0x07 uint64 / 0x08 层结束）。
  库里 38 个 app 有 **33 个**本地已有该文件，其余要先启动一次游戏让客户端缓存。
- **读写**：`Services/SteamStatsChild.cs` 在**短命子进程**（`OSTGUI.exe --stats-dump|--stats-apply`）里走
  `steamclient64.dll` 的 `CreateInterface` + `Steam_BGetCallback`/`Steam_FreeLastCallback`，调 **`ISteamUserStats013`**：
  `RequestUserStats` → `GetAchievementAndUnlockTime` / `SetAchievement` → `StoreStats`。
  必须在独立进程里做的原因：`SteamAppId` 要在 steamclient 首次加载前设定、一进程只能锁一个 appid、
  加载它的进程会被 Steam 当游戏进程（与 `SteamTicketExtractor` 同）。
  - **vtable 索引不写死**：按 SAM 的接口声明顺序取（`SetAchievement`=6 / `GetAchievementAndUnlockTime`=8 / `StoreStats`=9 /
    `GetNumAchievements`=13 / `RequestUserStats`=15），再用 **schema 里的成就条数现场校验 `GetNumAchievements`**
    决定偏移 0 或 1（客户端 vtable 可能多一个 `RequestCurrentStats`）；两个候选调用都无副作用。
  - **别走这条路**：手写 vtable 调 `ISteamUserStats013` → 偏移 1 探针命中 `GetAchievementName(uint32)` 却未给参，
    必然 0xC0000005（原生越界、catch 拦不住），照搬 SAM.API。
- `--stats-schema-dump <appid> <steamPath> <out.txt>`：schema 解析离线自检开关（不连 Steam）。
- **留底**：`%LOCALAPPDATA%\OSTGUI\achievements\<appid>.json`（勾选即原子写）——**唯一可靠副本**。
- **边界（别当 bug）**：① 写入会进 Valve 的服务器（重启 Steam 后仍在），但**成就页可能显示不出来**
  ——内核拉取成就时会清空 addappid 游戏的响应（见上 §3），显示层被它遮住；② **不能自定义解锁时间**
  ——`SetAchievement` 只有解锁/回锁两态，时间由 Steam 自己记（要改时间得直接改 5466 包，属内核路线）；
  ③ 以 480 启动的游戏（联机路线）成就属于 480；④ 一个进程只能锁一个 appid，所以每次动手都开一个短命子进程（约 2–5 秒）；
  ⑤ 读路径先读客户端缓存、不回拉（`RequestUserStats` 会重拉并可能把客户端里已有的状态抹平）。

**下一步（让显示层稳）**：两条路 —— ① **内核侧最小改动**：CR 没数据时**别清空** 819
（`Hooks_NetPacket.cpp` 的 `HandleRecv_ClientGetUserStatsResponse`），或改成叠我们自己的留底；
② 走 CloudRedirect：它已由内核托管（`Utils/CloudRedirect/CloudRedirectHost.cpp`，其 `config.json` 有
`sync_achievements` / `sync_playtime`），DLL 来源 = `github.com/Selectively11/CloudRedirect`（FSL 在构建期按版本 + SHA256 拉取）。
**游玩时长的留底/恢复**仍是独立一条线（`localconfig.vdf` 的 `Apps\<appid>`）。

- ⚠️ 导航奖杯字形 `E7C1` 只核过存在性，**字形语义未目视确认**。
