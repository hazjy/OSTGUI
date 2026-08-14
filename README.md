# OSTGUI

OpenSteamTool 可视化管理工具（Windows / WinUI 3）。

OSTGUI 是 [OpenSteamTool](https://github.com/OpenSteam001/OpenSteamTool) 的桌面图形界面：搜索并入库游戏、自动生成解锁 Lua 配置、补齐 depot 解密密钥与访问令牌、管理已入库游戏、处理 Denuvo 授权与联机启动。

> ⚠️ 本项目仅供学习与交流。请支持正版，购买你玩的游戏。

## 功能

- **搜索入库**：按游戏名称 / AppID / Steam 链接搜索，一键生成完整 Lua 配置并写入 `Steam/config/lua/`
- **多源清单**：ManifestHub（需 API Key）→ GitHub → Sudama 兜底，任一成功即完成
- **密钥与令牌**：Sudama 全量密钥缓存（24h TTL + 手动刷新），入库自动补齐 depot key / access token
- **DLC 支持**：可选"添加所有 DLC"，自动追加 DLC 的 addappid 与 addtoken
- **固定版本配置**：可选预写注释形式的 `setManifestid`，备用不启用，随时可在库页切换
- **入库管理**：扫描已入库游戏、编辑 Lua、复制 AppID / 游戏名、查看入库信息、切换版本模式（自动 / 固定）
- **Denuvo 授权**：.ost 授权文件导入 / 导出 / 在线提取
- **480 联机**：以 Spacewar(480) 身份启动已入库游戏，启用 Steamworks 联机
- **其他**：浅色 / 深色 / 跟随系统主题、入库结果系统通知、运行日志（可复制 / 查看文件）

## 界面

主页 / 搜索入库 / 入库管理 / 联机 / D加密授权 / 重启 Steam / 设置。

## 环境要求

- Windows 10 19041（20H1）及以上，x64
- Steam 客户端（可自动检测安装路径）
- OpenSteamTool 内核已注入 Steam（主页会显示关键 DLL 状态）

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/) 与 Visual Studio 2022+（含 WinUI 工作负载）。

```bat
REM 直接构建（Debug）
build.bat

REM 自包含发布（Release，产物在 bin\Release\...\publish）
MSBuild OSTGUI\OSTGUI.csproj /t:Publish /p:Configuration=Release ^
  /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:WindowsAppSDKSelfContained=true
```

## 项目结构

```
OSTGUI/
├─ Pages/        界面（主页 / 搜索 / 入库管理 / 联机 / 授权 / 设置）
├─ ViewModels/   MVVM 视图模型
├─ Services/     入库、清单、密钥缓存、Lua 生成、Steam 交互等
├─ Models/       数据模型（清单源、入库项、授权条目等）
├─ Helpers/      转换器等辅助
└─ Assets/       内置资源（重要说明页等）
```

## 清单源

详见 [SOURCES.md](SOURCES.md)。

## 免责声明

本项目仅用于技术学习与研究，不包含任何游戏文件、清单文件或受版权保护的内容。密钥与令牌来自公开的第三方数据源，请自行判断其合规性。使用本项目产生的一切后果由使用者自行承担。

## License

[GPL-3.0](LICENSE)。
