# REF-缺陷与归档（GUI 侧）

> 只收 已知实现缺陷与作废结论（别走这条路的清单）；机制正文见同目录其它 REF；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26）。

## 已知实现缺陷（GUI 侧，未修）

- `addtoken` 键错配（appid vs packageid）：`main/Services/LuaBuilder.cs:111,144` 按 appid/dlcId 查令牌库；
  本机实测 `token_cache.json` 8421 键中 4912 个 ≥7 位、3494 个 5–6 位，实际命中率低但**非零**
  （现场 39 个 lua 里 4 个有 addtoken）。键空间混杂，改法需先定性主键语义
- 文档失效引用：`docs/dev/SOURCES.md:3` 仍写"以 `main/Services/ManifestService.cs` 为准"，
  该文件不存在（实为 `ManifestDownloadService.cs` + `ManifestFileService.cs`）
- 两条 lua 写入路径不一致：`LuaConfigService.cs:139-169` vs `LuaBuilder.cs:194-216`
  ——前者顺带往 `steamtools.lua` 追加 addappid，后者不会
- 免 Steam 部署备份覆盖：`NoSteamLaunchOrchestrator.cs:58-65` 每次部署覆盖 `.bak`，
  重复部署会把已脱壳 exe 当作"原始备份"

## 作废结论与"别走这条路"

- **别走这条路**：AppID Changer 第一版"只写文件、不设环境变量、不加载垫片" → 实测不生效，
  关键在**进程环境**而非文件（正文见 `REF-联机.md`）。
- **别走这条路**：手写 vtable 调 `ISteamUserStats013` → 偏移 1 探针命中 `GetAchievementName(uint32)` 却未给参，
  必然 `0xC0000005`（原生越界、catch 拦不住），照搬 SAM.API（正文见 `REF-成就.md`）。
- **别走这条路**：页面入场用 `EntranceThemeTransition`（先到位再跳回），或用 `Frame.Content` 绕过导航过渡（用户要求）
  ——该条已记在 `doc/开发踩坑-UI.md`，此处不重复。
- **别走这条路**：`addappid` 第二参数（正文见 `REF-入库与密钥.md`）。
- **别走这条路**：改 depotcache 文件、改 `.acf` gid 来锁旧版本（正文见 `REF-入库与密钥.md`）。
- **已删并更名的备份目录**：原 `ost-backups/` → 工作区 `内核备份/`（2026-09-20 清理并更名）。
- 与 .cw/.shiki（流畅入库私有格式）不兼容是刻意选择（正文见 `REF-免Steam部署.md`）。
