# 清单源状态

记录 OSTGUI 内置清单源的接入状态。**实际入库调用以 `OSTGUI/Services/ManifestService.cs` 为准**，此文档仅作维护参考。

| Id | 名称 | 需要 Key/Token | 接入状态 | 说明 |
|---|---|---|---|---|
| `sac` | SAC 分流 | 否 | ❌ 未接入 | 预留：SteamAutoCracks/ManifestHub 分支 zip 下载 |
| `walftech` | Walftech | 否 | ❌ 未接入 | 预留：walftech.com 代理清单 |
| `mhub` | MHub | ✅ API Key | ✅ 已接入 | 入库优先源，按 depot+gid 从 manifesthub API 下载 manifest |
| `steamautocracks_v2` | SteamAutoCracks V2 | 否 | ❌ 未接入 | 预留：仅提供密钥 |
| `sudama` | Sudama 库 | 否 | ✅ 已接入 | 提供 depot 密钥(depotkeys.json)与访问令牌(appaccesstokens.json) |
| `buqiuren` | 清单不求人 | 否 | ❌ 未接入 | 预留：manifest.steam.run + CDN 下载 manifest |
| `github_auiowu` | GitHub (Auiowu) | ✅ GitHub Token | ✅ 部分接入 | Token 用于 GitHub API 请求以解除限流；源本身（Auiowu 仓库）未单独实现 |
| `auto_github` | 自动搜索 GitHub | 否 | ❌ 未接入 | 预留：自动搜索 GitHub 清单仓库 |

## 当前入库流程（已接入）

多源级联，任一成功即完成：

1. **MHub**（配置了 API Key 时优先）：SteamCMD/Steam 官方 API 获取 depot+gid → manifesthub API 下载 manifest
2. **GitHub**：SteamAutoCracks/ManifestHub 仓库按 AppID 分支下载 manifest
3. **Sudama 兜底**：SteamCMD API 获取 depot+gid → 生成完整 Lua（补 depot key / access token）；不下载清单文件，清单由清单源（MHub / GitHub）负责，Sudama 仅作密钥源

所有路径最终统一生成完整 Lua（`addappid` + depot key + `addtoken`，勾选"写入固定版本配置"时另预写注释形式 `--setManifestid`），原子写入 `Steam/config/lua/`。MHub / GitHub 路径还会把 manifest 下载到 Steam 的 depotcache；Sudama 路径不下载清单。

## 注意事项

- 未接入的源不会在实际入库中被调用，保留条目仅作后续实现占位。
- MHub 域名默认 `api.manifesthub2.filegear-sg.me`（原文档域名 `manifesthub1` 已不可用）。
- GitHub 接口在部分网络环境下可能不可用，失败时由 Sudama 密钥源兜底生成 Lua（不含清单文件）。
