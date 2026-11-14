# OSTGUI v1.8.7 —— 免Steam 部署与界面调整

> 对比 v1.8.6

## 更改

- **免Steam 部署**：删掉「试运行」开关；现在每次部署都会先把原 EXE 备份成 `.bak`（已存在则保留，不覆盖）。部署与还原结束后会弹应用内通知告诉你结果。
- **设置页**：日志区标题改为「日志」，文本框高度固定为 300；「D 加密模式」改名「D 加密身份模式」；免育碧功能的说明精简了一段。
- **联机**：「使用说明」弹窗底部按钮改为「好的 / 退出」，「好的」为默认按钮。
- **授权页**：「正版账号授权」的说明文案重写。

## 修复

- 导出授权失败时，不再在目标位置留下一个 0 字节的 `.ost` 文件。

## 底层

- Windows App SDK 1.6 → 2.6.5。程序自带运行时，覆盖目录即可，不需要另装。

## 升级方式

- 覆盖旧版程序目录即可。
- 内核无需更新。

---

[![](https://img.shields.io/badge/powered_by-dsh-4D6BFE?style=flat-square&logo=deepseek&logoColor=white)](https://github.com/deepseek-ai/deepseek-harness)
