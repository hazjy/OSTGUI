# REF-缺陷台账（GUI 侧）

> 只收**尚未修复**的已知实现缺陷。作废结论与"别走这条路"一律归各自领域文档，此处不复述。分工与文档地图见工作区根 `README.md`。

## 未修

- **`addtoken` 按 appid 查表，但缓存键空间混杂**：`LuaBuilder` 生成 addtoken 时用 DLC 的 appid / dlcId 去 `token_cache.json` 查，
  而该缓存的键既有 7 位以上的 appid、也有 5–6 位的 packageid，命中率低（非零）。要改得先定性主键语义。
- **两条 lua 写入路径行为不一致**：`LuaConfigService` 写单个游戏 lua 时会顺带把 `addappid(<appId>)` 追加进 `steamtools.lua`，
  `LuaBuilder` 不会——同一件事走不同入口，结果不同。
- **免 Steam 部署的 EXE 备份无条件覆盖**：`NoSteamLauncher/Services/NoSteamLaunchOrchestrator.cs` 每次部署都以
  `overwrite: true` 覆盖 `.bak`，重复部署会把已脱壳的 exe 当成"原始备份"。同工程的 DLL 备份有"已存在则跳过"的保护，EXE 这条没有。
