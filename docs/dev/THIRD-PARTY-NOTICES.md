# 第三方组件声明（THIRD-PARTY NOTICES）

本仓库在 `NoSteamLauncher/Resources/` 下随源码分发了若干第三方编译产物，用于免 Steam 部署功能。以下为各组件的来源、许可证与用途说明。这些组件版权归各自作者所有，本项目仅作集成与学习用途。

## 1. GSE 模拟器（Goldberg Emulator Fork）

| 项目 | 内容 |
|---|---|
| 文件 | `Resources/emu/game_goldberg/regular/x86/steam_api.dll`、`regular/x64/steam_api64.dll` |
| 上游 | https://github.com/Detanup01/gbe_fork |
| 许可证 | GNU LGPL-3.0 |
| 用途 | 部署到游戏目录以替代官方 Steam API，提供本地模拟的 Steamworks 接口 |
| 取件 | release **release-2026_09_27**，资产 `emu-win-release-vs26.7z`（2026-10-05 取）——与 SAC 硬编码的资产名一致；vs26/vs22 两版均为**静态链接 CRT**（导入表里没有 VCRUNTIME/MSVCP/ucrt），因此不需要用户机装 VC 运行库 |

## 2. Steamless 脱壳工具（K0oRui/Steamless-KR 4.3.0，自编译）

| 项目 | 内容 |
|---|---|
| 文件 | `Resources/Steamless.CLI.exe`（+`.exe.config`）、`Resources/Steamless.API.dll`、net48 运行时垫片（`System.Memory.dll` / `System.Buffers.dll` / `System.Runtime.CompilerServices.Unsafe.dll` / `System.Threading.Tasks.Extensions.dll` / `System.Numerics.Vectors.dll` / `Microsoft.Bcl.AsyncInterfaces.dll` / `System.ComponentModel.Annotations.dll` / `CommunityToolkit.Mvvm.dll`）、`Resources/Plugins/*.dll`（Variant 1.0–3.1 解壳插件 + `Steamless.API.dll` + `Iced.dll`） |
| 上游 | https://github.com/K0oRui/Steamless-KR （原版 https://github.com/atom0s/Steamless ） |
| 许可证 | CC BY-NC-ND 4.0（原版许可，本仓库跟随上游 fork 编译，仅供非商业学习研究） |
| 用途 | 移除游戏 EXE 的 SteamStub 壳，使模拟器部署成为可能 |
| 取件 | tag **v4.3.0**（`bb5d0ef`，2026-09-19），2026-10-05 编译 |

> **为什么要自己编**：上游 release 的 Windows 包是 `net9.0` **框架依赖**——用户机没装 .NET 9 运行时就会启动失败，与"发布包解压即用、不依赖 .NET 运行时"冲突。因此本仓库按 **net48** 重新编译同一份源码（Windows 自带 .NET Framework 4.8）。
> 相对上游源码只有两处 net9-API 兼容补丁：`Steamless.API/Crypto/AesHelper.cs` 的 `List.AddRange(Span)` → `ArraySegment<byte>`；`Steamless.CLI/Program.cs` 的 `Stream.ReadExactly` → 手写读取循环。`Directory.Build.props` 的 `TargetFramework` 由 `net9.0` 改为 `net48`。
> 反汇编器随上游由 SharpDisasm 换成 **Iced 1.21.0**（`Resources/Plugins/Iced.dll`）。

## 3. SteamAPICheckBypass

| 项目 | 内容 |
|---|---|
| 文件 | `Resources/SteamAPICheckBypass/SteamAPICheckBypass.dll`、`SteamAPICheckBypass_x32.dll` |
| 上游 | https://github.com/SteamAutoCracks/Steam-auto-crack （MIT License） |
| 用途 | 可选部署的 winmm.dll 劫持层，向游戏自身的完整性检查隐藏模拟器痕迹 |
| 取件 | tag **2.3**，资产 `Release_dlls.rar`（2026-10-05 取） |

## 4. 仅作参考、未随仓库分发的项目

以下项目的源码仅在本机作为实现参考阅读，不包含在本仓库及其发布物中：
[OpenSteamTool](https://github.com/OpenSteam001/OpenSteamTool)、
[FluentInstall](https://github.com/Files-community/FluentInstaller) 等见 `RefProjects/`（按优先级分 `1-在用`/`2-挂起`/`3-可清暂留` 三层存放，不纳入版本控制）。

## 5. Goldberg R2 模拟器（免育碧）

| 项目 | 内容 |
|---|---|
| 文件 | `main/Assets/Ubisoft/upc_r2_loader64.dll`（内嵌资源，免育碧部署时解压并改名复制到游戏目录） |
| 上游 | https://github.com/Detanup01/Goldberg_r2_extended |
| 许可证 | GNU LGPL-3.0 |
| 用途 | 替换育碧游戏的 uplay loader（`uplay_r2_loader64.dll` / `uplaypc_r2_loader64.dll`），无需运行 Ubisoft Connect 即可启动游戏 |
| 构建说明 | 自 `RefProjects/1-在用/Goldberg_r2_extended`（latest main）源码编译：`cl /std:c++20 /LD /DEMU_RELEASE_BUILD /DNDEBUG emu.cpp User32.lib Shell32.lib Ole32.lib /EHsc /Ox /link /OUT:upc_r2_loader64.dll`；相对最新 release 含 UPC_StorageFileOpen 存档修复 |

> ⚠️ 与 GSE 同样采用 **LGPL-3.0**。本项目以自编译的本体 DLL 形式集成本组件，仅供非商业的学习与研究用途。

## 6. SAM.API（Steam Achievement Manager 的接口封装，成就页用）

| 项目 | 内容 |
|---|---|
| 文件 | `main/SteamApi/**`（源码，非二进制：Client / Steam / NativeWrapper / NativeStrings / Callbacks / Interfaces / Wrappers / Types） |
| 上游 | https://github.com/gibbed/SteamAchievementManager （`SAM.API` 命名空间，作者 Rick / gibbed，2024） |
| 许可证 | **zlib**（见同目录 `LICENSE.txt`，随源码分发） |
| 用途 | 成就页读写 Steam 成就：`steamclient64.dll` → `ISteamClient018` / `ISteamUserStats013`（`RequestUserStats` / `GetAchievementAndUnlockTime` / `SetAchievement` / `StoreStats`），在 `--stats-dump` / `--stats-apply` 子进程里使用 |
| 本地改动 | ① `Steam.cs` 增加 `InstallPath` 覆盖（本项目的 Steam 路径来自自身配置，不回退注册表）；② 未引入 `GlobalSuppressions.cs`、`KeyValue*.cs`（成就定义改用本项目自己的 `SteamStatsSchema` 解析）；③ 本 csproj 因此需要 `AllowUnsafeBlocks`（`NativeStrings.cs` 用 unsafe 处理原生字符串） |

> zlib 许可允许闭源/商用与修改，条件是保留版权声明、标明改动（已在上表列出）、不得移除许可声明。

---

## 使用声明

1. 本项目仅供技术学习与研究，请支持正版，购买你玩的游戏；
2. 上述第三方组件的商标、版权均归属其各自作者；本项目不对这些组件的功能与安全性作任何担保；
3. 分发或二次使用本项目时，请一并保留本声明及各组件的许可证条款；
4. 因使用本项目产生的一切后果由使用者自行承担。
