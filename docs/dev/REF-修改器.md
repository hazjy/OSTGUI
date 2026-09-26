# REF-修改器（GUI 侧）

> 只收 修改器下载源与进程绑定；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26，2026-09-26 按领域重划）。

## 修改器下载与进程绑定（实测 + 实现事实，2026-09-25）

- **数据源**：`flingtrainer.com`（FLiNG 修改器）。
  - **搜索 = 站点官方 RSS**：`?s=<关键字>&feed=rss2`（WordPress 自带）→ 用 stdlib `XDocument` 解析，只留 `/trainer/` 条目。
    实测与页面结果区**一致**：elden 2/2、crimson 3/3、wukong 1/1（2026-09-25）；且 feed 只有正文条目，
    **侧栏"热门/最新/相关"进不来**——扒 HTML 时会：`?s=elden` 真结果 2 条却抓到 16 条、`?s=peak` 把 12 条推荐当结果。
  - 曾做过并实测可用的两个入口，2026-09-25 按需求砍掉（代码删了，需要时看 git 历史）：
    首页热门 `<a href=".../trainer/..." class="wpp-post-title">`（实测 6 条）、站点全局 RSS `/feed/`（20 条）。
- **附件直链**：详情页里含 `class="attachment-link"` 的 `a` 标签，`href` 是下载地址、`title` 就是文件名
  （如 `Elden.Ring.v1.02-v1.16.1.Plus.35.Trainer-FLiNG`）。**属性顺序不固定**（实测 href 在前、class 在后）
  → 先切整个标签再逐个取属性。
- **附件是 zip 且标题没有扩展名**：只能按内容嗅探（开头 `PK`）判断，是 zip 就解压到同名目录、取里面的 exe；
  解压做 zip-slip 校验。
- **站点是间歇性的**：同一条 PowerShell 命令时而 HTTP 200，时而"基础连接已经关闭" → 抓取必须重试一次，
  失败要兜住（页面里"抓取失败"文案），日志留 HTTP 状态与页面长度。
- **不引第三方 HTML 解析库**：本机 nuget 不通，加不了 HtmlAgilityPack（Fluent-Steam-Lua 用的是它）
  → 只用 `Regex` 锚定上面那几个固定标记。
- **进程绑定语义**（与 FSL 的 `SvcMonitor` 对齐，但不单独建工程/装服务）：
  - 绑定 = `{AppId, GameName, GameExePath, TrainerFilePath, IsEnabled}`，落 `%LOCALAPPDATA%\OSTGUI\trainers\bindings.json`；
  - 监控 = **同 exe 子进程** `OSTGUI.exe --trainer-monitor`（与 `--stats-*` 并列的早退分支）：每 2 秒轮询，
    游戏进程在 → 启动修改器；游戏退出 → **只结束自己启动过的那个**；没有启用的绑定就自退；
  - 单实例靠 `Global\OSTGUI_TrainerMonitor` 互斥体；pid 写 `monitor.pid`，GUI 关闭开关时据此主动结束它；
  - 下载完**不自动执行**：只有用户点「启动」或（绑定启用 + 监控开启）时才运行。
- **实测**：`--trainer-monitor` 手动跑过 —— 无绑定时自退 `exit 0`，日志有"没有启用的绑定，退出"。
- **未做（有意）**：封面图、自动按键（FSL 的 AutoKeys）、解析修改器 exe 取功能列表、多源（只 FLiNG）、开机自启/服务安装。
