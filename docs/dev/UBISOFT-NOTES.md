# 育碧支持（UBISOFT-NOTES）

> 状态：**挂起**。本文只记"要哪些组件 + 大致怎么做"，不构成实施承诺。
> 完整调研过程（三大流派、生态检索、ServerEmus spike 实验、第三方候选全量清单、UNO 实测记录、
> 可复现环境与清理步骤）见归档稿：工作区 `_archive/20261001-育碧授权调研笔记.md`。

## 1. 组件（全部是公开开源项目，不自研模拟器）

| 层 | 组件 | 作用 | 上游 |
|---|---|---|---|
| UC 层（R2） | `upc_r2_loader64.dll` | 接管 UPC 全部应答，免 Ubisoft Connect 客户端 | [Detanup01/Goldberg_r2_extended](https://github.com/Detanup01/Goldberg_r2_extended)（与我们在用的 gbe_fork 同维护者） |
| UC 层（R1 世代） | `uplay_r1_loader64.dll` | 同上，旧世代 | [Mr_Goldberg/goldberg_emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator)；注入式替代 [acidicoala/UplayR1Unlocker](https://github.com/acidicoala/UplayR1Unlocker)（0BSD） |
| D 密层 | `dbdata.dll` | 模拟育碧 Denuvo 令牌接口，消费 `token.ini` | [denuvosanctuary/ubi-dbdata](https://github.com/denuvosanctuary/ubi-dbdata)（Rust，LGPL-3.0）；C# 版 [Detanup01/UplayServer](https://github.com/Detanup01/UplayServer)（MIT，已归档 → [ServerEmus](https://github.com/ServerEmus)） |
| Steam 侧（Steam 版游戏） | `steam_api64.dll` + `steam_settings/` | Steam 所有权与成就模拟 | [Detanup01/gbe_fork](https://github.com/Detanup01/gbe_fork) |

- 组件一律**从上游 Release 取或自行编译**，不取任何第三方工具的打包资源（版本更透明，许可证也好登记）。
- 可选替代铺装方式：[acidicoala/Koaloader](https://github.com/acidicoala/Koaloader) 不替换原文件，按 DLL 搜索顺序劫持注入解锁器。
- loader 三名：`upc_r2_loader64.dll`（Unity 育碧新作）/ `uplay_r2_loader64.dll` / `uplaypc_r2_loader64.dll`。
- 位宽必须与游戏一致；Unity 游戏除根目录还要扫 `*_Data\Plugins\x86_64\`。
- 单 loader 覆盖只覆盖传统 R2 游戏；UNO 那类多组件 Unity 游戏覆盖不到（见 §4）。

## 2. 配置契约（照抄上游，不自己发明字段）

| 文件 | 位置 | 内容 |
|---|---|---|
| `uplay_r2.ini` | 与 loader 同目录 | `[Settings]` 账号/语言/存档 + `[DLC]` 列表。`SaveType`：0=appdata、1=游戏目录、2=`SavePath` |
| `dbdata.ini` | 与 `dbdata.dll` 同目录 | `[settings] dlcs=`；缺该段时回退读 `upc_r2.ini`；同时兼容旧 R1 loader 的 `[Settings] Username/SaveType/SavePath` |
| `token.ini` | 与 `dbdata.dll` 同目录 | `[token] token= / ownership=`，两个不透明 base64，由**用户自己的账号**签发 |
| `token_req.txt` | 首次运行由 `dbdata.dll` 生成于游戏目录 | 含 appid 与请求体，是换 token 的输入；该文件不出现说明 `dbdata.dll` 未被加载 |

DLC 列表要在 `[DLC]` 与 `dbdata.ini` 的 `dlcs=` 两侧保持一致。

## 3. 大致实现方案

沿用现有免 Steam 部署骨架（检测 → 备份 → 替换 → 写配置 → 可还原），加一条育碧分支：

1. **判世代**：按目录里的 loader 名定 R1 或 R2；若同时存在 `dbdata.dll`，叠加 D 密层。
2. **备份**：原 loader 与 `dbdata.dll` 一并备份。
3. **铺装**：按原名覆盖 loader；有 `dbdata.dll` 的再覆盖它。
4. **写配置**：`uplay_r2.ini` 用现有生成逻辑；有 `dbdata.dll` 时补写 `dbdata.ini`，`dlcs` 取同一份列表。
5. **令牌交接**：不内置账号。用户启动一次游戏后若出现 `token_req.txt`，用自己账号换取 token 并导入 `token.ini`（与 Steam 侧的 `.ost` 同一形态）。
6. **还原**：删除写入的配置文件，备份改回原名。

## 4. 边界

- **无 D 密的 R1/R2 游戏**：纯模拟器即可，不需要任何账号。
- **有 D 密的游戏**：需要 token，只能由拥有该游戏的正版账号签发，开源组件不伪造。
- **SecureDLC 世代（2025-09 起，如 AC Shadows）**：另需账号签发的 `ownershipList`（即 `token.ini` 的 `ownership=`）。
- **UNO 类多组件 Unity 育碧游戏**：除 loader 外还缺 `ubiservices`、`uprofile`、`Storm`，无任何项目针对性模拟；本方案覆盖不到，保持挂起。
- 联机依赖社区补丁且需各方版本一致；带反作弊的在线模式不可行。
- **待核对**：D 密游戏究竟必须"本机正版激活过一次"（我们旧结论），还是"有拥有该游戏的账号即可取 token 换入"（复刻文档的说法）——两者层面不同，均未实测。
