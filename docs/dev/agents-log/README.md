# 智能体变更台账（AGENTS-LOG）

多智能体协作的共享变更记录。**具体记录按日期分文件存放**（见下方"索引"），本文件仅为指引。

## 记录范围（当前约定）

- 当前**仅记录 `dsh` 智能体**（本开发会话）对仓库的变更
- 后续**内核开发任务（ZTool 等）暂不记录**（不纳入台账）
- 将来扩展现有约定时，在本节补充即可

## 文件命名与结构

- 每个**变更发生日**一个记录文件：`YYYY-MM-DD.md`（如 `2026-08-25.md`）
- 同一天的多条变更追加进当天文件，按时间先后排列（新的在下/按 commit 时间）
- 目录：`docs/dev/agents-log/`
- 事实源：`git log`；本台账是"谁、何时、改了什么"的解读层

## 填写规范

- 时间：本地时间 `HH:mm`（UTC+8，与 commit 时间一致）
- 类型：`feat` / `fix` / `refactor` / `build` / `docs` / `chore` / `ui` / `revert`
- 条款：一句话摘要 + 关键决策；涉及文件；关联 commit；验证状态
- 规则：与代码**同一 commit** 提交；不改动已写条目（改错用新条目修正）

### 变更记录表模板

```markdown
### YYYY-MM-DD

| 时间 | 类型 | 变更内容 | 涉及 | commit |
|---|---|---|---|---|
| HH:mm | feat | ... | ... | abc1234 |
```

## 协作约定

1. 每次改动完成后：在当天记录文件追加条目，再与代码**同一 commit** 提交
2. commit message 保持前缀规范（feat:/fix:/refactor:/build:/docs:/chore:/ui:/revert:）
3. 修改共享构建/目录约定（build.bat、.build、NoSteamLauncher 资源、ZTool、docs/dev/DEV-NOTES.md）前，先读本台账对应日期文件与该文件现状
4. 重大领域结论与踩坑沉淀进 `docs/dev/DEV-NOTES.md`；发布说明写 `docs/changelog/UPDATE-NOTES-*.md`；本台账只记"发生了"

## 当前基线

- 版本：v1.3.3
- 分支（台账建立时）：`feat/online-compat-env-launch`（本次重构提交后，以 `git log -1` 为准）
- 构建入口：`build.bat`（Debug 自包含）；目录与产物约定以最新 commit 为准（v1.3.2 后已迁移仓库级 `.build`）

## 索引

| 文件 | 内容概要 |
|---|---|
| `2026-08-25.md` | 创意工坊密钥入库（Phase 1）、构建产物整理、目录更名 main、死代码清理、日志显示修复、版本 v1.3.2 发布 |
| `2026-08-27.md` | 本台账体系创建 |
| `2026-08-28.md` | Sudama 无自动过期刷新；名称缓存重构（纯缓存读+后台补名+主游戏限定+联机解耦）；DLC 名单实时化与数据源实证；Token 上次更新时间；build.bat 产物同步修复 |
| `2026-09-02.md` | Sudama 缓存策略文档修正（README/DEV-NOTES/孤儿注释清理）；480 联机上游调研与实测定论（BOXHEAD:Immortal 阳性对照、PEAK 纯好友邀请死路、ZTool v3 邀请修复投入暂缓） |