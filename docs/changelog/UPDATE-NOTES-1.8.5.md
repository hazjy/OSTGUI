# OSTGUI v1.8.5 —— 入库提示与界面细节

> 对比 v1.8.4

## 新增

- **用户手册**：`docs/guide/`，安装、入库、库与启动、免 Steam 部署、联机、成就与修改器、设置、故障排查、文件位置共 9 章。

## 修复

- **入库时的「缺密钥」提示不再误报**：macOS / Linux 专属的 depot、以及别的应用共享的公共库 depot 都不再计入。这些内容在 Windows 上根本不会被下载。实测红色沙漠：提示从 10 个降到 4 个，剩下的都是确实缺密钥的 Windows 语言包。
- **授权页「重要说明」重写**：只留事实与操作步骤，去掉冗长铺垫。

## 改进

- 窗口顶部留白 48 → 40，界面整体上移一点。
- 「Denuvo」「联机」页顶部的分段切换高度统一到 32，与「模拟器」「修改器」页一致。

## 升级方式

- 覆盖旧版程序目录即可。
- 内核无需更新。

---

[![](https://img.shields.io/badge/powered_by-dsh-4D6BFE?style=flat-square&logo=deepseek&logoColor=white)](https://github.com/deepseek-ai/deepseek-harness)
