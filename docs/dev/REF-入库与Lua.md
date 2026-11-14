# REF-入库与Lua（GUI 侧）

> 何时读：写 / 改 Lua 语义、搜索与入库链路、应用内 Lua 编辑器时。
> 管什么：Lua 各行的语义与参数规则、入库链路的实现契约、编辑器事实。
> 不写什么：清单源、密钥缓存与请求码状态见 `SOURCES.md`；清单投喂与固定版本见 `REF-清单与版本.md`；AOT 约束见 `REF-AOT适配.md`。

## Lua 配置语义（内核查证）

```lua
addappid(2001760)                     -- 解锁主游戏（假装拥有）
addappid(2001761, 1, "64位hex")       -- 解锁 depot + 注入解密密钥
addtoken(2001760, "令牌")              -- 受限 app 取 appinfo 需要
setManifestid(2001761, "gid", 大小)    -- 固定版本（锁 depot 的 manifest）
```

- `addappid` 第二参数内核实际忽略（只读第 1、3 参数），密钥必须恰好 64 字符否则不生效；DLC 用 `addappid(dlcId)` 只解锁不带密钥（多数复用本体 key）。
- `--` 开头是注释、函数名不区分大小写，文件放 `config\lua\{AppId}.lua` 且内核热重载；`setManifestid` 有则锁版本、无则自动更新，大小可省略（内核把 size 强制为 0）。
- **缺 `addtoken` 的后果**：内核只对 Lua 里有该 appid `addtoken` 的项注入 token（`Hooks_NetPacket_AccessToken`，未注入打 `skip appid=… in depot, no token configured`），受限 app 因此拿不到 appinfo。**下载**靠 manifest + depot key + 请求码可正常完成；**安装/启动**需要 appinfo 给出 depot 列表与 `installdir`，社区反馈时报「安装路径无效」，本机尚未复现。排查顺序：Lua 有无 `addtoken` → Debug 内核 `pics.log` 的 `skip appid=` → 同机普通免费游戏是否同样报错。
- `steamtools.lua` 不由本程序维护：入库 / 补全只写 `<AppId>.lua`；删除入库只清理该文件里旧版留下的 `addappid` 引用。

## 搜索与入库链路（GUI 侧实现契约）

- 搜索统一走 `cc=us`（成人内容不再被 cn 区过滤），能搜到 ≠ 能入库；清单源与密钥源分离：MHub 清单源、Sudama 仅密钥、Ryuu 清单点播，各源状态见 `SOURCES.md`。
- 入库勾选「下载 Manifest」（默认开）：MHub → Sudama（仅密钥）级联；不勾则跳过清单直接生成 Lua，由内核运行时兜底取清单；清单投喂的两处 depotcache 双写见 `SOURCES.md`。
- `LuaBuilder` 顺序：Sudama 密钥 / 令牌 → `MergeAllDepotsAsync` 用全量 depot 补全（防漏密钥）→ 缺密钥收集并通知；DLC 段查 `keys[dlcId]` 写 `addappid(dlcId, 1, key)`，缺解密密钥在两种模式下都警告。
- 取消纪律：`catch (OperationCanceledException)` 必须带 `when (ct.IsCancellationRequested)` 过滤（`HttpClient` 超时也抛 `TaskCanceledException`，不过滤会把超时当用户取消、吃掉兜底）；每个网络 / 等待点都要把 `ct` 传下去，入库期间禁止第二个任务（`IsAdding`）；取消立即生效，只有两处原子写（`tmp + Move`）不可打断。
- DLC 名数据源：steamcmd 对 DLC 返回空壳（无 `name`），必须回退官方 `appdetails` 取名（本机约 8 s 截断）。

## 应用内 Lua 编辑器（2026-10-24）

- 入口：库页「编辑 Lua」→ `ContentDialog` 内嵌 `Views/LuaEditorView`（原 `notepad.exe` 已删除）；控件为 `RichEditBox` + 自写词法 `Services/LuaHighlighter.cs`（四色、深浅两套），不用第三方控件、不引 WebView2。
- 撤销：关掉 `RichEditBox.UndoLimit`，改 `Services/EditorHistory.cs` 整段文本快照（逐 token 上色会污染原生撤销队列）；换行与编码由 `Services/LuaTextCodec.cs` 唯一转换（模型 `\n`、编辑器 `\r` 段分隔、写盘还原原风格，UTF-8 无 BOM）。
- 保存后由内核 `LuaFileWatcher` 热重载；本文件不维护 `steamtools.lua`，`manifest.lua` 也不归它管；快速插入模板取自 `ZSteamTool/src/Utils/Config/LuaConfig.cpp:513-526`。
- 未做：行号槽、查找替换、自动补全。
