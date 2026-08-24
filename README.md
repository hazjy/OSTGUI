# OSTGUI

OpenSteamTool 可视化管理工具（Windows / WinUI 3）。

OSTGUI 是 [OpenSteamTool](https://github.com/OpenSteam001/OpenSteamTool) 的桌面图形界面：搜索并入库游戏、自动生成解锁 Lua 配置、补齐 depot 解密密钥与访问令牌、管理已入库游戏、处理 Denuvo 授权与联机启动，并支持一键免 Steam 启动（自动脱壳 + Goldberg 模拟器部署）。

> ⚠️ 本项目仅供学习与交流。请支持正版，购买你玩的游戏。

## 功能

- **免 Steam 启动**：选择游戏 EXE 一键完成 SteamStub 脱壳与 Goldberg 模拟器部署（含配置生成、可选反检测 Bypass），之后无需启动 Steam 直接游玩
- **搜索入库**：按游戏名称 / AppID / Steam 链接搜索，一键生成完整 Lua 配置并写入 `Steam/config/lua/`
- **多源清单**：内置 SAC 分流、MHub、GitHub、Sudama 等 8 个预置源，可独立开关与调整优先级，任一成功即完成入库
- **密钥与令牌**：Sudama 全量密钥缓存（24h TTL），支持应用内刷新与本地文件导入，入库自动补齐 depot key / access token
- **DLC 支持**：可选"添加所有 DLC"，自动追加 DLC 的 addappid 与 addtoken
- **固定版本配置**：可选预写注释形式的 `setManifestid`，备用不启用，随时可在库页切换
- **入库管理**：扫描已入库游戏、编辑 Lua、复制 AppID / 游戏名、查看入库信息、切换版本模式（自动 / 固定）
- **Denuvo 授权**：.ost 授权文件导入 / 导出 / 在线提取
- **480 联机**：以 Spacewar(480) 身份启动已入库游戏，启用 Steamworks 联机
- **其他**：浅色 / 深色 / 跟随系统主题、入库结果系统通知、运行日志（可复制 / 查看文件）

## 界面

主页 / 搜索入库 / 入库管理 / 联机 / D加密授权 / 免Steam(S.A.C) / 重启 Steam / 设置。

## 环境要求

- Windows 10 19041（20H1）及以上，x64
- Steam 客户端（可自动检测安装路径）
- OpenSteamTool 内核已注入 Steam（主页会显示关键 DLL 状态）

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/) 与 Visual Studio 2022+（含 WinUI 工作负载）。

根目录的 `build.bat` 是唯一构建入口：

- 自动探测 VS 安装路径（支持 VS 2022 / 18 与 Community / Professional / Enterprise）
- 控制台安静输出，不再刷屏；完整日志落盘 `%TEMP%\ostgui_build.log`
- 构建失败时自动在控制台打印全部错误并提示日志路径
- `build.bat /r`：构建成功后自动启动应用

```bat
REM 直接构建（Debug）
build.bat

REM 构建成功后自动启动
build.bat /r
```

自包含发布（Release，产物在 `bin\Release\...\publish`）：

```bat
MSBuild main\OSTGUI.csproj /t:Publish /p:Configuration=Release ^
  /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:WindowsAppSDKSelfContained=true
```

## 项目结构

```
main/
├─ Pages/            界面（主页 / 搜索 / 入库管理 / 联机 / 授权 / 免Steam / 设置）
├─ ViewModels/       MVVM 视图模型
├─ Services/         入库、清单、密钥缓存、Lua 生成、Steam 交互等
├─ Models/           数据模型（清单源、入库项、授权条目等）
├─ Helpers/          转换器等辅助
├─ Assets/           内置资源（重要说明页等）
├─ docs/             第三方组件声明、版本更新说明
└─ NoSteamLauncher/  免 Steam 部署类库（脱壳 / 模拟器部署，内嵌运行时资源）
```

## 清单源

内置 SAC 分流、Walftech、MHub、SteamAutoCracks V2、Sudama、清单不求人、GitHub (Auiowu)、自动搜索 GitHub 八个预置源，可在设置页启用 / 禁用并调整优先级；需要密钥的源（MHub、GitHub）在设置页填入 API Key。

## 免责声明

本项目仅用于技术学习与研究，不包含任何游戏文件、清单文件或受版权保护的内容。密钥与令牌来自公开的第三方数据源，请自行判断其合规性。使用本项目产生的一切后果由使用者自行承担。

免 Steam 部署功能随仓库分发了以下第三方编译组件：

| 组件 | 用途 | 许可证 |
|---|---|---|
| [GSE 模拟器（gbe_fork）](https://github.com/Detanup01/gbe_fork) | Steam API 本地模拟 | LGPL-3.0 |
| [Steamless](https://github.com/atom0s/Steamless) | 移除 SteamStub 壳 | CC BY-NC-ND 4.0 |
| [SteamAPICheckBypass](https://github.com/SteamAutoCracks/Steam-auto-crack) | 隐藏模拟器痕迹 | MIT |

来源与许可证详情见 [docs/THIRD-PARTY-NOTICES.md](docs/THIRD-PARTY-NOTICES.md)。

## License

[GPL-3.0](LICENSE)。
