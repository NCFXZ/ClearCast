# ClearCast 1.3.0.0 发行状态

## 当前范围

本版包含音乐全屏、歌词显示及匹配、手动浏览、频繁切歌资源回收修正，以及相关安全、背压和打包校验。长期规则见 `AGENTS.md`，当前任务及验收边界见 `PROGRESS.md`。

- 版本：应用、Bundle、APPX 为 `1.3.0.0`，MSI 为 `1.3.0`；既有升级身份、Store Name 和 Publisher 不变。
- 源码由 `v1.3.0.0` 标签固定为 `8919da6592aa9808065e520fc3ad047545f90c21`；最终桌面/商店构建的 `ProductVersion` 对应该提交，接收器散列及资源索引已核对。后续发行记录提交不改变标签与源码 ZIP。
- GitHub 源码及安装包已于 2026-11-12 发布：[v1.3.0.0](https://github.com/NCFXZ/ClearCast/releases/tag/v1.3.0.0)。五个附件均已匿名完整下载，大小与 SHA-256 相符；标签直接指向上述源码提交。发行页包含中英文音乐、歌词全屏截图，歌词截图四周无黑边。
- 微软商店：本版 Submission 5（`1152921505702381340`）已于 2026-11-12 提交，最终 `1.3.0.0` x64 APPX 已通过包验证；中英文说明及各十张截图已保存，包含深色/浅色全屏歌词截图。Partner Center 已显示 `Update in certification`，Submission 完成、Pre-processing 进行中，认证通过后自动发布。尚未确认本版上架；既有 Submission 4 的 `1.2.2.0` 保持上架。
- 发行说明：[中英文说明](release-notes-1.3.0.0.md)。完整测试结果和产物留在忽略的 `.runtime/qa-release-1.3.0.0/` 与 `.runtime/release-1.3.0.0/`；公开 QA 报告随发行附件提供。

## 最终包与本地交付

| 文件 | SHA-256 |
| --- | --- |
| `ClearCast-1.3.0.0-Windows-x64-Setup.exe` | `4d8a0ad24e313876122cb1f885d26d5923ad79a4fefa964d07629411c4194704` |
| `ClearCast-1.3.0.0-Windows-x64-Store.appx` | `66c89398780c0f8a96019834e7b42d8333a7470b777908339f390a2812570076` |
| `ClearCast-1.3.0.0-source.zip` | `1db6b3f6342c53f00e267ef4725778541f42a8bb8b0916e9cbcb8afd21f05cdf` |
| `ClearCast-1.3.0.0-corresponding-source.zip` | `095ad87c0d4691ef05e0b50e6ac1aaa9d7a6ad1b7e06341392d9acb1956eae51` |
| `QA-summary.md` | `faee1371b7b25e36e410752c835f24906473ea566daff6715c710eb1e2f9375f` |

默认 `.runtime/app/` 和 `.runtime/receiver/` 已同步最终构建并完整校验；真实配对及偏好内容保持不变，用户备份未动。沙箱仅使用任务自建实例及临时签名，测试包、证书、信任和私钥已清理。

## 验证边界

自动回归覆盖受控接收器、托管状态、图像与歌词输入、媒体解码与网络策略、实际窗口英文/布局、压测及安装生命周期。不能代替 iPhone/iPad/Mac、真实账号内容、实际声音/GPU、长期弱网和物理多网卡验收。

OpenSSL 当前固定 3.5.9；官方尚未提供 3.5.10 稳定归档，已知 Low 公告与应用路径可达性评估留在公开 QA 报告及依赖维护待办中。不得以未固定快照或跳过完整性检查代替正式更新。
