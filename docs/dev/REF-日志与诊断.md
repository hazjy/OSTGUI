# REF-日志与诊断（GUI 侧）

> 只收 日志双通道拆分 / 文件侧时序 / 崩溃钩子；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26，2026-09-26 按领域重划）。

## 日志系统拆分（2026-09-26）

**背景**：原来 `AddAppLog` 只写文件（60 处，全是 trainer/stats/hover 流水账）、`AddLog` 只进内存视图（45 处，搜索/入库/联机），
两边互不可见——文件成了流水账，而崩溃诊断信息又常常不在应用里看得到。

**拆成两条通道**：
- `Diag()`：写文件 + 进运行时日志（视图里带 `[诊断]` 前缀）。启动退出、异常、外部失败、关键状态变化
  （实测最终 Diag 64 处 / Event 40 处）。
- `Event()`：只进运行时日志，不落盘。流水账（手动启动、解压细节、`[Hover]`、`[Cover]`、stats 进度、搜索无结果、取消）。
- `Fatal(context, ex)`：三条崩溃钩子（UI 线程 / AppDomain / 新增 TaskScheduler.UnobservedTaskException）
  → 文件写 `Exception.ToString()` 全栈 + `FATAL`，视图只留一行摘要。

**文件侧改动（实测验证）**：
- 格式 `[yyyy-MM-dd HH:mm:ss.fff] [p<pid>] [D] msg`（毫秒 + pid，便于对齐多进程）。
- 追加写改 `FileStream` + `FileShare.ReadWrite` + 失败重试一次。原来用 `AppendAllText`，
  监控/stats 子进程占着文件时抛 `IOException` 被 `catch { }` 吞掉＝**真丢行**。
- 裁剪改**按大小轮转**（>2 MB → `ostgui.1.log` → `ostgui.2.log`）。
  原来 `TrimFileToMaxLines` 每 50 条"读全文件 + 重写"，既 O(n) 又与另外两个进程抢文件。
- 验收实测：撑到 2.15 MB 后触发一次写入 → `.1` 保留 2.15 MB、主日志重新起 2 行 ✓；
  并发跑自检 + 监控 → 两进程各自的 pid 行都在 ✓。

**踩到的坑（方案 B 引入，已修）**：把 `--trainer-monitor` 挪到自写入口点（`Program.Main`）后，它不再经过 `App` 构造函数，
而 `LogService.Initialize()` 在那儿——监控的日志被**静默丢掉**。现在 `LogService.AppendFile()` 内部兜底
`Initialize(DefaultPath)`，任何入口点都有日志。

**有意不做**：不引第三方日志库、不加级别过滤 UI；子进程没有视图，其行只能进文件。
