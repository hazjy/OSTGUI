# OSTGUI v1.8.3 —— 清单源修复

> 适用版本：v1.8.3（对比 v1.8.2）

## 修复

- **Ryuu 清单源恢复可用**：该站点下线了旧的查询式接口（无论带不带密钥都返回 404），程序改用路径式接口取值。上一版用该源入库时，每个 depot 都会失败。
- **接口地址改不动的问题**：内置清单源的名称、接口地址、顺序与能力标记改为始终以程序为准，不再被配置文件里的旧值覆盖；配置里已下线源的残留条目会在启动时清理，并写一行日志。
- **配置文件瘦身**：界面用的派生状态不再写入 `config.json`。

## 改进

- 设置页「清单源」：密钥源（Sudama）不再显示无效的开关；密钥输入框的提示按源区分（MHub 未配置密钥则不使用，Ryuu 可不填）。

## 升级方式

- 覆盖旧版程序目录即可（新旧可以共存）。
- 内核无需更新。

---

[![](https://img.shields.io/badge/powered_by-dsh-4D6BFE?style=flat-square&logo=deepseek&logoColor=white)](https://github.com/deepseek-ai/deepseek-harness)
