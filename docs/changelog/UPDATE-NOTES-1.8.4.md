# OSTGUI v1.8.4 —— 新增清单源 Hubcap

> 适用版本：v1.8.4（对比 v1.8.3）

## 新增

- **清单源 Hubcap**：按 depot+gid 要单份清单，服务端现去 Steam 取，所以**旧分支版本也能取到**（实测 CS2 `1.40.8.8`、BG3 `patch6_hf9` 均正常）；单份额度 1500 次/日。
  - 需要 API key：在 <https://hubcapmanifest.com/> 生成，Discord 社区 <https://discord.gg/hubcapsmanifest>。
  - 填在设置页「清单源 → Hubcap」，那一行会显示「上次更新」时间，便于留意 key 的有效期。
- **级联顺序**：MHub（配了 key 时优先）→ Hubcap（兜底）→ Sudama（只出 Lua）。任一家可用即可入库，入库日志会打印本次实际级联顺序。

## 修复与改进

- **移除 Ryuu's Manifests**：站方下线了"按 depot 取清单"的接口（现在任何请求都返回 404），整包接口暂不接入，因此整源下架；配置里的残留条目会在启动时自动清理并记一行日志。

## 升级方式

- 覆盖旧版程序目录即可（新旧可以共存）。
- 内核无需更新。
- 首次启动日志出现 `清理清单源残留（已下线或自定义）：ryuu` 属正常；要用 Hubcap 请到设置页填入 API key。

---

[![](https://img.shields.io/badge/powered_by-dsh-4D6BFE?style=flat-square&logo=deepseek&logoColor=white)](https://github.com/deepseek-ai/deepseek-harness)
