# OSTGUI

Steam 本地入库与解锁的桌面图形界面（Windows / WinUI 3）。

OSTGUI 为配套内核 **[ZSteamTool](https://github.com/hazjy/ZSteamTool)** 提供图形界面：搜索并入库游戏、自动生成解锁 Lua 配置、补齐 depot 解密密钥与访问令牌、管理已入库游戏、D 加密模式切换、联机启动（Spacewar 480 / 自定义会话身份），以及一键免 Steam 启动（自动脱壳 + Goldberg 模拟器部署）。

内核关系：**[ZSteamTool](https://github.com/hazjy/ZSteamTool)** 以 [OpenSteamTool](https://github.com/OpenSteam001/OpenSteamTool) 为基础做定制发行（D 加密模式、联机会话身份、清单投喂等改动）；本仓库只负责图形界面，不含内核代码。

> ⚠️ 本项目仅供学习与交流。请支持正版，购买你玩的游戏。

## 功能

- **免 Steam 启动**：选择游戏 EXE 一键完成部署，之后无需启动 Steam 直接游玩
- **搜索入库**：按游戏名称 / AppID / Steam 链接搜索，一键完成入库
- **多源清单**：MHub 清单源 + Sudama 密钥源级联，任一成功即完成入库
- **密钥与令牌**：Sudama 全量密钥缓存，支持应用内手动刷新与本地文件导入，入库自动补齐
- **DLC 支持**：可选"添加所有 DLC"
- **入库管理**：扫描已入库游戏、编辑配置、复制 AppID / 游戏名、切换版本模式
- **Denuvo 授权**：.ost 授权文件导入 / 导出 / 在线提取
- **联机启动**：以 Spacewar(480) 或自定义会话身份启动已入库游戏，启用 Steamworks 联机
- **其他**：浅色 / 深色 / 跟随系统主题、入库结果系统通知、运行日志（可复制 / 查看文件）

## 界面

主页 / 搜索入库 / 入库管理 / 联机 / D加密授权 / 免Steam(S.A.C) / 重启 Steam / 设置。

## 环境要求

- Windows 10 19041（20H1）及以上，x64
- Steam 客户端（可自动检测安装路径）
- [ZSteamTool](https://github.com/hazjy/ZSteamTool) 内核已部署到 Steam 根目录（v1.1.0+；主页会显示三个关键 DLL 的状态）

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

内置 **MHub**（清单源，需 API Key）与 **Sudama**（密钥源）两个已接入源：MHub 负责下载清单文件，Sudama 提供 depot 密钥与访问令牌；入库时按 MHub → Sudama 级联，任一成功即完成。历史其余 6 个预置源（SAC 分流、Walftech、SteamAutoCracks V2、清单不求人、GitHub (Auiowu)、自动搜索 GitHub）及 GitHub 分支清单模式已于 v1.3.x 清理，详见 `docs/dev/` 相关文档。

## 免责声明

本项目仅用于技术学习与研究，不包含任何游戏文件、清单文件或受版权保护的内容。密钥与令牌来自公开的第三方数据源，请自行判断其合规性。使用本项目产生的一切后果由使用者自行承担。

免 Steam 部署功能随仓库分发了以下第三方编译组件：

| 组件 | 用途 | 许可证 |
|---|---|---|
| [GSE 模拟器（gbe_fork）](https://github.com/Detanup01/gbe_fork) | Steam API 本地模拟 | LGPL-3.0 |
| [Steamless](https://github.com/atom0s/Steamless) | 移除 SteamStub 壳 | CC BY-NC-ND 4.0 |
| [SteamAPICheckBypass](https://github.com/SteamAutoCracks/Steam-auto-crack) | 隐藏模拟器痕迹 | MIT |

来源与许可证详情见 [docs/dev/THIRD-PARTY-NOTICES.md](docs/dev/THIRD-PARTY-NOTICES.md)。

## License

[GPL-3.0](LICENSE)。
