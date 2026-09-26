# REF-入库与密钥（GUI 侧）

> 只收 Lua 配置语义 / depot-manifest-gid-key / 清单投喂 / 搜索入库 / Sudama / 创意工坊；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26）。

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

## depot / manifest / gid / key 关系（查证）

- **depot**：游戏内容分区（本体/DLC/语言包各一），ID 固定不随版本变
- **manifest**：某 depot 某版本的文件清单；"下哪些 depot"由 appinfo 决定，manifest 管"下哪些文件块"
- **gid**：manifest ID，代表某次内容快照，每次更新换新；完整版本 = 全部 depot 的 gid 组合
- **key**：AES-256 密钥 per-depot 且对所有用户相同 → 第三方密钥库可行的根本原因
- 有内容清单（manifests）的 depot 内容均为 AES-256 加密，下载必须有 key；
  也存在**无 manifests 的纯所有权壳 depot**（常见于 DLC 占位，SteamCMD 数据中连 manifests 字段都没有），
  本就没有可解密内容，裸 `addappid(depotId)` 即为正确写法，不应计入"缺密钥"警告

### manifest 获取门槛（查证）

- 两步：向 CM 发 `GetManifestRequestCode`（需登录且**拥有该 depot**，参数须匹配当前 appinfo）→
  拿 code 向 CDN 拉 `depot/{id}/manifest/{gid}/5/{code}`；code 约 5 分钟轮换、CDN 侧约 10 分钟有效、**不绑定请求者**
- **9/9 前的真实漏洞（用户补充 2026-09-20）**：拿到码后 CDN 只校验"你是否拥有这个 depot"，
  **不校验请求的 gid 清单是否属于这个 depot** → 一个合法 depot 的码可以拉任意清单，
  第三方清单库因此能以极低成本攒齐全网清单（也解释了当时"非当前/老版本清单也能下"）——
  **2026-09-09 起该绑定校验上线**：只能拉属于该 depot 的清单 → **必须拥有目标 depot 的账号才取得到码**，
  这就是"离线号"（大批拥有账号批量取码供社区复用）的来由
- 第三方清单库本质：有人用拥有权限的正版账号批量抓取共享；OST 内核拦截 code 请求伪造响应喂回客户端，manifest 本体仍从 Steam CDN 直连
- **该机制的历史变迁、9/9 服务器收口侦查、第三方码源失效与替代路线，见 `doc/EVENTS/`**（不在此重复）

### 直接改清单 ID 不能锁版本（查证）

- 改 depotcache 文件：只是缓存，不触发更新决策
- 改 `.acf` gid 为旧值：Steam 判定落后反而强制更到最新；社区做法是改成最新值骗过防更新，不是锁旧版
- 锁版本唯一正道：内核 hook（见 `REF-版本锁定与Denuvo模式.md` §固定版本体系对应实现）

## 清单投喂位置：两级 depotcache（查证）

- `config\depotcache` = **持久层**（可长期留存）；Steam 根 `depotcache` = **易失工作层**
- Steam 客户端只读**根** depotcache；根目录清单会在**卸载/回滚时被清理**（实测：一次失败自动卸载、一次手动卸载，两次都清根）
- 因此投喂清单必须**同时写两处**（OSTGUI `ManifestFileService` 即双写 + 逐份容错），
  否则会出现"config 有、根没有"的静默半成品（表现为下载报 "No connection"）
- 清单文件名格式：`<depotId>_<gid>.manifest`

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
- 卡片形态（尺寸 / 图标 / 文案 / 按钮）见 `doc/细节与偏好.md`；清单投喂两处 depotcache 见本文件「清单投喂位置」
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
