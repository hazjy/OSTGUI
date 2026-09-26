# REF-入库与Lua（GUI 侧）

> 只收 Lua 配置语义 / 搜索与入库链路 / Sudama 密钥缓存；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26，2026-09-26 按领域重划）。

## Lua 配置语义（内核查证）

```lua
addappid(2001760)                                  -- 解锁主游戏（假装拥有）
addappid(2001761, 1, "64位hex")                   -- 解锁 depot + 注入解密密钥
addappid(2827030)                                 -- DLC：只解锁不带密钥（多数复用本体 key）
addtoken(2001760, "令牌")                          -- 受限 app 获取 appinfo 需要
setManifestid(2001761, "gid", 大小)               -- 固定版本（锁 depot 的 manifest）
```

- `addappid` 第二参数内核实际忽略（源码只读第 1、3 参数）；密钥必须恰好 64 字符否则不生效
- `--` 开头是注释，内核忽略；函数名不区分大小写；文件放 `config\lua\{AppId}.lua`，内核热重载
- `setManifestid` 有则锁版本，无则自动更新；大小可省略（内核实际把 size 强制为 0）

## 搜索与入库链路（GUI 侧实现事实）

- **搜索统一走 `cc=us`**（AppID 详情、storesearch 主源、HTML 备源、关键词源均已统一）→ 成人内容不再被 cn 区过滤，
  名称搜索可直接命中；**能搜到 ≠ 能入库**（还需密钥存在）
- **清单源与密钥源分离**：MHub = 清单源（**仅最新版**），Sudama = 仅密钥源；`IsImplementedSource` 只认 `mhub` / `sudama`
- **GitHub(Auiowu) 源已废弃**（v1.3.x）：上游 `SteamAutoCracks/ManifestHub` 自 2025-07-24 停更，
  实测 CS2(730) / Palworld(1623730) / Deadlock(892970) / 黑神话(2358720) 四分支全 404，
  社区生态已迁到 MHub API（Hubcap / ManifestHub3 / SteaMidra 等都不再走 GitHub 分支）。
  `DownloadFromGithubAsync`、`GetSourceApiKey` / `GetSourceBaseUrl`、`GitHub` / `GitHubSearch` 枚举值、
  `CustomGithubRepos` / `CustomZipUrls` / `CustomManifestSources` 字段、`AppConfig.GithubToken` 均已删除，
  另清掉 5 个从未接入的源预设
- 入库勾选「下载 Manifest」（默认开）：MHub → Sudama(仅密钥) 级联；不勾则跳过清单直接生成 Lua，
  由内核运行时兜底取清单（成功提示会注明兜底）
- `LuaBuilder` 顺序：Sudama 密钥/令牌 → `MergeAllDepotsAsync` 用**全量 depot 列表**补全（防止只写有 manifest 的 depot 漏密钥）
  → 缺密钥收集并通知；DLC 段对每个 DLC AppID 查 `keys[dlcId]`，
  命中的独立 DLC depot key 写进 `addappid(dlcId, 1, "<key>")`。
  缺解密密钥警告在两种模式下都保留（无 key 无法解密已加密内容）
- **取消是"立即"的**：`ct` 贯穿所有会等的环节，唯一不打断的是两处原子写（`tmp + Move`，亚秒 / 毫秒级）
  → 永不留下半个 `.lua` 或半份 manifest；取消后不兜底第二个源、不弹通知
- 卡片形态（尺寸 / 图标 / 文案 / 按钮）见 `doc/细节与偏好.md`；清单投喂两处 depotcache 见 `REF-清单与版本.md`
- ⚠️ **取消的两条纪律**（2026-09-22 复核后补，都是上轮踩出来的）：
  ① `catch (OperationCanceledException)` **必须带 `when (ct.IsCancellationRequested)` 过滤** ——
  `HttpClient` 的**超时抛的也是 `TaskCanceledException`（OCE）**，不过滤就会把一次网络超时当成"用户取消"透传，
  白白吃掉原有的重试与兜底（`GetGameDetailsFromSteamCmdAsync` 的 3 次重试、`DownloadJsonAsync` 的重试 + 过期缓存兜底、
  逐 depot 下载的失败记账）；② **每个网络 / 等待点都要真的把 ct 传下去**——
  最容易漏的是链路开头那两处取 depot 信息（漏了等于"点完立刻取消"这最常见场景仍不可取消）。
  入库期间禁止再起第二个任务（`IsAdding` 守卫）
- **DLC 名数据源**：steamcmd 对 DLC 返回空壳（无 `name`），必须回退官方 `appdetails` 取名；官方在本机可达但约 8s 截断

## Sudama 密钥缓存（GUI 侧实现事实）

- 文件：`%LOCALAPPDATA%\OSTGUI\sudama_cache.json`（约 22 万条 / 16.5MB）、`token_cache.json`；
  **存在即用、不自动过期**（已去掉 24h TTL）——仅无缓存文件时才自动下载；
  新密钥 / 令牌靠设置页**手动刷新**或**手动导入本地文件**（按文件名 / 内容自动识别类型）取得
- 下载策略：密钥与令牌并行、流式接收、单次超时 max(120, 设置值)、重试间隔 1.5s、成功日志带条数/体积/耗时；
  **Sudama 没有按需查询接口**，只有全量端点 → 勿每次入库实时拉全量
- **入库取键走流式扫描**（`SudamaKeyCache.ScanWantedAsync`）：64KB 分块喂 `Utf8JsonReader`、
  跨块靠 `CurrentState` 续读，只收 `wantedIds` 命中的键值对；调用方（`LuaBuilder`）**先算出要哪些 id**
  （appId + 各 depot + 各 DLC），所以 DLC 列表的获取被提到写行之前。
  三种做法的实测内存对比与"峰值后压大对象堆"见 `doc/开发踩坑-环境.md`
- ⚠️ **截断保护**：流式读不完整 JSON 时 `Read()` 可能只是返回 false（不抛），会把截断缓存静默当成"少了那些键"
  → 入库静默少密钥。做法：先校验**尾部最后一个非空白字节必须是 `}`**，不合格就抛 `JsonException` 让调用方降级（重下 / 旧缓存 / 空）
- **缓存文件形状兼容两种**：本程序写的是 `{"Data":{…}}`，读侧也认原始明文 `{…}`
  （扫描只认"深度 ≤ 2 的键值对"，不关心套在哪一层）
- 隐藏调优参数 `DownloadTimeout`（config.json，默认 120，无 UI）：同时影响清单文件下载（max(60,·)）
  与 Sudama 缓存下载（max(120,·)）的超时；早期有设置控件，08-14 起移除仅留字段
- 待做（A2）：冷缓存下载仍是"MemoryStream → 字典 → 再序列化 17.5MB 落盘"三次物化（只在首次 / 刷新时走），
  可改流式写 `.tmp` + `Move`；要动缓存文件形状且只能靠真实冷缓存验证，暂缓
