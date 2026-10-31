# REF-日志与诊断（GUI 侧）

> 只收日志流、文件侧时序与崩溃钩子；分工与文档地图见工作区根 `README.md`。

## 日志系统（单一日志流）

- `Diag()`：启动退出、异常、外部失败、关键状态变化；写文件 + 进运行时日志（视图里带 `[诊断]` 前缀）。
- `Event()`：只进运行时日志、不落盘；流水账（手动启动、解压细节、`[Hover]`、`[Cover]`、stats 进度、搜索无结果、取消）。两者行为完全一致，两个名字只为让调用处读得出语义（异常 / 失败 vs 流水）。
- `Fatal(context, ex)`：三条崩溃钩子（UI 线程 / AppDomain / `TaskScheduler.UnobservedTaskException`）→ 文件写 `Exception.ToString()` 全栈 + `FATAL`，视图只留一行摘要。
- 文件格式 `[yyyy-MM-dd HH:mm:ss.fff] [p<pid>] [D] msg`（毫秒 + pid，便于对齐多进程）。
- 追加写用 `FileStream` + `FileShare.ReadWrite` + 失败重试一次，GUI / 监控 / stats 子进程共用同一份；`AppendAllText` 在文件被占时会抛 `IOException` 并被 `catch { }` 吞掉＝真丢行。
- 裁剪按行数：`LogMaxLines`（默认 1000）就是硬上限，超出即重写为最后若干行，不留 `.1` / `.2` 备份。
- 任何入口点都有日志：`LogService.AppendFile()` 内部兜底 `Initialize(DefaultPath)`（自写入口 `Program.Main` 不经过 `App` 构造函数）。
- 子进程没有视图，其行只能进文件。

## 有意不做

- 不引第三方日志库。
- 不加级别过滤 UI。

## 读日志的坑

- **`Get-ChildItem` 报的大小不可信**：被进程持有的文件（内核 `opensteamtool\*.log`、Steam 的 `logs\*.txt`）目录项长度不刷新——实测 `package.log` 显示 0 字节实际 9 KB、`main.log` 显示 2335 实际 12 KB。判"日志空不空"必须 `(Get-Content $p -Raw).Length` 真读，否则会得出"一条日志都没写"的错误结论。
- **"下载失败"的诊断顺序**：先看 Steam 自己的 `logs\connection_log.txt`（连通性测试，`Got HTTP 502` / `ConnectFailed` 都在这）与 `logs\content_log.txt`（`Failed to get manifest request code` 之类原文），再看内核 `opensteamtool\*.log` 与脚本。代理抽风的表现就是"下载失败"，不是"网络错误"。
