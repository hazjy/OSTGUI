# REF-清单与版本（GUI 侧）

> 只收 depot·manifest·gid·key 关系 / 清单获取与投喂 / 固定版本体系；分工与文档地图见工作区根 `README.md`。

## depot / manifest / gid / key 关系（查证）
- **depot**：游戏内容分区（本体 / DLC / 语言包各一），ID 固定不随版本变。
- **manifest**：某 depot 某版本的文件清单；"下哪些 depot"由 appinfo 决定，manifest 管"下哪些文件块"。
- **gid**：manifest ID，代表某次内容快照，每次更新换新；完整版本 = 全部 depot 的 gid 组合。
- **key**：AES-256 密钥 per-depot 且对所有用户相同 → 第三方密钥库可行的根本原因。
- 有内容清单（manifests）的 depot 内容均为 AES-256 加密，下载必须有 key；也存在**无 manifests 的纯所有权壳 depot**（常见于 DLC 占位，SteamCMD 数据中连 manifests 字段都没有），本就没有可解密内容，裸 `addappid(depotId)` 即为正确写法，不应计入"缺密钥"警告。

### manifest 获取门槛（查证）
- 两步：向 CM 发 `GetManifestRequestCode`（需登录且**拥有该 depot**，参数须匹配当前 appinfo）→ 拿 code 向 CDN 拉 `depot/{id}/manifest/{gid}/5/{code}`；code 约 5 分钟轮换、CDN 侧约 10 分钟有效、**不绑定请求者**。
- **2026-09-09 起 CDN 校验清单归属**：只能拉属于该 depot 的清单 → **必须拥有目标 depot 的账号才取得到码**，这就是"离线号"（大批拥有账号批量取码供社区复用）的来由。
- 第三方清单库本质：有人用拥有权限的正版账号批量抓取共享；OST 内核拦截 code 请求伪造响应喂回客户端，manifest 本体仍从 Steam CDN 直连。

### 直接改清单 ID 不能锁版本（查证）
- 改 depotcache 文件：只是缓存，不触发更新决策。
- 改 `.acf` gid 为旧值：Steam 判定落后反而强制更到最新；社区做法是改成最新值骗过防更新，不是锁旧版。
- 锁版本唯一正道：内核 hook（见本文件 §固定版本体系对应实现）。

## 清单投喂位置：两级 depotcache（查证）
- `config\depotcache` = **持久层**（可长期留存）；Steam 根 `depotcache` = **易失工作层**。
- Steam 客户端只读**根** depotcache；根目录清单会在**卸载 / 回滚时被清理**（实测：一次失败自动卸载、一次手动卸载，两次都清根）。
- 因此投喂清单必须**同时写两处**（OSTGUI `ManifestFileService` 即双写 + 逐份容错），否则会出现"config 有、根没有"的静默半成品（表现为下载报 "No connection"）。
- 清单文件名格式：`<depotId>_<gid>.manifest`。
- **从 CDN 直抓的清单是 zip 外层，不能直接投喂**：CDN 端点 `depot/<id>/manifest/<gid>/5/<code>` 返回 zip（magic `PK`），内含单条目 `z`，**解出的内层**才是 depotcache 要的格式——magic `d0 17 f6 71`（Steam 的 manifest magic `0x71F617D0`），现存 depotcache 里所有清单都是这个 magic。
- 直接投入 zip 的后果：Steam 记一行 `WARNING! Deleted corrupt manifest file (parsing_failed)` 并删掉它，然后照旧去要请求码 → 看起来像"投喂完全没生效"。
- MHub / Sudama 给的本来就是内层格式，故 GUI 投喂链路不受影响；只有自行走 CDN 抓取时才要解包（例：`2001761_5463549948452951231` zip 1,190,689 B → 内层 1,554,210 B）。
- **多源级联 + 落盘前校验（2026-10-24）**：单份清单由 `ManifestDownloadService.DownloadOneManifestAsync` 按 `ManifestSource.Priority` 逐家尝试（当前 MHub → Ryuu），**每家下完先校验头 4 字节 `d0 17 f6 71`**，不是内层的（HTML 错误页 / zip 外壳）就丢掉换下一家；全部失败才算这份没拿到。入库与「清单按需投喂」共用这一条路径，所以两边行为天然一致。各源接入状态见 `SOURCES.md`（唯一权威）。

## 固定版本体系对应实现（GUI 侧事实）
- `setManifestid` → `ManifestOverrides` → 内核 `BuildDepotDependency` 直接 patch depot 条目的 gid；同时双向拦截 `GetManifestRequestCode`（出站换 code / 入站伪造 OK），实测可回退旧版。
- 内核不写 `.acf` / depotcache，全部内存 hook；`pinApp` 已弃用。
- 入库勾选"写入固定版本配置" = 预写**注释形式** `--setManifestid(...)`，默认仍是自动更新；库页切换锁定模式按文件实际内容判断（有未注释 `setManifestid` → 注释掉变 auto；无 → 需存在注释形式才取消注释）。
- **覆盖检查**：auto→fixed 要求每个 depot 都有 `setManifestid` 覆盖，缺则拒绝并列出缺失项；「补齐版本配置」（`RepairVersionConfigAsync`）取全部 depot 的当前 GID 写进注释块（即始终锁到当前最新版）。
- **不做"版本切换器"（2026-10-25 决定，曾实现后回退）**：`depots.branches` 只能看到开发者**公开**的分支，而那些分支本来就是给玩家在 Steam「属性 → Betas」里切的，自己做等于重做官方 UI；只有 `public` 的游戏（艾尔登法环 / 黑神话 / 幻兽帕鲁）根本没有第二个版本可选。非拥有者即便锁了旧 gid，也要第三方源恰好收录该 gid 才有清单，且请求码链只有 `20770407` / `manifestdex` 对旧 gid 有效（`wudrm` / `steamrun` 实测 502）。结论：**回旧版走官方 Betas；要手动锁特定 gid 直接编辑 lua**，不为它做界面。
