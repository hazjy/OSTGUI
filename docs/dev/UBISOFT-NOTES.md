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
- **ServerEmus 生态（2026-09 新增）**：UplayServer（Detanup01，已归档）拆分为多仓库——[Uplay.upc_r2](https://github.com/ServerEmus/Uplay.upc_r2)（**UPC R2 完整导出模拟**，C#/DllShared 框架 + 命名管道客户端 `UseNamePipeClient`，**有 GitHub Releases 持续发布** `download_releases.yml` 每日同步）、Uplay.dbdata / Uplay.upc_r1（同系列）、[Release.Uplay](https://github.com/ServerEmus/Release.Uplay)（发布物：`dbdata.dll` + `upc_r1.dll/r164/r2/r264`，UPX+NativeAOT 编译，杀软误报 `Program:Win32/Wacapew.A!ml`）。**体系 = 模拟 dbdata/upc 核心库 + 命名管道 + 本地 Server（LiteDB+认证，Server 无现成二进制需自建）**；
- 检索环境说明：本会话 harness 网络受限（github 全文抓取/reddit 检索失败、exa 曾短暂 401），仓库细节建议在可联网环境核对；exa 已恢复可用。

## 6.5 UNO 实机测试结论（2026-09-05，Steam AppID 470220）

- **loader 三档文件名**：`upc_r2_loader64.dll`（Unity 育碧新作）/ `uplay_r2_loader64.dll` / `uplaypc_r2_loader64.dll`
- **Unity 子目录布局** `*_Data\Plugins\x86_64\`（除根目录外还要扫这层）；`uplay_r2.ini` **必须与 loader 同目录**（emu.cpp `lib_path + "\\uplay_r2.ini"` 求证）
- **结论**：无第三条开箱即用路线，免育碧**保持挂起**；UNO 缺 `ubiservices`/`uprofile`/`Storm` 三件且无模拟
- **ServerEmus 是覆盖多组件 UC 栈的唯一完整链路**（DLL + 命名管道 + 本地 Server），未实测、Server 需自建，暂不集成
- 别走这条路：联机"其他"下拉的 BAT 脚本注入 → 只是路线 B 的减法（等于功能倒退），用户否决
（来源：agents-log 09-05 / 09-06 / 09-19）

- **UNO = "多组件 UC 栈 + Steamworks 双栈"**：插件区含 `upc_r2_loader64.dll`（loader）+ `ubiservices.dll` + `uprofile.dll` + `dbdata.dll` + `Storm.dll` + `steam_api64.dll`/`uno_steam.dll`（Unity + Steamworks.NET）。**验证了 Goldberg R2 单 loader 模拟的边界**：只替换 `upc_r2_loader64.dll` 后，游戏仍弹"需要育碧客户端"（ubiservices/uprofile 向真实 UC 客户端要服务）；**流畅入库（同 Goldberg 核心）对 UNO 同样失败**——非实现缺陷，架构不兼容；
- **loader 命名三档（服务 KnownLoaders）**：`upc_r2_loader64.dll`（Unity 育碧新作，如 UNO）/ `uplay_r2_loader64.dll` / `uplaypc_r2_loader64.dll`；布局：根目录 或 `*_Data\Plugins\x86_64\`（Unity）；`uplay_r2.ini` **必须与 loader 同目录**（emu.cpp `lib_path + "\\uplay_r2.ini"`）；
- **结论**：免育碧可选**两条路线**——① Goldberg 单 loader（轻、已实现，兼容传统单 loader R2 游戏）；② ServerEmus 链路（DLL+命名管道+本地 Server，理论覆盖 UNO 类，**未实测 + Server 需自建 + 重架构**）。UNO 本身作为"不兼容样本"记录，需此类游戏支持则走路线②（spike 门槛高，暂不集成）。

## 6.6 ServerEmus spike 实验结论（2026-09-06，UNO 实测）

- **做了**（本机全链路实操）：检出 UplayServer 源码 → `dotnet publish ServerApp`（net9.0，恢复 NuGet——本机 TLS/schannel 需完全权限沙箱才可；日志另记）→ `cert/v2_run` 自签证书（global CA + services.pfx，密码 CustomUplay，SAN 覆盖 `*.ubi.com`/dmx/ubiservices/onlineconfigservice）→ hosts 劫持 `dmx.upc.ubisoft.com` + `local-ubiservices.ubi.com` → 证书装系统受信任根 → Server 监听 0.0.0.0:443（单一 TLS 服务，Demux/HTTPS 同端口路由）→ 启动 UNO；
- **结果**：**连接层完全打通**——Server 日志确认收到 UNO 的 `POST /v1/profiles/{userid}/global/ubiconnect/playsession/api/sessions`、economy/challenges/rewards/configuration 等请求（游戏启动即向育碧服务鉴权）；**但业务层半成品**（`Functionality [ ]` 属实），playsession 等路由应答不完整 → UNO 进程退出，未过"需要育碧客户端"；
- **双路线边界定型**：Goldberg=接口层可用、缺网络服务；ServerEmus=网络层可用、业务未完成——**完整支持 UNO 类 = 接口模拟 + 网络服务 + 业务应答三合一（上游未完成的大工程）**；
- **决策**：免育碧**归档为挂起项**——保留 Goldberg 单 loader 实现（传统 R2 游戏可用）；UNO 类依赖上游 ServerEmus 成熟度（upc_r2/dbdata 仍活跃发布，**追踪点**：若其业务补完再评估接入）；当前重心回归 Steam 主线。实验态已清理（Server 停止、443 释放）；hosts 两行与 Root 证书可逆可删。

### 6.6.1 可复现路径与环境记录（复测用）

- **产物（保留于本机）**：`RefProjects/2-挂起/UplayServer/out-server/`（ServerApp net9.0 发布物 + ServerCore/依赖）、`RefProjects/2-挂起/UplayServer/cert/`（自签 global/services/signer 证书，密码 `CustomUplay`；SAN 覆盖 `*.ubi.com` 及 dmx/ubiservices/onlineconfigservice 等）；
- **构建**：`dotnet publish Server\ServerApp\ServerApp.csproj -c Release -o out-server`（依赖 NuGet：LiteDB/ModdableWebServer/NetCoreServer/JWT/Google.Protobuf/Uplay-Protobufs 等；**本机 TLS/schannel 在受限沙箱不可用**——构建须在完全权限沙箱或正常终端下执行）；
- **启动坑**：无 stdin 后台运行 `dotnet ServerApp.dll` 会因 `Console.ReadLine()!` 返回 null 崩（Program.cs:34 NRE）——本地已加实验补丁 `if (endCheck == null) Thread.Sleep(Timeout.Infinite)` 保持服务（仅实验用途）；`ServerConfig.json` 自动生成：`DemuxUrl=dmx.local.upc.ubisoft.com:443`、`HTTPS_Url=local-ubiservices.ubi.com:443`（**Demux 与 HTTPS 同 socket 单 TLS 443 端口按路由分发**，非端口冲突）、`GlobalOwnerShipCheck=true`、`ServicesCertPassword=CustomUplay`；
- **装配**：hosts 追加 `127.0.0.1 dmx.upc.ubisoft.com` + `local-ubiservices.ubi.com`（先备份）；`certutil -addstore Root global.crt`（+ services.crt 可选）；
- **实测信号**：Server 日志收到 `POST /v1/profiles/{userid}/global/ubiconnect/playsession/api/sessions`、economy/challenges/rewards/configuration/entities 等——游戏启动即向育碧服务鉴权，链路全通；失败点 = 业务应答不完整；
- **清理（可逆）**：停 Server → hosts 删上述两行（备份 `%TEMP%\hosts.bak_*`）→ `certutil -delstore Root "Custom Ubisoft"` / `"*.ubi.com"`。

## 6.7 第三方候选清单（2026-09-06 子代理联网调研全量）

> 结论：**不存在"开箱即用、公开维护"的第三条完整路线**（全网"uplay/ubiconnect emulator"收敛回 Goldberg 系与 ServerEmus 系）。以下为组件级/旁门候选，作后续积木或澄清排除用。

**A. 组件/研究级（最接近"第三条路线"的现成材料）**

| # | 项目 | 定位 | 成熟度 | 对 UNO 类覆盖 |
|---|---|---|---|---|
| A1 | [denuvosanctuary/ubi-dbdata](https://github.com/denuvosanctuary/ubi-dbdata) | `dbdata.dll` 客户端模拟（育碧 Denuvo 变体），可 drop-in 替换 | 活跃（2026，~79★） | 仅 dbdata 一件；与 UC 原生 dbdata 是否同变体需核验 |
| A2 | [YoobieRE/ubisoft-demux-node](https://github.com/YoobieRE/ubisoft-demux-node) | 游戏↔UC 的 named-pipe/protobuf demux 协议实现 | 库级非成品（22★） | 可作"本地假 UC 核心"通信层 |
| A3 | [UplayDB 组织](https://github.com/UplayDB)（Protobufs/Ubi-Parser/UbiProxyDlls/ChannelKit/UplayWrapper） | upc.exe 完整 protobuf 定义 + UC 缓存解析 | 研究资源，2026 仍在更 | 业务层"协议图纸"，不直接覆盖 |
| A4 | [ServerEmus/Release.Uplay](https://github.com/ServerEmus/Release.Uplay) | 预编译 upc_r1/r164/r2/r264 + dbdata（AOT/UPX） | 12★，2025-09 后停更 | 属路线 2 现成产物 |
| A5 | ServerEmus Plugin.Photon / Plugin.UplayServer.Quazal / Plugin.DTLS | Photon/Quazal 联机服务模拟 | 2025-2026 活跃 | 与启动无关（UNO 多人走 Photon，联机才需要） |

**B. 旁门 / 特定游戏**

- **UNO-TiNYiSO（2018-01-13 场景组破解）**：UNO 曾"复制 crack 即玩"（联机不可用）——**证明其多组件 UC 栈存在 per-game 绕过路径，但无通用工具**（[ovagames](https://www.ovagames.com/638498-uno-tinyiso.html)、[skidrowrepack](https://skidrowrepack.com/9599-uno.html)）；
- 中文站离线 Build / "免Uplay补丁"（3DM、17wanjia：看门狗 2014、AC3 等）——**R1 时代 per-game**，非通用；
- 官方离线：AC Brotherhood Steam 版已官方支持免 UC 离线（[steamsolo](https://steamsolo.com/guide/...)）；**UNO 官方明确回复"必须 UC"**（[Steam 讨论](https://steamcommunity.com/app/470220/discussions/1/5081733371341210063/)）；
- [Skip-Game-Launcher](https://github.com/voc0der/Skip-Game-Launcher)：仅跳启动器 UI 直拉 exe，不破 DRM，仅适用本体不强制校验的游戏；
- UplayR1/R2 Unlocker / Koalageddon / CreamInstaller（DynDruid/ubden）：**正版 DLC 解锁器**（需客户端在线），非免客户端方案（澄清排除）。

**C. 已排除/死掉**：Nemirtingas 生态（无 Uplay 方向，仅 Steam/Epic/GOG/Galaxy）；philicious/open-uplay（2016，R1）；gloriag/uplay-stub（404 疑似删除）；Rat431 系（Mini_Uplay_API_Emu/ColdPlay_Uplay）与 Re0xCat/uplay-r1-loader、Detanup01/upc_r1（均 R1，UNO 是 R2）；HOCKI1/Mr_Goldberg_UPlay_R2_emu（2022 单日 fork，同 Goldberg R2）；michal-kapala/ubi-gs（2000-2005 老 GS 服务，非现代 ubiservices）；jbousquie/OfficeGames（同名无关）。

**UNO 多组件栈覆盖缺口表**

| UNO 组件 | 现成覆盖 |
|---|---|
| `upc_r2_loader64.dll` | Goldberg_r2_extended / ServerEmus upc_r2（已有，UNO 实测失败） |
| `dbdata.dll` | ServerEmus Uplay.dbdata（服务端）+ ubi-dbdata（客户端，待核验） |
| `ubiservices.dll` / `uprofile.dll` / `Storm.dll` | **无任何项目针对性模拟** |

**现实方向**：覆盖 UNO 类 = 用 UplayDB 协议资料 + ubisoft-demux-node **自建本地假 UC 核心**（工程量大、非通用），或 per-game 补丁；通用"免 UC"对多组件 Unity 育碧游戏属上游未完成领域，暂不接。

## 7. 结论备忘

- **最佳方案（2026-09 定论）**：核心组件 = **Goldberg_r2_extended**（满足"免启动器"），D 密层靠**"正版激活一次"留本机缓存**——组合即"Steam 侧 dbdata+OST 的育碧同构"；部署骨架可完全复用 NoSteam（检测 `uplay*_r2_loader64.dll` → 备份 → 替换 → 写 `uplay_r2.ini`）；
- **推荐起步路线（轻量）**：先做"无 D 加密游戏"的模拟器部署（干净、无 Denuvo 边界）；D 加密游戏作为后续（需用户正版激活配合，文档写明边界）；
- UplayR2Unlocker（DLC 解锁）定位不同——只适合"已有 UC 的合法本体解锁 DLC"，不作为"免启动器"主路线，可作为附加功能；
- 技术上与现有免 Steam 部署高度同构，集成的主要成本在**测试矩阵**（游戏×UC版本×渠道组合），而非代码；
- 决策点在于产品定位：OSTGUI 目前是 Steam 生态专用工具，加入育碧支持会扩大定位但也引入跨平台测试负担；
- 本文档仅记录公开技术资料与研究思路，不构成实施承诺。
