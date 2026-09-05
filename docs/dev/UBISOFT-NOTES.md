# 育碧授权调研笔记（UBISOFT-NOTES）

> 状态：**线索与思路记录**，非定稿知识。信息来自公开资料与源码阅读，未经完整实测验证。
> 目的：评估「入库工具是否值得/如何集成育碧游戏支持」。定稿验证后可将成熟结论并入 DEV-NOTES。

## 1. 育碧授权体系如何工作

### 1.1 双层结构

```
游戏 EXE ──(SDK 调用)──► uplay_r1/r2_loader64.dll ──► 本机 Ubisoft Connect 客户端 ──► 育碧服务器
                                                                        （登录态 + 许可缓存）
```

- 游戏通过 SDK DLL（旧版 **R1**：`uplay_r1_loader64.dll`；现行 **R2**：`uplay_r2_loader64.dll`）与 UC 通信；
- `UPC_Init` 时游戏自报 AppID（因此模拟器无需配置 appid）；
- 运行中经 UPC API 查询所有权/entitlements（决定本体与 DLC 可玩性）、云存档、好友 overlay；
- 后端：`dmx.upc.ubisoft.com` 等育碧服务器；本地 UC 客户端持有登录态与许可缓存；
- 启动硬性要求：UC 客户端必须在运行且登录。官方离线模式要求至少成功联网启动过一次。

### 1.2 与 Denuvo 的叠加（双保护）

育碧重度新作常为 **Denuvo + Ubisoft Connect 双保护**：

| 层 | 职责 |
|---|---|
| Denuvo | EXE 代码完整性、反调试、本机激活令牌 |
| Ubisoft Connect | 所有权应答、entitlements、客户端在场检查 |

关键事实：**两层的"解药"不同且互不替代**——

- Denuvo 的激活令牌是各平台的缓存文件（PCGamingWiki 整理）：
  - Steam：`userdata\<uid>\<appid>\dbdata`
  - **Ubisoft Store：`%LOCALAPPDATA%\Ubisoft Game Launcher\cache\ownership\########`**
  - EA App：`License\########.dlf`
- 令牌与本机指纹绑定、有时效（需周期在线刷新）；"How Denuvo Is Actually Cracked" 的概括是：识别 Denuvo 校验所需的信息并喂给它，使其认为令牌对本机有效——即使令牌是在另一台 PC 生成的。

## 2. 现有绕过方案的三大流派

### 2.1 DLC 解锁器（正版拥有者的本地权益解锁）

- 代表：[acidicoala/UplayR1Unlocker](https://github.com/acidicoala/UplayR1Unlocker)、[UplayR2Unlocker](https://github.com/acidicoala/UplayR2Unlocker)
- 原理：Windows DLL 搜索顺序劫持——游戏目录放同名 loader DLL 接管 UPC API 应答，将"是否拥有该 DLC"伪造成 true；可选 JSON 配置定制行为
- 定位：给**已合法拥有本体**的用户解锁 DLC 权益，不提供游戏文件

### 2.2 完整模拟器（无需 UC 客户端）

- 代表：[Detanup01/Goldberg_r2_extended](https://github.com/Detanup01/Goldberg_r2_extended)
- 原理：实现 `upc.h` 全套 UPC 协议，替换 `uplay_r2_loader64.dll` 后接管全部 UC 交互；游戏调 `UPC_Init` 时自动上报 AppID（无需每游戏配置），`uplay_r2.ini` 做运行设置
- **作者 Detanup01 同时是 gbe_fork（GSE）的维护者**——与我们已在使用的 GSE 构建链同源，这是重要的低风险信号
- Nemirtingas 模拟器生态也有 Uplay 方向的同类项目

### 2.3 联机修复重打包

- online-fix.ru 系：整包自带破解与联机补丁，非工具形态，仅作参考

## 3. 相关生态工具

| 项目 | 说明 |
|---|---|
| [UplayDB/UplayKit](https://github.com/UplayDB/UplayKit) | C# SDK，与 Uplay/Ubisoft API（dmx.upc.ubisoft.com）交互——若做深层次集成可直接复用 |
| [BigBoiCJ/SteamAutoCracker](https://github.com/BigBoiCJ/SteamAutoCracker) | Steam 侧自动破解脚本（Steamless + 模拟器），与 OSTGUI 思路同构但不含育碧 |
| [denuvosanctuary/steam-ticket-generator](https://github.com/denuvosanctuary/steam-ticket-generator) | 本地生成 Steam EncryptedAppTicket（用于 Denuvo 保护游戏的所有权校验）——与 OSTGUI 的 `.ost`/SteamTicketExtractor 同一生态位 |
| Irdeto 官方博客 | [Is securing DLC against unlockers possible?](https://irdeto.com/blog/is-the-dlc-unlocker-a-nightmare-for-your-gaming-studio) ——厂商侧承认 hook API 伪造应答/替换 API DLL 是主要威胁模型 |

## 4. 与 OST / OSTGUI 体系的对照

| 维度 | Steam 侧（现有） | 育碧侧（潜在） |
|---|---|---|
| 所有权应答 | OST 内核伪造 NetPacket/PICS 应答 | UC 模拟器伪造 UPC entitlements |
| 内容解密 | Lua depot key 经 ConfigStore 劫持注入 Steam 解密管线 | ？（UC 内容下载由 UC 客户端完成，模拟器场景下内容来源需用户自备） |
| 清单获取 | manifest 文件 + 内核 code 上游兜底 | UC 客户端自行管理 |
| 票据/授权文件 | `.ost`（AppTicket+ETicket 导入导出） | ？可能对应 `cache\ownership\` 的导出导入 |
| 部署形态 | 替换 `steam_api*.dll` + steam_settings | 替换 `uplay_r2_loader64.dll` + `uplay_r2.ini` |

## 5. 思路与线索（待验证）

1. **部署模式可完全照搬 GBE 部署架构**：检测游戏目录中的 `uplay_r2_loader64.dll` → 备份 → 替换为模拟器 DLL → 写 `uplay_r2.ini`。与现有 GBE 部署代码同构，工作量主要是 UI 与流程复用。
2. **上游同源红利**：Goldberg_r2_extended 与 gbe_fork 同维护者，版本跟进与信任成本最低。
3. **前提约束**：用户手里必须有该游戏的合法游戏文件（UC 客户端下载或解包产物）；工具本身只解决"无 UC 客户端运行"，不提供任何内容。
4. **Denuvo 边界（2026-09 定论）**："绕过启动器 + D 密正常" = **两个独立条件，分别由两层满足**：
   - UC 层（免启动器）：**Goldberg_r2_extended**（替换 `uplay_r2_loader64.dll`，UPC 应答全接管）——唯一能做到彻底免 UC 客户端；
   - Denuvo 层（D 密正常）：**与模拟器无关**——只能靠**本机真实激活一次**（正版号启动留 `cache\ownership\########` 激活缓存，绑定机器指纹）或破解补丁。无任何"绕过启动器"工具能替代 Denuvo 激活；
   - **可行组合** = "正版激活一次（D 密层）+ Goldberg_r2 模拟器（UC 层）"，与 Steam 侧同构（Steam：dbdata 激活 + OST 内核喂票；育碧：ownership 激活缓存 + 模拟器答 UPC）。仅替换 loader 不碰主 EXE → Denuvo EXE 完整性校验不受影响，方案成立的前提；
   - **硬边界**：未激活的 D 加密新作纯工具免不了；Denuvo 令牌有时效需在线续期 → 模拟环境无法续期，需定期回正版环境开一次（本机已有激活的旧作通常离线可启）。
5. **待验证问题清单**：
   - Goldberg_r2_extended 对最新 UC 客户端与近年新游戏的兼容覆盖面？
   - 模拟环境下云存档/成就/好友等在线功能缺失对实际游玩的影响面？
   - Steam 版育碧游戏目录与 UC 版目录的结构差异（同一游戏两个发行渠道文件是否通用）？
   - `uplay_r2.ini` 各字段的完整语义（上游 README 只给了 fenyx 单例）？
   - 多人游戏（如 TC 的联机作品）在模拟下是否可用？
   - 部署目标 DLL 命名差异：`uplay_r2_loader64.dll` vs `uplaypc_r2_loader64.dll`（不同游戏可能不同）？
   - Goldberg_r2_extended 的版本跟进：与当前 UC 版本/2024+ 新游的兼容面随时间变化？

## 6. 生态现状（2026-09 检索补充）

- **Goldberg_r2_extended**（Detanup01）：仍维护（⭐78）——作者同时是 gbe_fork(GSE) 维护者，信任成本最低；
- **UplayR2Unlocker**（acidicoala）：社区主流 DLC 解锁用法实锤——放同名 DLL 劫持 + 编辑 `UplayR2Unlocker.jsonc` 指定 DLC（r/PiratedGames Anno 1800 教程）；适合"合法本体 + 解锁 DLC"，**不是"免启动器"方案**；
- **Batlez-DLC-Unlocker** / **CreamInstaller**（FroggMaster）：自动化参照——自动发现已装 Steam/Epic/Ubisoft 游戏并生成/维护解锁器配置，与 OSTGUI 自动化形态同构；
- Irdeto 官方博客：厂商威胁模型 = "hook API 伪造应答 / 替换 API DLL"（侧面背书该路线）；
- 检索环境说明：本会话 harness 网络受限（github 全文抓取/reddit 检索失败、exa 曾短暂 401），仓库细节建议在可联网环境核对；exa 已恢复可用。

## 7. 结论备忘

- **最佳方案（2026-09 定论）**：核心组件 = **Goldberg_r2_extended**（满足"免启动器"），D 密层靠**"正版激活一次"留本机缓存**——组合即"Steam 侧 dbdata+OST 的育碧同构"；部署骨架可完全复用 NoSteam（检测 `uplay*_r2_loader64.dll` → 备份 → 替换 → 写 `uplay_r2.ini`）；
- **推荐起步路线（轻量）**：先做"无 D 加密游戏"的模拟器部署（干净、无 Denuvo 边界）；D 加密游戏作为后续（需用户正版激活配合，文档写明边界）；
- UplayR2Unlocker（DLC 解锁）定位不同——只适合"已有 UC 的合法本体解锁 DLC"，不作为"免启动器"主路线，可作为附加功能；
- 技术上与现有免 Steam 部署高度同构，集成的主要成本在**测试矩阵**（游戏×UC版本×渠道组合），而非代码；
- 决策点在于产品定位：OSTGUI 目前是 Steam 生态专用工具，加入育碧支持会扩大定位但也引入跨平台测试负担；
- 本文档仅记录公开技术资料与研究思路，不构成实施承诺。
