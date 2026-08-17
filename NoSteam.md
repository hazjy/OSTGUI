# NoSteam 模块开发总结

## 概述

本文档记录 OSTGUI 中"免Steam运行"（NoSteam）功能的开发历程、技术决策和当前状态。

---

## 架构设计

### 组件关系

```
OSTGUI (WinUI 3, net10.0)
    │
    ├── ProjectReference → NoSteamLauncher (Library, net8.0)
    │
    └── 运行时机制:
        ├── NoSteamLauncherService (包装器)
        │   └── 解压嵌入资源到 %TEMP%
        │
        └── NoSteamLaunchOrchestrator (编排器)
            ├── SteamlessService → Steamless.CLI.exe (v3.1.0.0)
            └── GBEDeploymentService → Goldberg Emulator (GBE)
```

### 目录结构

```
D:\Projects\
├── OSTGUI\                    # 主程序 (WinUI 3, .NET 10)
│   └── OSTGUI\
│       ├── Pages\NoSteamPage.xaml(.cs)   # UI 页面
│       ├── ViewModels\NoSteamViewModel.cs  # 业务逻辑
│       ├── Services\NoSteamLauncherService.cs  # 服务包装层
│       └── Models\AppConfig.cs          # 配置持久化
│
├── NoSteamLauncher\           # 独立库 (net8.0, 独立 git)
│   ├── Models\LaunchOptions.cs      # 启动选项模型
│   ├── Services\
│   │   ├── INoSteamLauncherService.cs  # 接口定义
│   │   ├── NoSteamLaunchOrchestrator.cs  # 编排器
│   │   ├── SteamlessService.cs      # SteamStub 脱壳
│   │   └── GBEDeploymentService.cs  # GBE 部署
│   └── Resources\                 # 嵌入资源
│       ├── steam_api.dll / steam_api64.dll  # GBE DLL
│       ├── generate_interfaces_x64.exe    # 接口生成工具
│       ├── Steamless.CLI.exe          # 脱壳工具 v3.1.0.0
│       ├── Plugins\                # Steamless 插件
│       └── steam_settings.EXAMPLE\  # 配置模板
│
├── OpenSteamTool\           # 核心实现 (C++, CMake) - 不可删除
│   └── OpenSteamTool\
│       └── src\             # Hook/Steam/Pipe 等核心代码
│
└── RefProjects\             # 参考项目
    ├── goldberg_emulator\     # 原始 GBE (已停更)
    ├── gse_fork\            # GSE Fork
    ├── Steamless\           # Steamless 源码
    └── SteamAutoCracker\    # 一键破解工具
```

---

## 核心流程

### 部署流程

```
1. 用户选择游戏 EXE → 自动填充 AppID (读取 steam_appid.txt 或 appmanifest_*.acf)
2. 配置部署选项
3. 点击"开始部署"
   ↓
4. 验证参数
5. 执行编排器 ExecuteAsync()
   ├─ Step 1: Steamless 脱壳 (如果未跳过且游戏有 SteamStub)
   ├─ Step 2: GBE 部署 (DLL + 配置文件)
   └─ Step 3: 启动验证 (超时检查)
6. 输出日志到 UI
```

### GBE 部署细节

```
1. 检测游戏架构 (PE header 64/32位)
2. 部署 steam_api.dll / steam_api64.dll
   - UE 游戏: 同时部署到 Engine 目录 + 游戏目录
   - 其他游戏: 部署到游戏目录
3. 写入 steam_appid.txt (游戏目录 + steam_settings/)
4. 生成 steam_interfaces.txt
   - 优先: 运行 generate_interfaces_x64.exe
   - 回退: 复制模板文件
5. 全量复制 steam_settings.EXAMPLE/ 模板目录
6. 写入 dlc.txt (根据 DLC 列表)
```

---

## 技术选型

### 为什么使用 ProjectReference 而不是 NuGet

| 因素 | 说明 |
|------|------|
| 开发效率 | 直接引用，无需打包发布 |
| 版本同步 | 修改后立即生效 |
| 调试便利 | 可打断点调试两层代码 |
| 项目规模 | 较小库，无第三方依赖冲突 |

### 为什么 NoSteamLauncher 使用 net8.0

- .NET 8 是当前 LTS 版本，稳定可靠
- OSTGUI 使用 .NET 10，向后兼容没问题
- 避免与 Windows App SDK 1.6 的兼容性问题

---

## GBE 版本信息

### 当前使用版本

| 项目 | 信息 |
|------|------|
| **来源** | `gitlab.com/Mr_Goldberg/goldberg_emulator` |
| **分支** | `master` |
| **Commit** | `475342f` |
| **版本** | `0.2.5-516-g475342f` |
| **备注** | SDK 1.56/1.57 支持 |

### 版本状态

- ⚠️ **原始 GBE 已停更** (2023年5月后无更新)
- 🍴 社区 Fork `gbe_fork` (github.com/Detanup01/gbe_fork) 活跃维护
- 迁移成本高 (配置文件格式变更、构建系统不兼容)，暂不迁移

### Steamless 版本

| 项目 | 信息 |
|------|------|
| **版本** | v3.1.0.0 |
| **作者** | atom0s |
| **来源** | `github.com/atom0s/Steamless` |
| **支持** | SteamStub v1.0 ~ v3.1 全部变体 |

---

## 已知问题

### 1. Windows App SDK 1.6 + .NET 10 兼容性问题

**现象**: 构建时出现 MSB4062 错误，找不到 `Microsoft.Build.Packaging.Pri.Tasks.dll`

**影响**: 不影响运行时功能，仅构建阶段报错

**状态**: 已知问题，暂不处理 (TargetFramework 不能修改)

### 2. 资源提取路径解析

**问题**: `Resources.Plugins` 嵌套在 `Resources` 目录下，路径解析可能失败

**解决**: 已修复 `ExtractEmbeddedFolder` 方法，支持点号和反斜杠分隔符

---

## 未来改进方向

### 短期

- [ ] 完善 GBE 高级配置 UI (DLC 管理、账号设置等)
- [ ] 添加配置导出/导入功能
- [ ] 优化日志输出格式

### 中期

- [ ] 考虑迁移到 gbe_fork (需要评估兼容性)
- [ ] 支持更多游戏类型检测
- [ ] 添加部署预览模式

### 长期

- [ ] 集成 SteamAutoCracker 一键破解流程
- [ ] 支持云端配置同步
- [ ] 游戏兼容性数据库

---

## 相关资源

- GBE 官方仓库: `https://gitlab.com/Mr_Goldberg/goldberg_emulator`
- GBE Fork: `https://github.com/Detanup01/gbe_fork`
- Steamless: `https://github.com/atom0s/Steamless`
- SteamAutoCracker: `https://github.com/Demagog04/SteamAutoCracker`
- SteamDB: `https://steamdb.info/` (查询 AppID/DLC)

---

## 变更记录

| 日期 | 变更内容 |
|------|----------|
| 2026-08-17 | 初始版本，记录当前架构和状态 |
