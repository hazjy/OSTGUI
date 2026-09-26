# REF-授权与Denuvo（GUI 侧）

> 只收 Denuvo 授权（.ost 导入导出）与 D 加密模式读写；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26，2026-09-26 按领域重划）。

## Denuvo / 授权（查证）

- 需 **AppTicket + ETicket 双票**，不可本地伪造；ETicket 由 Steam 实时签发，**30 分钟有效**（报错 88500005）
- 提取授权存在"本地缓存的过时授权"假象且无法程序化验证账号正伪（ETicket 请求会被内核拦截）
  → 采用导出时弹窗提醒策略
- `.ost` 为明文 JSON：AppTicket / ETicket / Source(Steam 用户名) / CreatedAt / ExpiresAt / UseCount / ExporterVersion
- 导入写注册表（`HKCU\Software\Valve\Steam\Apps\<appid>` 的 `AppTicket` / `ETicket`；**GUI 不写 `SteamID`**），
  本机任意 Steam 账号可用；部分游戏 DLC 也受 D 加密，只带主游戏票时 DLC 可能解锁失败
- 每账号每天最多 5 台新机器激活；已激活机器不消耗
- 与 .cw/.shiki（流畅入库私有格式）不兼容是刻意选择
- **身份模式（`[denuvo] mode`）的两种语义、切换后果与协议层边界**：内核侧事实见工作区根 `doc/` 下的事实考证（内核侧）；
  GUI 侧读写实现见本文件 §D 加密模式读写

## D 加密模式读写（GUI 侧事实，2026-09-13）

- **唯一数据源** = 内核配置文件 `<Steam 目录>\opensteamtool.toml` 的 `[denuvo] mode`
  （**不是** GUI 自己的 `%LOCALAPPDATA%\OSTGUI\config.json`）
- 读写实现：`SteamDllService.GetConfigPath()/GetDenuvoMode()/SetDenuvoMode()`；
  写入**保留注释只改 mode 行**（段/键缺失自动追加、行内注释可解析、CRLF 保持、非法值拒绝）
- 生效方式：内核 `ConfigFileWatcher` **整文件变更即热重载** → 改完无需重启 Steam；
  进设置页会从文件回读（外部手改可见）
- 两模式语义与后果见工作区根 `doc/` 下的事实考证（内核侧）
