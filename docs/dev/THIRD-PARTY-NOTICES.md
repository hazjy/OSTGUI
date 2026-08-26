# 第三方组件声明（THIRD-PARTY NOTICES）

本仓库在 `NoSteamLauncher/Resources/` 下随源码分发了若干第三方编译产物，用于免 Steam 部署功能。以下为各组件的来源、许可证与用途说明。这些组件版权归各自作者所有，本项目仅作集成与学习用途。

## 1. GSE 模拟器（Goldberg Emulator Fork）

| 项目 | 内容 |
|---|---|
| 文件 | `Resources/emu/game_goldberg/regular/x86/steam_api.dll`、`regular/x64/steam_api64.dll` |
| 上游 | https://github.com/Detanup01/gbe_fork |
| 许可证 | GNU LGPL-3.0 |
| 用途 | 部署到游戏目录以替代官方 Steam API，提供本地模拟的 Steamworks 接口 |

## 2. Steamless 脱壳工具

| 项目 | 内容 |
|---|---|
| 文件 | `Resources/Steamless.CLI.exe`、`Resources/Steamless.API.dll`、`Resources/SharpDisasm.dll`、`Resources/Plugins/*.dll`（Variant 1.0–3.1 解壳插件） |
| 上游 | https://github.com/atom0s/Steamless |
| 许可证 | CC BY-NC-ND 4.0（署名-非商业性使用-禁止演绎） |
| 用途 | 移除游戏 EXE 的 SteamStub 壳，使模拟器部署成为可能 |

> ⚠️ 特别说明：Steamless 采用 **CC BY-NC-ND** 许可。本项目以其**未修改的原始形态**集成本组件，仅供非商业的学习与研究用途。如需商业使用，请自行前往上游获取并遵守其许可条款。

## 3. SteamAPICheckBypass

| 项目 | 内容 |
|---|---|
| 文件 | `Resources/SteamAPICheckBypass/SteamAPICheckBypass.dll`、`SteamAPICheckBypass_x32.dll` |
| 上游 | https://github.com/SteamAutoCracks/Steam-auto-crack （MIT License） |
| 用途 | 可选部署的 winmm.dll 劫持层，向游戏自身的完整性检查隐藏模拟器痕迹 |

## 4. 仅作参考、未随仓库分发的项目

以下项目的源码仅在本机作为实现参考阅读，不包含在本仓库及其发布物中：
[OpenSteamTool](https://github.com/OpenSteam001/OpenSteamTool)、
[FluentInstall](https://github.com/Files-community/FluentInstaller) 等见 `RefProjects/`（该目录不纳入版本控制）。

---

## 使用声明

1. 本项目仅供技术学习与研究，请支持正版，购买你玩的游戏；
2. 上述第三方组件的商标、版权均归属其各自作者；本项目不对这些组件的功能与安全性作任何担保；
3. 分发或二次使用本项目时，请一并保留本声明及各组件的许可证条款；
4. 因使用本项目产生的一切后果由使用者自行承担。
