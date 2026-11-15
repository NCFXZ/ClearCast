# ClearCast 1.3.3.0 发行状态

截至 2026-11-15：GitHub 已正式发布，默认本地入口已同步，微软商店更新已提交认证。

- [GitHub v1.3.3.0](https://github.com/NCFXZ/ClearCast/releases/tag/v1.3.3.0) 为正式 Latest 版本。五个附件均已匿名完整下载，大小与 SHA-256 与最终文件一致。源码快照与轻量标签固定为 `ac3ac8a4164e32db7f1bb50ea0672a7321cb03e0`。
- 正式应用编译源为 `d57ff62a8f8f74f225c5f45c65d38fa33d6eab3d`，安装包使用 `bb8eba7` 的完整来源校验门禁。后续快照更新对应源码校验、外部 UI 探针及实际截图；逐文件 Git blob 核对确认 WinUI/原生源码、素材与编译输入一致。两个源码 ZIP 均按固定提交、归档 CRC、来源哈希与许可证原文独立核对。程序信息版本保留真实编译提交。
- 应用、Bundle 和 APPX 为 `1.3.3.0`，MSI 为 `1.3.3`；沿用升级身份、商店 Name/Publisher 与 Windows x64 范围。
- 微软商店产品 `9P0HD6FG5CS3` 的 Submission 8（`1152921505702411362`）已提交。最终 APPX 已被接受，中英文各十张截图与[简明更新说明](release-notes-1.3.3.0.md)已保存。回读确认两种语言的首图均为等待连接页，首图 SHA-256 与原始截图一致。Partner Center 显示 `Update in certification`：Submission 完成、Pre-processing 进行中，认证通过后自动发布；尚未确认本版上架。已上架基线为 Submission 7 的 `1.3.2.0`。

## 最终文件

| 文件 | SHA-256 |
| --- | --- |
| `ClearCast-1.3.3.0-Windows-x64-Setup.exe` | `f9aa67dad9c1c154de014e754e73644382247af877380f9ec2fcaf54661c2914` |
| `ClearCast-1.3.3.0-Windows-x64-Store.appx` | `b32d7547a8c855281274f69076e56f8649df519ede04fc3176385be78a44bfb3` |
| `ClearCast-1.3.3.0-source.zip` | `fafff56199fdb52b636db5cb527a7f15b31ebe2f4830073f817682670cc80164` |
| `ClearCast-1.3.3.0-corresponding-source.zip` | `0c35fbe8b428099fd45613ce73953daab8cbb9ddc64a1887909653cddda9bc08` |
| `QA-summary.md` | `5eaa2397f0b2a690bf876bf243716929cedad531b549fb2e517c7201c057b514` |
| `SHA256SUMS` | `6b13066283cfa755f6cd84e90f611e8df0b157e5ad1f4829148763e1bdd89936` |

## 清理、验收与本地交付

详细验证范围、结果与限制见公开 [QA 报告](https://github.com/NCFXZ/ClearCast/releases/download/v1.3.3.0/QA-summary.md)。覆盖托管状态、原生协议与配对、安全/畸形输入、引用膨胀、HTTP/HLS/TLS、媒体解码、停止及重连压力、真实应用播放、英文/视觉布局，以及 Windows 沙箱安装、升级、降级拒绝、修复、卸载和 Store 包启动。测试中发现的外部探针时序问题已修正并重测，原始失败记录保留在本地证据中。

本轮使用官方固定来源构建 libplist 2.8.1，补齐对应源码的哈希绑定与打包拒绝检查，保留 LGPL/MIT 声明。清理旧运行目录并核对无用源码/UI 引用；本仓库已无 Android TV 源码，迁出的独立仓库未改动。WinUI 隐式消费的主题键与视觉状态保持有效。

`scripts/Launch-ClearCast.cmd` 的默认 `.runtime/app/` 与 `.runtime/receiver/` 已同步最终运行时，替换文件有备份并经过完整哈希核对，真实配对与偏好的路径和字节保持不变。沙箱只使用本次自建实例，测试包、证书、信任、私钥及实例均已清理。

当前证据位于忽略的 `.runtime/qa-release-1.3.3.0-libplist281/`，最终产物位于 `.runtime/release-1.3.3.0-libplist281/`。商店凭据为 `store-submission-results.json`、`store-listings-readback.json`、`store-submitted-state.txt` 与 `store-submitted.jpg`。旧候选证据另行保留；公开下载核对完成后清理重复上传副本。

设备名称由发送端声明，共享私钥持有者可冒用同名，不能将本地名称准入视为硬件身份隔离。自动化也不替代真实 Apple 设备、账号/DRM、物理音画同步、全部 GPU 或长期弱网验收。OpenSSL 官方尚未发布的 Low 级修复及受影响功能可达性评估见 QA 与[官方公告](https://openssl-library.org/news/secadv/20261110.txt)；未引入未验证快照或完整性绕过。

后续发行记录更新不改变已发布资产、标签或应用身份。
