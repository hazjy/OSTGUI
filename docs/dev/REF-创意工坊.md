# REF-创意工坊（GUI 侧）

> 只收 创意工坊下载（Phase 1）；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26，2026-09-26 按领域重划）。

## 创意工坊下载（Phase 1，GUI 侧实现事实）

- 创意工坊内容走 depot 加密管线：Steam 客户端把订阅的 item 当作 **depot = consumer_app_id（游戏 AppID）** 下载
  （depotcache 命名 `<AppID>_<manifestID>.manifest`），解密密钥按 ConfigStore `…\<AppID>\DecryptionKey` 读取
- 内核已覆盖两环：manifest code（`GetManifestRequestCode` 劫持；第三方源只要收录了该 workshop gid 即可）
  与密钥注入（`ConfigStoreGetBinary` hook 从 Lua 的 DepotKeySet 取 key）
- 缺口曾是主游戏行 `addappid(appid)` 不带密钥 → 内核无 key 可喂 → 订阅下载报"内容仍处于加密"。
  Phase 1（v1.3.x）：`LuaBuilder` 主游戏行自动带上 Sudama depotkeys 中 **AppID 自身**的密钥
  （社区称"创意工坊密钥"，须恰好 64 位 hex）→ **新入库**即具备工坊下载解密能力；
  已入库游戏需重新入库或手动把主行改成 `addappid(appid, 1, "<key>")`
- 边界：**限制匿名的工坊**（部分游戏 / 新 manifest 的 code 请求被服务器拒绝）无法绕过，需真实拥有该游戏的账号；
  **老式独立 workshop depot**（SteamDB 标注 Workshop 的 depot，如 Dying Light）需该 depot 单独密钥，Phase 1 不覆盖
