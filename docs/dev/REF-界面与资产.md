# REF-界面与资产（GUI 侧）

> 只收 显示效果（无/云母/亚克力）与封面图等美术资源链路；分工与文档地图见工作区根 `README.md`。
> 来源：从 `doc/GUI-事实考证.md` 拆出（2026-09-26）。

## 显示效果（无 / 云母 / 亚克力）实测（2026-09-19）

- 实现方式：给 `Window.SystemBackdrop` 赋 `null` / `MicaBackdrop` / `DesktopAcrylicBackdrop`（WASDK 1.6）；
  设置页下拉写 `config.json` 的 `BackdropMode`，默认 `acrylic`。
- **关键结论：`SystemBackdrop = null` 的窗口底色跟随的是「系统主题」，不是「应用主题」**。
  修复前在「浅色应用主题 + 深色系统」下采样到 `#808080` / `#D9D9D9`，导航栏处直接露 `#000000`
  （等于浅色界面压在一层深色底上，观感发灰发脏）。修法 = `RootGrid` 里铺一层
  `{ThemeResource SolidBackgroundFillColorBaseBrush}` 的 `SolidBackdrop`，只在「无」档显示（浅 `#F3F3F3` / 深 `#202020`）；
  云母与亚克力则跟随应用主题，无需干预。
- 亚克力的实测观感：壁纸色块明显透出且重度模糊（深浅两套主题都成立）；云母只是极淡的色调偏移
  ——两者肉眼可区分，截图文件体积也差一档（同窗口 138 KB / 340 KB / 825 KB）。
- 系统要求：云母 Win11 22000+、亚克力 Win11 22621+；不支持时框架静默回落纯色底（不抛异常）。
- 三档采样色值表（本机 Windows 11 build 26200 / 25H2，窗口 2398×1695 @2.25x，窗口内三处背景像素）见 `REF-归档-测试与采样.md`。

## 封面图（Steam 美术资源，查证 2026-09-21）

- 两套 CDN 布局并存：旧 `cdn.cloudflare.steamstatic.com/steam/apps/<id>/header.jpg`（460×215，覆盖多数老游戏）；
  新 `shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/<id>/...`
  （会 302 到 `shared.steamstatic.com`，`HttpClient` 默认跟随重定向）
- **2024+ 新上架游戏在旧布局下 `header.jpg` / `capsule_616x353.jpg` / `capsule_231x87.jpg` / `library_600x900.jpg` 全是 404**
  （实测 PEAK 3527290、3548580、4001890），且新布局的猜测路径同样 404 → **靠拼 URL 拿不全，必须用官方接口**
- 官方源两条：`store.steampowered.com/api/appdetails?appids=<id>` 的 `data.header_image` / `capsule_image` / `capsule_imagev5`
  （**无需 API key**，本仓库已在用）；`api.steampowered.com/IStoreBrowseService/GetItems/v1` + `include_assets:true`
  可批量取全套 `assets.*`（含 `library_600x900` 立绘，本仓库未采用）
- **本机 hosts 把 `cdn.akamai.steamstatic.com` / `store.akamai.steamstatic.com` / `steamcdn-a.akamaihd.net` /
  `media.steampowered.com` / `steamcommunity.com` / `store.steampowered.com` / `api.steampowered.com` 指向 127.0.0.1**：
  官方接口给老游戏返回的图片 URL 常落在 akamai 主机上 → 需换等价 cloudflare 主机重试
  （`SteamGameInfoService` 走系统代理时官方 API 仍可达，故这条兜底有效）
- Steam 自己的本地美术缓存 `appcache\librarycache\<appid>\` 只有部分游戏有（BG3 有 `library_600x900.jpg`/`logo.png`，
  PEAK 只有一个 733 字节小图）→ **不能当兜底源**
- **`librarycache\<appid>\header.jpg` 与 CDN 下到的是同一张图（逐字节完全相同，12/12 命中样本，均 460×215）**，
  所以"优先读本地"只省网络请求、省不了磁盘；覆盖率也低 → **2026-09-21 评估后未采用**（真正省盘的是按显示尺寸重编码）
- 已知本来就没有封面的条目：`1716751`（育碧组件）——判定为"缺失"属正常
- **取值链**（`CoverImageService`，信号量并发 4）：① 旧布局 `cdn.cloudflare.steamstatic.com/steam/apps/<id>/header.jpg`
  ② 新布局 `shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/<id>/header.jpg` ③ 同两处的 `capsule_616x353.jpg`
  ④ **官方 appdetails** 的 `header_image` → `capsule_image` → `capsule_imagev5`（仅 ①–③ 全失败时调用，8s 超时）；
  官方 URL 若落在被封主机上，自动换成等价 cloudflare 主机重试一次
- **2024+ 新上架游戏封面 URL 猜不出来、必须问官方**：旧/新两套 CDN 布局逐条试全 404（PEAK 3527290 / 3548580 / 4001890 实测）。
- **封面补链**：静态模板 4 条（旧/新布局 × header/capsule）+ 官方 `appdetails` 的 `header_image → capsule_image → capsule_imagev5`。
- **搜索页缩略图走同一套来源**（`HeaderTemplates` 两条 header 布局 → 官方 `GetHeaderImageUrlAsync` → null 占位），
  但**只走内存、刻意不落盘**（不调 `EnsureCoverFileAsync`）；并发 4，卡片 `Stretch="Uniform"` 作保险
- **`SearchByAppIdAsync` 根本不填 `ImageUrl`** → 搜索缩略图必须有官方兜底，否则按 AppID 搜出来永远没图。
- **缩略图比例**：storesearch 的 `tiny_image` 231×87（2.66:1）vs 本项目卡片 2.14:1（120×56）
  → `UniformToFill` 会裁两侧；改用 header 源 + `Uniform` 留边。
- **缓存与缺失标记**：`%LOCALAPPDATA%\OSTGUI\covers\<appid>.jpg`（命中不联网）；确无图的写 `<appid>.miss2`（TTL 1 天）。
  ⚠️ **改 URL 链 / 兜底源时必须把 `MissSuffix` +1**，否则旧标记会在 TTL 内挡住新逻辑——2026-09-21 的"封面永远出不来"就是这么来的
- **落盘尺寸 = 460×215**（`StoreWidth` = `header.jpg` 原生尺寸）+ JPEG q85，编码用 `System.Drawing.Common`。
  **不能再按列表尺寸存**：225% DPI 下网格图框要 ~450 物理像素，曾经的 240×112 塞进网格就是 1.9× 放大发糊。
  实测 39 张：240 时代 361 KB / 平均 9.3 KB → 改后 **1,103 KB / 平均 29.0 KB / 最大 45.6 KB**（1000 个游戏约 29 MB）。
  ⚠️ **卡片再调大时改 `StoreWidth`，并改名 `MigrationMarker`（当前 `.v3`）**触发一次性迁移；
  迁移是"删掉比 `StoreWidth` 窄的旧图、只删不放大"（把 240 插值到 460 是伪造清晰度）
- **判定语义**：只有 404/403 才算"确实没有"；超时 / 5xx / 网络异常**不写标记**，下次重试。
  **官方接口的两种失败必须分开**：`GetHeaderImageUrlAsync` 内部重试 1 次仍失败就**抛异常**
  （调用方按"接口暂时不可用"处理 → 不写标记）；只有"应答正常但没有图片字段"才返回 null（= 确实没有 → 写 `.miss2`）。
  原实现把两者都当 null，一次偶发失败就把游戏变成 1 天空白——2026-09-21 修（同类坑第二次）
- **覆盖统计**（本机 302 个 appid 目录 / 入库 39 个游戏）：118 个有 `header.jpg`、51 个有 `library_600x900.jpg`、
  145 个只有 <5KB 小图，入库游戏仅 12 个命中（31%）。
- **按需加载**：列表 `ListView` / 网格 `GridView` 共用同一个钩子 `ContainerContentChanging`（`GridView` 也继承 `ListViewBase`），
  滚进视口才取图 / 建位图，滚出回收后不重复取。实测进页面只取 20 张（视口 + 缓冲），滚到底累计 33 张。
  ⚠️ 换成 `ItemsControl` 等非虚拟化容器会让这条机制彻底失效
