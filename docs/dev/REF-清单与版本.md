# REF-清单与版本（GUI 侧）

> 何时读：动入库链路的 depot / manifest / 固定版本时。
> 管什么：depot·manifest·gid·key 的关系、取码门槛、固定版本体系与实现。
> 不写什么：清单源接入、投喂位置与级联校验见 `SOURCES.md`；入库链路见 `DEV-NOTES.md` §2。

## depot / manifest / gid / key

- **depot**：内容分区（本体 / DLC / 语言包各一），ID 不随版本变。
- **manifest**：某 depot 某版本的文件清单；下哪些 depot 由 appinfo 决定，manifest 决定下哪些文件块。
- **gid**：manifest ID，代表一次内容快照，每次更新换新；完整版本 = 全部 depot 的 gid 组合。
- **key**：per-depot 的 AES-256 密钥，对所有用户相同，这是第三方密钥库可行的原因。
- 无 manifests 的纯所有权壳 depot 不需要 key，写法见 `REF-入库与Lua.md`。

## 取码门槛

- 两步：向 CM 发 `GetManifestRequestCode`（要求登录且拥有该 depot，参数须匹配当前 appinfo），拿到 code 再到 CDN 拉 `depot/{id}/manifest/{gid}/5/{code}`；code 约 5 分钟轮换，有效期实测 ≥20 分钟，不绑定请求者（2026-10-06 实测，见 `SOURCES.md` §请求码源）。
- 2026-09-09 起 CDN 校验清单归属：只能拉属于该 depot 的清单，所以必须用拥有该 depot 的账号取码（「离线号」批量取码的由来）。
- 已接入的码源、判活方式与内核注入点见 `SOURCES.md`；manifest 本体仍从 Steam CDN 直连。

## 不能锁版本的三条路

- 改 depotcache 文件：只是缓存，不参与更新决策。
- 改 `.acf` 的 gid 为旧值：Steam 判定落后会强制更到最新；社区改成最新值是骗过防更新，不是锁旧版。
- 改 `.acf` 的 `StateFlags`：它是状态位不是版本输入（`2`=UpdateRequired、`4`=FullyInstalled、`32`=FilesMissing、`2048`=Uninstalling），由 `buildid` 与 `InstalledDepots/*/manifest` 跟服务器 PICS 比对后写出；强改一次比对即回滚，还可能被判 `FilesCorrupt` 触发重下。本机对照：`6` = 已安装+需更新，`2086` = 需更新+已安装+文件缺失+卸载中。
- 锁版本的唯一做法是内核 hook，见下节。

## 固定版本体系（GUI 侧实现）

- `setManifestid` → `ManifestOverrides` → 内核 `BuildDepotDependency` 直接 patch depot 条目的 gid；同时双向拦截 `GetManifestRequestCode`（出站换 code、入站伪造 OK），实测可回退旧版。
- 内核不写 `.acf` / depotcache，全部内存 hook；`pinApp` 已弃用。
- 入库勾选「写入固定版本配置」= 预写注释形式 `--setManifestid(...)`，默认仍是自动更新；库页按文件实际内容判断锁定模式（有未注释 `setManifestid` 则注释掉变 auto；否则需要存在注释形式才取消注释）。
- 覆盖检查：auto→fixed 要求每个 depot 都有 `setManifestid`，缺则拒绝并列出缺失项；「补齐版本配置」`RepairVersionConfigAsync` 取全部 depot 当前 GID 写进注释块。
- 不做版本切换器（2026-10-25 决定，曾实现后回退）：`depots.branches` 只有开发者公开的分支，等于重做官方 Betas；角色只有 `public` 的游戏没有第二版可选；非拥有者锁旧 gid 还需要第三方源收录该 gid，且旧 gid 只有 `20770407` / `manifestdex` 有效。回旧版走官方 Betas，要锁特定 gid 直接编辑 lua。
