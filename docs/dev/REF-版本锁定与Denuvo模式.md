# REF-版本锁定与Denuvo模式（GUI 侧）

> 只收 固定版本体系 / Denuvo 模式读写；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26）。

## 固定版本体系对应实现（GUI 侧事实）

- `setManifestid` → `ManifestOverrides` → 内核 `BuildDepotDependency` 直接 patch depot 条目的 gid；
  同时双向拦截 `GetManifestRequestCode`（出站换 code / 入站伪造 OK），实测可回退旧版
- 内核不写 `.acf` / depotcache，全部内存 hook；`pinApp` 已弃用
- 入库勾选"写入固定版本配置" = 预写**注释形式** `--setManifestid(...)`，默认仍是自动更新；
  库页切换锁定模式按文件实际内容判断（有未注释 `setManifestid` → 注释掉变 auto；无 → 需存在注释形式才取消注释）
- **覆盖检查**：auto→fixed 要求每个 depot 都有 `setManifestid` 覆盖，缺则拒绝并列出缺失项；
  「补齐版本配置」（`RepairVersionConfigAsync`）取全部 depot 的当前 GID 写进注释块（即始终锁到当前最新版）

## D 加密模式读写（GUI 侧事实，2026-09-13）

- **唯一数据源** = 内核配置文件 `<Steam 目录>\opensteamtool.toml` 的 `[denuvo] mode`
  （**不是** GUI 自己的 `%LOCALAPPDATA%\OSTGUI\config.json`）
- 读写实现：`SteamDllService.GetConfigPath()/GetDenuvoMode()/SetDenuvoMode()`；
  写入**保留注释只改 mode 行**（段/键缺失自动追加、行内注释可解析、CRLF 保持、非法值拒绝）
- 生效方式：内核 `ConfigFileWatcher` **整文件变更即热重载** → 改完无需重启 Steam；
  进设置页会从文件回读（外部手改可见）
- 两模式语义与后果见工作区根 `doc/` 下的事实考证（内核侧）
