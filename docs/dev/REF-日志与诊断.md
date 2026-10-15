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
