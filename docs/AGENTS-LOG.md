# 智能体变更台账（AGENTS-LOG）

> 用途：多智能体/多人协作的共享更改记录。每个智能体完成一次可交付改动后，
> 必须在下方「变更记录」追加条目，并与代码**同一 commit** 提交，保证台账可追溯。
> 流水细节以 `git log` 为准，本台账是"解读层"：谁、何时、改了什么、为什么。

## 填写规范

- 时间：本地时间 `YYYY-MM-DD HH:mm`（与项目日志一致，UTC+8）
- 智能体：任意助记标识（工具/角色/名称，如 `agent-a`、`claude-code`、`human`、`dsh`）
- 类型：`feat` / `fix` / `refactor` / `build` / `docs` / `chore` / `ui` / `revert`
- 内容：一句话摘要 + 关键决策；涉及文件；关联 commit；验证状态
- 追加位置：最新条目放「变更记录」顶部；同一次改动只记一条；**不改动历史条目**（改错了用新条目修正）

## 协作约定

1. 每次改动完成后：先在台账顶部追加条目，再与代码**同一 commit** 提交
2. commit message 保持现有前缀规范（feat:/fix:/refactor:/build:/docs:/chore:/ui:/revert:）
3. 修改共享构建/目录约定（build.bat、.build、NoSteamLauncher 资源、ZTool、docs/DEV-NOTES.md）前，先读本台账与该文件的现状
4. 重大领域结论与踩坑沉淀进 `docs/DEV-NOTES.md`；发布说明写 `docs/UPDATE-NOTES-*.md`；本台账只记"发生了"
5. 并行开发优先独立分支；台账必须在合并回主干前补齐

## 当前基线

- 版本：v1.3.2
- 分支：`feat/online-compat-env-launch`（台账创建时 HEAD）
- HEAD：`5a178e6` — docs: 内核开发驻地迁移至 D:/Projects/OSTGUI/ZTool
- 构建入口：`build.bat`（Debug 自包含）；目录与产物约定以最新 commit 为准（v1.3.2 后已迁移仓库级 `.build`）
- 说明：台账创建前（基线 `ed5b81a` 至 `5a178e6`）的历史条目由 git log 回填；除本会话外智能体标识不可考，统一标「历史」

## 变更记录

### 2026-08-26

| 时间 | 智能体 | 类型 | 变更内容 | 涉及 | commit |
|---|---|---|---|---|---|
| 21:44 | 历史 | docs | 内核开发驻地自 RefProjects 迁出至 `D:/Projects/OSTGUI/ZTool` | 目录结构 | 5a178e6 |
| 21:40 | 历史 | docs | DEV-NOTES §14 实证轮结论：真凶为叠加层身份还原致邀请对话框降级，v3 撤销并加回 Persona 改写 | DEV-NOTES | eafa807 |

### 2026-08-25 晚（联机与构建体系，智能体标识不可考）

| 时间 | 智能体 | 类型 | 变更内容 | 涉及 | commit |
|---|---|---|---|---|---|
| 21:28 | 历史 | docs | DEV-NOTES §14 更新：内核邀请补丁已实施部署，附验证清单与回滚路径 | DEV-NOTES | bd63776 |
| 20:32 | 历史 | build | 构建产物迁移到仓库级 `.build` 目录，构建脚本确定性恢复 | build.bat, .build | f67e4b6 |
| 20:15 | 历史 | fix | build.bat 兜底嵌套目录产物——先镜像回规范路径再清理 | build.bat | 841b029 |
| 08:19 | 历史 | revert | 移除 480 联机兼容模式（环境变量直启），结论记入 DEV-NOTES §14 | 联机 | 90c4c55 |
| 08:04 | 历史 | ui | 联机页精简：去模式后缀与提示小字，主程序位置并入下拉框 | 联机页 | f9ac091 |
| 07:48 | 历史 | fix | build.bat 先验证主产物再清理嵌套目录，避免误删唯一副本 | build.bat | fe9fa07 |
| 07:37 | 历史 | fix | 联机页打不开——RadioButton 初始化触发 Checked 时 VM 尚未赋值 | 联机页 | 1a15b13 |
| 07:31 | 历史 | feat | 480 联机新增兼容模式（环境变量直启，全一致 480 世界） | 联机 | 4d49728 |

### 2026-08-25 凌晨（创意工坊修复与工程整理，本会话）

| 时间 | 智能体 | 类型 | 变更内容 | 涉及 | commit |
|---|---|---|---|---|---|
| 05:08 | dsh | chore | 版本号升至 1.3.2，新增 UPDATE-NOTES-1.3.2；Release 自包含打包并验证 | csproj, docs, 发布包 | 6c5ad37 |
| 05:04 | dsh | fix | 日志链路跨线程加固：Clear 封送 + 设置页 Loaded 兜底同步 | LogService, SettingsPage | d1a0174 |
| 04:59 | dsh | fix | 设置页日志栏打开无新日志则空白——构造时初始填充 LogsText | SettingsPage | 81336c7 |
| 04:54 | dsh | chore | 删除死代码 LuaConfigService.GenerateLuaContent（无调用方） | LuaConfigService | 001fda8 |
| 04:20 | dsh | refactor | 主项目目录 OSTGUI 更名 main，同步 sln/build.bat/文档全部路径引用 | 目录结构 | fed17ce |
| 04:10 | dsh | build | 构建产物固定 main\bin，构建后自动清理 WindowsAppSDK 冗余嵌套副本 | build.bat | b5ba9c9 |
| 03:47 | dsh | feat | 入库主游戏行自动附带创意工坊密钥（Phase 1），修复订阅创意工坊下载"内容仍处于加密" | LuaBuilder, DEV-NOTES | de95cf0 |
| 03:46 | dsh | chore | 创意工坊 Phase1 改动前基线提交（含 1.3.1 更新说明残留改动） | 仓库 | ed5b81a |