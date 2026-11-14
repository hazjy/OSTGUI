# REF-免Steam部署（GUI 侧）

> 何时读：做免 Steam 部署 / 免育碧部署，或改铺装、备份、还原逻辑时。
> 管什么：两条部署链路的实现事实、组件与字段级配置契约、已知边界。
> 不写什么：实施步骤与排除清单见 `doc/TODO/待办/261015-免育碧部署扩展.md`；第三方组件的许可与来源声明见 `THIRD-PARTY-NOTICES.md`。

## 免 Steam 部署（GSE / SAC 对齐，GUI 侧实现事实）

- 流程：选游戏 EXE + AppID →（有壳时）Steamless 脱壳替换 EXE → GSE(Goldberg) 模拟器部署进游戏目录 → 可选 SteamAPICheckBypass（winmm 劫持隐藏模拟器痕迹）；原文件备份为 `.bak`，删 `steam_settings` 并改回 `.bak` 即还原。
- **备份不覆盖**：EXE 与 DLL 一致，`.bak` 已存在就保留（跳过备份），重复部署不会用脱壳后的 EXE 覆盖唯一原件（对齐 SAC 的判定）。
- **游戏自带的 `Plugins` 目录不会被删**：脱壳要往 `<游戏目录>\Plugins` 拷插件（Steamless 按目标目录找插件），该目录已存在且**不是我们留下的**（无 `Steamless.API.dll` + `Variant*.dll` 标记）→ 就地改名成 `Plugins.ostgui-bak` 暂存、脱壳结束原名还原；只有确认是我们的才整目录删。暂存失败（占用 / 权限）直接中止脱壳并说明，绝不硬删。
- 对齐 SAC（SteamAutoCrack）的部署逻辑与 ini 配置格式；无壳游戏自动跳过脱壳不中断；2016 前 SDK 的老游戏会额外生成 `steam_settings/steam_interfaces.txt`（gbe_fork 需要）。
- **资源嵌入**：所有二进制（Steamless CLI/插件、GSE 模板、Bypass）以 EmbeddedResource 打进 `NoSteamLauncher.dll`，运行时解压到 `%TEMP%\OSTGUI_NoSteamLauncher\<版本>\` 并做关键文件完整性校验，缺失自动重解压。
- 已知冲突：自带 winmm 依赖或有反作弊的游戏对 Bypass 可能不适配（默认关闭）。
- **一键还原**照抄 SAC `SteamAutoCrack.Core/Utils/Restore.cs` 四步：① 目录里存在 `SteamAPICheckBypass.json` 才递归删 `version.dll` / `winmm.dll` / `winhttp.dll`（不做哈希校验）；② 递归删 `steam_interfaces.txt` / `local_save.txt` / `SteamAPICheckBypass.json`；③ 递归把**所有** `*.bak` 换回原名（覆盖式 `File.Move`，SAC 是先删再改名，结果一致且失败不丢备份）；④ 递归删所有 `steam_settings` 目录。
- ③ 是通配，游戏自带的 `*.bak` 也会被换回原名，这是照抄 SAC 的既定代价（曾试过白名单版，被要求改掉）。
- **部署根 = 游戏根，不是 exe 所在目录**：UE 布局的 `steam_api64.dll` 在 `<根>\Engine\Binaries\ThirdParty\Steamworks\Steamv<NNN>\Win64\`，与 exe（`<根>\<项目>\Binaries\Win64\`）**不同枝**：`GameRootResolver` 向上最多找 3 层、命中「含 `Engine\Binaries\ThirdParty\Steamworks`」的那层即为根（绝不上溯到盘根或 `steamapps\common`），找不到就仍用 exe 目录（普通游戏行为不变）。**部署与还原走同一套根解析**，否则 UE 布局会「部署到 Engine、还原只扫 exe 目录」留残留。
- 还原**只动游戏目录、不解压资源**（不走 `EnsureExtracted()`）；逐项日志 + 还原后复查残留：被占用（游戏在跑）时逐条报失败并输出"还原未完成"，不报假成功；每个文件操作先按 **4×250 ms 重试**：刚写下去的 11 MB 模拟器 DLL 常被杀软 / 索引器短暂映射住，此时覆盖式 `File.Move` 报 `Access denied`，重试即可自愈（合成 UE 目录树实测：无重试必失败、有重试 571 ms 后成功）。
- `%APPDATA%\GSE Saves\<appid>` 只提示路径不删；`steam_appid.txt` 归 AppID Changer 台账管，还原不碰；幂等（无残留时输出"未发现模拟器残留（可能已还原）"）。

## 免育碧部署（实验性）：组件与配置契约

- 覆盖范围、体系与路线见 `doc/TODO/待办/261015-免育碧部署扩展.md` 附录 A；取件纪律 = 一律从上游 Release 取或自行编译，不取第三方工具的打包资源。
- **loader 三档命名**：`upc_r2_loader64.dll`（Unity 新作，如 UNO）/ `uplay_r2_loader64.dll` / `uplaypc_r2_loader64.dll`；位宽须与游戏一致，扫描游戏根目录 **或** Unity 的 `*_Data\Plugins\x86_64\`（两处都扫）。
- **配置与对应 DLL 同目录**：`uplay_r2.ini` 随 loader（上游 `emu.cpp`：`lib_path + "\\uplay_r2.ini"`）；`dbdata.ini` / `token.ini` 随 `dbdata.dll`。
- `uplay_r2.ini`：`[Settings]` 账号 / 语言 / 存档（`SaveType` 0=appdata、1=游戏目录、2=`SavePath`）+ `[DLC]` 列表；不改上游字段名。
- `dbdata.ini`：`[settings] dlcs=`，缺该段回退读 `upc_r2.ini`；兼容旧 R1 loader 的 `[Settings] Username/SaveType/SavePath`。
- `token.ini`：`[token] token=` / `ownership=` 两个不透明 base64，只能由拥有该游戏的账号签发（开源组件不伪造）；`dbdata.dll` 首启在游戏目录生成 `token_req.txt`（含 appid 与请求体，是换 token 的输入；不出现 = DLL 未被加载）。
- `[DLC]` 与 `dbdata.ini` 的 `dlcs=` 必须同一份列表；铺装顺序 = 按 loader 名定 R1 / R2 → 备份 loader 与 `dbdata.dll` → 覆盖式铺装（有 `dbdata.dll` 才叠加 D 密层）→ 写配置 → 留还原路径；也可用 Koaloader 不替换原文件、按 DLL 搜索顺序劫持注入。

| 组件 | 文件 | 作用 | 上游 / 取件 |
|---|---|---|---|
| UC 层 R2（主路线） | `upc_r2_loader64.dll` | 接管全部 UPC 应答，免 UC 客户端 | [Detanup01/Goldberg_r2_extended](https://github.com/Detanup01/Goldberg_r2_extended)（与在用 gbe_fork 同维护者） |
| UC 层 R1（旧世代） | `uplay_r1_loader64.dll` | 同上 | [Mr_Goldberg/goldberg_emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) · 注入式 [acidicoala/UplayR1Unlocker](https://github.com/acidicoala/UplayR1Unlocker)（0BSD） |
| D 密层 | `dbdata.dll` | 模拟育碧 Denuvo 变体令牌接口，消费 `token.ini` | [denuvosanctuary/ubi-dbdata](https://github.com/denuvosanctuary/ubi-dbdata)（Rust，LGPL-3.0）· C# 版 [Detanup01/UplayServer](https://github.com/Detanup01/UplayServer)（MIT，已停更） |
| DLC 解锁（附属，非免客户端） | 同名 loader 劫持 | 给合法本体解锁 DLC 权益 | [acidicoala/UplayR2Unlocker](https://github.com/acidicoala/UplayR2Unlocker)（配 `UplayR2Unlocker.jsonc`） |
| 联机（与启动无关） | Photon / Quazal 模拟 | 多人联机 | ServerEmus `Plugin.Photon` / `Plugin.UplayServer.Quazal` / `Plugin.DTLS` |
| 现成打包产物 | `dbdata.dll` + `upc_r1/r164/r2/r264` | 免编译取件（UPX + NativeAOT，杀软可能误报） | [ServerEmus/Release.Uplay](https://github.com/ServerEmus/Release.Uplay)；Server 端无二进制，需自建 |
| 多组件栈（UNO 类，未覆盖） | `ubiservices.dll` / `uprofile.dll` / `Storm.dll` | 向 UC 要服务 | 无项目模拟；协议资料见 [UplayDB](https://github.com/UplayDB)、[YoobieRE/ubisoft-demux-node](https://github.com/YoobieRE/ubisoft-demux-node) |

## 归档：免 Steam 部署夹具实测

- 夹具两轮（完整产物正常还原；`.bak` 被独占锁定时只该项失败、其余照做、报"还原未完成"）+ 用户真机 Ib 部署 → 还原后正常。
