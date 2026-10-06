# OSTGUI

Steam 本地入库与解锁的图形界面（Windows 10/11 · WinUI 3 · 原生编译）。


OSTGUI 是 [ZSteamTool](https://github.com/hazjy/ZSteamTool) 内核的图形界面：内核注入 Steam，接管所有权、
depot 解密密钥与清单请求；OSTGUI 负责搜索入库、生成 Lua 配置、管理已入库游戏，以及联机启动与免 Steam 部署。
本仓库不含内核代码，也不含任何游戏文件。
## 功能

- **搜索入库**：按游戏名称、AppID 或 Steam 链接搜索。清单取自 MHub（需 API Key），密钥与令牌取自 Sudama；
  可选「添加所有DLC」「写入固定版本配置」「下载 Manifest」，过程可取消。
- **入库管理**：列表 / 网格浏览与过滤，切换版本模式、补齐版本配置、编辑 Lua、复制 AppID 与名称、删除。
- **成就**：编辑成就解锁状态，先写本地留底，再手动「保存到 Steam」（需 Steam 在线）。
- **修改器**：搜索并下载风灵月影修改器，可绑定游戏进程，随游戏启停 **（GUI关闭时也有效）** 。
- **联机**：三条路线——内核原生、DLL 注入（推荐）、AppID Changer。
- **Denuvo**：授权文件（`.ost`）导入与导出，导出需 Steam 在线且账号拥有该游戏。
- **模拟器**：免 Steam 部署（Steamless 脱壳 + Goldberg 模拟器）与免育碧（实验性），均支持一键还原。
- **其它**：浅色 / 深色主题与云母 / 亚克力效果、运行日志、检查更新、重启 Steam。


## 环境要求

- Windows 10 20H1（19041）及以上，x64
- Steam 客户端（安装路径自动检测）
- 内核 [ZSteamTool](https://github.com/hazjy/ZSteamTool) 已部署到 Steam 根目录（三个 DLL）
- 无需安装 .NET 运行时

## 快速开始

1. 按 ZSteamTool 的说明部署内核三个 DLL（覆盖前先备份原文件）。
2. 下载发布包 `OSTGUI-v<版本>.zip`，解压到任意目录，运行 `OSTGUI.exe`。
3. 在设置页「清单源」填入 MHub API Key——不填只能拿到密钥与令牌，下载不到清单。

配置与缓存位于 `%LOCALAPPDATA%\OSTGUI`，升级时解压覆盖程序目录即可。

## 构建

需要 **.NET 10 SDK** 与 **Visual Studio 2022 / 18（含 WinUI 工作负载）**。构建走 VS 自带的 MSBuild：

```bat
build-jit.bat        :: Debug 构建
build-jit.bat /r     :: 构建成功后自动启动
```

产物在 `.build\OSTGUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\`，完整日志 `%TEMP%\ostgui_build.log`。

## 发布

```bat
build-aot.bat
```

Native AOT 发布，产物在 `.build\OSTGUI\publish-aot\`（约 97 MB、250 余个文件，打包后约 36 MB）。
脚本会自检 AOT 未被跳过、宿主可独立运行；restore 与 publish 是分开的两次调用，
VS 的 MSBuild 路径自动探测（VS 18 / 17 × Community / Professional / Enterprise）。

## 项目结构

```
main/                WinUI 3 主程序（Pages / Views / ViewModels / Services / Models / SteamApi）
NoSteamLauncher/     免 Steam 部署类库，第三方二进制以 EmbeddedResource 内嵌
OnlineHost/          联机宿主，独立 exe，随 GUI 发布
docs/                changelog（逐版本更新说明）与 dev（开发文档）
```

## 文档

| 内容 | 位置 |
|---|---|
| 架构、服务索引、已知限制 | `docs/dev/DEV-NOTES.md` |
| 各领域机制与实测结论 | `docs/dev/REF-*.md` |
| 逐版本改动 | `docs/changelog/` |
| 第三方组件与许可 | `docs/dev/THIRD-PARTY-NOTICES.md` |

## 免责声明

本项目仅供技术学习与研究，不包含任何游戏文件、清单文件或受版权保护的内容。
请支持正版，购买你玩的游戏；使用本项目产生的一切后果由使用者自行承担。

## License

[GPL-3.0](LICENSE)。
