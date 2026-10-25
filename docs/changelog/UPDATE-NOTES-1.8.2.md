# OSTGUI v1.8.2 —— 内置 Lua 编辑器与清单源扩容

> 适用版本：v1.8.2（对比 v1.8.1）

## 新增

- **内置 Lua 编辑器**：库页「编辑 Lua」不再调用记事本，新增应用内Lua编辑器。支持语法高亮、行:列状态栏、快速插入 `addappid` / `addtoken` / `setmanifestid`、一键注释与取消注释；
- **新增清单源 Ryuu's Manifests**
- **清单源可单独开关、单独测活**：现在可以单独开关清单源并测试可用性

## 修复与改进

- **不再把坏清单写进 depotcache**：清单落盘前校验格式，非法的manifest会被丢弃并从下一个源重新获取

## 升级方式

- 覆盖旧版程序目录即可（新旧可以共存）。
- 内核无需更新。

---

[![](https://img.shields.io/badge/powered_by-dsh-4D6BFE?style=flat-square&logo=deepseek&logoColor=white)](https://github.com/deepseek-ai/deepseek-harness)
