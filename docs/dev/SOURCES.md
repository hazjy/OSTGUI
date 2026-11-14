# 清单源与请求码源状态

> 何时读：查清单源 / 请求码源 / 密钥与令牌缓存的接入状态，或改投喂与级联顺序时。
> 管什么：清单源 / 请求码源 / 密钥与令牌缓存的唯一权威（含投喂位置）。
> 不写什么：清单版本体系见 `REF-清单与版本.md`；入库与 Lua 生成见 `REF-入库与Lua.md`；本文件只留状态与契约，实现见 `main/Services/ManifestDownloadService.cs`、`ManifestFileService.cs`，内核消费见 `ZSteamTool/src/Utils/SteamMetadata/ManifestClient.cpp`。

| Id | 名称 | 需要 Key/Token | 状态 | 说明 |
|---|---|---|---|---|
| `mhub` | MHub | ✅ API Key（24h 过期） | ✅ 已接入 | 入库首选；按 depot+gid 取，旧分支 gid 也能取到 |
| `hubcap` | Hubcap | ✅ API Key（免费 25/日整包、1500/日单份） | ✅ 已接入（v1.8.4） | 单份生成：直接让站点去 Steam 取一份，旧分支 gid 也能取 |
| `sudama` | Sudama | 否 | ✅ 已接入 | 只作密钥源：提供 depot 密钥与访问令牌 |
| `ryuu` | Ryuu's Manifests | ✅ auth code | ❌ 已下架（2026-10-31） | 点播腿被站点移除，整包腿按决定不启用，见 §Ryuu |
| `walftech` | Walftech | 否（要 PoW token） | ❌ 未接入 | 只有按 appid 整包，没有 depot+gid 点播 |
| `luatools` | LuaTools（lua.tools） | ✅ Discord 会话 | ❌ 未接入 | 内部 API 有清单与 lua，但只有浏览器会话鉴权，见 §LuaTools |

## 入库流程

- 多源级联，任一成功即完成：MHub（配了 key 时优先）→ Hubcap（单份生成兜底）→ Sudama 兜底（只生成 Lua，不下清单）。整包腿当前没有启用源，见 §Ryuu。
- 参与级联的源与顺序由 `ManifestSource.IsEnabled` + `Priority` + `PerDepotFetch` 决定；预置源的这些字段以代码为准，见工作区根 `doc/开发经验.md` §三。
- 落盘前校验内层 magic `0x71F617D0`，返回 HTML 错误页或 zip 外壳的一律丢弃并换下一个源。
- 最终统一生成完整 Lua（`addappid` + depot key + `addtoken`；勾「固定版本」时预写注释形式 `--setManifestid`），原子写入 `Steam/config/lua/`。MHub 路径另把清单下到 depotcache，Sudama 路径不下。

## 清单投喂位置（两级 depotcache）

- `<Steam>\config\depotcache` 是持久层，可长期留存；`<Steam>\depotcache` 是易失工作层，Steam 客户端只读它，卸载或回滚时会清空（实测两次：失败自动卸载、手动卸载，都清根）。
- 投喂必须两处同时写（`ManifestFileService` 双写 + 逐份容错）；只写 config 会出现「config 有、根没有」的静默半成品，表现为下载报 `No connection`。
- 文件名固定 `<depotId>_<gid>.manifest`。
- CDN 直抓的 `depot/<id>/manifest/<gid>/5/<code>` 返回 zip 外层（magic `PK`），解出的内层才是 depotcache 格式（magic `d0 17 f6 71`）；把 zip 直接投进去，Steam 记一行 `Deleted corrupt manifest file (parsing_failed)` 后删掉，再照旧去要请求码。
- MHub / Sudama 给的已是内层格式；只有自行走 CDN 抓取才需要解包（例：`2001761_5463549948452951231` zip 1,190,689 B → 内层 1,554,210 B）。

## 注意事项

- MHub 域名 `api.manifesthub2.filegear-sg.me`（`manifesthub1` 已不可用），key 24h 有效，设置页填写。错误语义：key 过期 403 `API key has expired`，gid 不存在 404 `{"success":false,"error":"Manifest not found"}`。
- MHub 按 depot+gid 取，旧分支的 gid 也能取到（2026-10-25 实测 CS2 `1.40.8.8`、BG3 `release_patch6_hf9` 均 200 + 内层 magic）；能否取到取决于该 gid 是否被收录。
- 旧版本靠请求码这条路同样可行：同一批旧 gid 用 `20770407` / `manifestdex` 取到有效码（`wudrm` / `steamrun` 回 502），切换版本不必另铺清单链路，级联顺序让这两家排在前面即可。
- GitHub（Auiowu）源已于 v1.3.x 废弃：上游 2025-07-24 停更、实测全 404，生态迁到 MHub API，相关字段、枚举与自定义源一并移除。配置里的残留 entry 由 `ConfigService.MergeDefaults` 每次启动清掉并记日志。

## 密钥与令牌缓存（Sudama）

- 文件：`%LOCALAPPDATA%\OSTGUI\sudama_cache.json`（约 22 万条 / 16.5 MB）与 `token_cache.json`；**存在即用、不自动过期**，只在无缓存文件时才自动下载；新密钥 / 令牌靠设置页手动刷新或手动导入本地文件（按文件名 / 内容识别类型）取得。
- 下载策略：密钥与令牌并行、流式接收、单次超时 max(120, 设置值)、重试间隔 1.5 s、成功日志带条数 / 体积 / 耗时；Sudama 没有按需查询接口，只有全量端点，勿每次入库实时拉全量。
- 入库取键走流式扫描（`SudamaKeyCache.ScanWantedAsync`）：64 KB 分块喂 `Utf8JsonReader`、跨块靠 `CurrentState` 续读，只收 `wantedIds` 命中的键值对；调用方 `LuaBuilder` 先算出要哪些 id（appId + 各 depot + 各 DLC），DLC 列表的获取因此提到写行之前。三种做法的实测内存对比见 `REF-AOT适配.md` §八。
- `token_cache.json` 的键就是 appid（与内核 `addtoken(appId, token)` 同一套 id）：本机 41 个 lua 里 14 条 `addtoken` 的 id 14/14 都在缓存里；缓存只有 8808 条（受限 app 才有令牌），**查不到属正常**，不是缺键。
- **截断保护**：流式读不完整 JSON 时 `Read()` 可能只返回 false（不抛），会把截断缓存静默当成「少了那些键」→ 入库静默少密钥；做法：先校验尾部最后一个非空白字节必须是 `}`，不合格就抛 `JsonException` 让调用方降级（重下 / 旧缓存 / 空）。
- 缓存文件形状兼容两种：本程序写 `{"Data":{…}}`，读侧也认原始明文 `{…}`（扫描只认深度 ≤2 的键值对，不关心套在哪一层）。
- 隐藏调优参数 `DownloadTimeout`（config.json，默认 120，无 UI）：同时影响清单文件下载（max(60,·)）与 Sudama 缓存下载（max(120,·)）的超时。
- 待做（A2）：冷缓存下载仍是「MemoryStream → 字典 → 再序列化 17.5 MB 落盘」三次物化（只在首次 / 刷新时走），可改流式写 `.tmp` + `Move`；要动缓存文件形状且只能靠真实冷缓存验证，暂缓。

## Ryuu's Manifests（已下架，2026-10-31）

- **点播腿**：站点在 10-30 把 `/api/manifest/<depot>_<gid>.manifest` 整条移除，任何配对恒 404 `{"error":"Manifest not found.","status":"not_found"}`（带 key 也一样），官方文档页只剩 `/api/download`、`/requestupdate`、`/request`、`/requestbranch`。
- **整包腿**：10-30 曾接入（`GET /api/download/{appid}?file_type=manifest` 返回 zip，内含 `<appid>.lua` 与 `<depot>_<gid>.manifest`，实测 730 为 4,035,055 B / 17 条目 / 内层 magic 全对）；10-31 按决定**不启用**：清单侧 Hubcap 直接去 Steam 取单份，密钥侧有 Sudama，运行时还有请求码兜底，整包边际收益不足。
- **能力保留**：`ManifestSource.WholeAppFetch` + `ManifestDownloadService.DownloadWholeAppAsync/ExtractManifests` 留在代码里（有自检覆盖），当前无任何源启用；将来只提供整包 zip 的源（Walftech、lua.tools）可直接复用。
- 重开条件与完整实测数据见 `doc/TODO/挂起/261030-Ryuu整包腿接入.md`。
- 历史：10-24 接入点播（查询式）→ 10-27 站点下线查询式、改路径式 → 10-30 路径式也被移除 → 10-31 下架。
## 请求码源（内核取码）

- 两条路：内核 `[manifest] url` 的内置表，或 `<Steam>\config\lua\manifest.lua` 的 Lua 钩子（优先级更高，可多源级联）。
- 验证不依赖 Steam 客户端：直接请求 Steam CDN `/depot/<depot>/manifest/<gid>/5/<code>`，200 + 清单二进制 = 真码，401 = 假码或码与 gid 不配对，404 = gid 不真实。两个 CDN 节点均复现。
- **CDN 只校验「码与 (depot, gid) 配对且在有效期内」**，不判断拥有权（拥有权在 CM 签发侧，见 `REF-清单与版本.md` 的取码门槛）：路径不合法或 gid 不属于该 depot → 404，码不是为该 (depot, gid) 签发 → 401，两条分支不同；无码与错码同走 401。逐条矩阵见 `doc/EVENTS/02-附-请求码验证面实测.md` §二。

| Id | 端点 | 需要 UA | 结论 | 备注 |
|---|---|---|---|---|
| `20770407` | `https://20770407.xyz/manifest/{depot_id}/{gid}` | 否 | ✅ 有效 | 唯一独立号池 |
| `manifestdex` | `https://manifest.manifestdex.com/{gid}` | 必须 `ManifestDeX/1.0` | ✅ 有效 | 60 次/分，超出 429 |
| `wudrm` | `http://gmrc.wudrm.com/manifest/{gid}` | 否 | ✅ 有效 | 与 steamrun 同源同值；对不存在的配对可回 503 `no valid mrc` |
| `steamrun` | `https://manifest.steam.run/api/manifest/{gid}` | 否 | ✅ 有效 | 返回 JSON `{"content":"<码>"}`；对不存在的配对可回 502 |
| `opensteamtool` | `https://manifest.opensteamtool.com/{gid}` | 否 | ❌ 403 | 内核默认源，被服务端 WAF 拦，换 UA 与出口均无效 |

- 码每 5 分钟轮换；同一时刻 manifestdex / wudrm / steamrun 返回同值（一份码源，只算传输层冗余），`20770407` 是另一个池。同一 gid 同一时刻存在多个有效码，采样须一次取多家。
- **码不绑请求者 / 会话 / 出口**：同一码直连与走代理逐个同值，同一时刻多个池的码都能用；有效期内任何人可复用（旧稿「请求码会话绑定」作废）。有效期实测 ≥1,219 s（≈20.3 min，旧记「约 10 分钟」作废），应读作「至少这么久还能拿到清单」，源侧换新值不等于旧码立刻失效。
- **判活边界**：manifestdex 对不存在的 gid 也回 200 + 数字，拿编造的 gid 当健康检查会假阳性；`20770407` 对不存在的配对直接 401，适合当探针。另外三家对查不到的 gid 回一个每次变化的 uint64 兜底值，CDN 上全无效；`20770407` 自己会做 gid→depot 归属判定（同一真实 gid 配别的 depot 一律 401），非法入参回 400（`gid=0` → `Invalid Manifest ID`、非数字 depot → `Invalid Depot ID`）、缺段 → 404。
- **四家码源给的都是真码**（拥有该 depot 的账号替我们签发），没有绕过 CDN 校验；拿到后 1 分钟内验 CDN 全 200。源侧会吐出会 401 的码，**必须级联重试**（失败即换下一个源），不做验活直接注入会出现偶发 `No connection`；取码间隔 ≥1 秒。
- **CDN 偶发假 404 / 401**（实测 3 次）：单次 404 不能判定「码源给了错码」，单次 401 不能判定「归属被拒」，换码或换时刻复跑一次再下结论。
- **接入**：设置页勾选 → `ManifestLuaService` 渲染 `manifest.lua`，顺序即级联顺序；全关写短路版。该文件存在时旁路内核 `[manifest] url`，勾选一变即覆盖、不留备份。
- **manifest.lua 不能有文件级 `local`**：内核逐行累积文本，凑成完整 chunk 就执行并丢弃作用域，`local` 到钩子被调用时已是 nil，结果一次请求都不发、静默回落内置源。函数体内的 `local` 不受影响，生成物统一用 `ostgui_*` 全局前缀；核验必须用同样的逐行加载算法跑真实产物（`OSTGUI.exe --dump-manifest-lua` 导出）。
- **Lua 返回 `"0"` 的口径**：内核把 `code == 0` 当作「这次没拿到码」，不改包、CM 原始应答透传（`Hooks_NetPacket.cpp:652-656`）；`"0"` 被 Lua 成功解析为 0 并返回 true（`LuaConfig.cpp:630-725`），改成 `return "1"` 之类会真的注入假码。
- 「测活」做在 GUI 侧，用真实配对 `481 / 3183503801510301321`：manifest.lua 的失败是静默的，这是唯一的诊断窗口。
- **Hubcap（2026-10-30 拿到 key，已接入）**：`https://hubcapmanifest.com`，鉴权 `api_key` 查询参数 / `Authorization: Bearer` / `X-API-Key` 三选一。
  - 点播腿：`GET /api/v1/generate/manifest?depot_id=&manifest_id=`：站点直接去 Steam 取这一份，返回内层清单（magic `d017f671`）。实测当前 gid 与旧分支 gid 都能取（CS2 `2347770/5023020258185551340` 2,545,792 B、BG3 `1086941/4016443084035836922` 6,368,997 B，与 MHub 字节数一致）。gid 不存在 → 404、缺参数 → 422、key 错 → 401 `Invalid API key format`。
  - 额度是两个独立桶（`/api/v1/generate/usage`）：单份 **1500/日**、整包 100、workshop 500；`/api/v1/user/stats` 另给 `daily_usage/25`（整包与 lua 的下载计数）与 key 过期时间（本次到 2026-11-05）。
  - 整包腿（未接入）：`GET /api/v1/manifest/{appid}` 给 zip、`/api/v1/manifest/{appid}/contents` 免费列出包里的 depot/gid、`/api/v1/lua/{appid}` 给整份 lua（含 depot 密钥与 `setManifestid`）。三者都计入 25/日。
  - 注意：同一对 gid，Hubcap 生成件的字节数未必与 MHub/Ryuu 缓存件完全一致（红色沙漠 3321461 差 10 KB，magic 都对），别拿它当校验基准。
- Walftech 未接入：四步链路（PoW challenge → 本地求 nonce → redeem 拿 90 秒 token → `depotbox_lua.php` 取件）可白嫖，但只有按 appid 整包，没有 depot+gid 与分支参数；请求须带 Referer 与整套浏览器指纹，缺了回 CF 挑战或 403；`/gen_download.php` 的 DB 缓存版 zip 内容实为 `404: Not Found`，只能用 `depotbox_lua.php`。
## LuaTools（lua.tools，2026-10-30 侦察，未接入）

- 站点是 Next.js + Supabase；Discord OAuth 登录后的会话放在浏览器 cookie，**没有面向用户的 API key 入口**（wiki 里那页 "Hubcap API" 讲的是第三方 Hubcap 的 key，与本站无关）。
- 从 `/_next/static/chunks/415-*.js` 挖到的内部 API：
  - `GET /api/manifest/check?appid=` 返回该 app 可用清单源列表（401 = 未登录）；
  - `GET /api/manifest/download?appid=&source=&game_name=` 取清单，**429 = 当日 25 次用尽**（前端文案 `Daily download limit reached (25/day)`）；
  - `GET /api/dlc/info?appid=&base=`、`/api/dlc/generate?appid=&base=&game_name=`；
  - `GET /api/steam/search?q=`（需登录）、`GET /api/steam/details?appid=`（**公开**，实测 730 正常返回名称/类型/基础 app/genre/头图）。
- 未登录访问前四个一律 `401 {"error":"Unauthorized"}`；`/api/me/supporter-status` 公开。
- 要接就得复用浏览器会话：Edge 的 cookie 库被进程独占锁、且可能是 App-Bound（`v20`）加密，程序侧拿不到；可行的是让用户从 DevTools 导出 access/refresh token 再让程序自行刷新，代价与安全面都不小。**暂不接入**（2026-10-30 决定）。
- 唯一现成可用的：`/api/steam/details` 不需要登录，需要游戏名/图标/类型时可用它；与清单链路无关。