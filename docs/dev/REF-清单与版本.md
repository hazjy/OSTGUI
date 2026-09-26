# REF-清单与版本（GUI 侧）

> 只收 depot·manifest·gid·key 关系 / 清单获取与投喂 / 固定版本体系；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26，2026-09-26 按领域重划）。

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
- 锁版本唯一正道：内核 hook（见本文件 §固定版本体系对应实现）

## 清单投喂位置：两级 depotcache（查证）

- `config\depotcache` = **持久层**（可长期留存）；Steam 根 `depotcache` = **易失工作层**
- Steam 客户端只读**根** depotcache；根目录清单会在**卸载/回滚时被清理**（实测：一次失败自动卸载、一次手动卸载，两次都清根）
- 因此投喂清单必须**同时写两处**（OSTGUI `ManifestFileService` 即双写 + 逐份容错），
  否则会出现"config 有、根没有"的静默半成品（表现为下载报 "No connection"）
- 清单文件名格式：`<depotId>_<gid>.manifest`

## 固定版本体系对应实现（GUI 侧事实）

- `setManifestid` → `ManifestOverrides` → 内核 `BuildDepotDependency` 直接 patch depot 条目的 gid；
  同时双向拦截 `GetManifestRequestCode`（出站换 code / 入站伪造 OK），实测可回退旧版
- 内核不写 `.acf` / depotcache，全部内存 hook；`pinApp` 已弃用
- 入库勾选"写入固定版本配置" = 预写**注释形式** `--setManifestid(...)`，默认仍是自动更新；
  库页切换锁定模式按文件实际内容判断（有未注释 `setManifestid` → 注释掉变 auto；无 → 需存在注释形式才取消注释）
- **覆盖检查**：auto→fixed 要求每个 depot 都有 `setManifestid` 覆盖，缺则拒绝并列出缺失项；
  「补齐版本配置」（`RepairVersionConfigAsync`）取全部 depot 的当前 GID 写进注释块（即始终锁到当前最新版）
