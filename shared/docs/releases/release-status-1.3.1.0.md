# ClearCast 1.3.1.0 发行状态

截至 2026-11-14：GitHub 已发布，微软商店更新已提交认证。

- [GitHub v1.3.1.0](https://github.com/NCFXZ/ClearCast/releases/tag/v1.3.1.0) 为 Latest 正式版，五个附件已匿名完整下载，大小与 SHA-256 均匹配。应用源码及标签固定为 `39aca8d0c4cfd031ec9b6473ca2edb8463232bf9`；后续打包说明、发行记录和文案提交不改变安装包与源码身份。
- 应用、Bundle 和 APPX 为 `1.3.1.0`，MSI 为 `1.3.1`。既有升级身份及商店 Name/Publisher 不变。
- 微软商店产品 `9P0HD6FG5CS3` 的 Submission 6（`1152921505702403904`）已提交。最终 x64 APPX 通过包验证，中英文说明与各十张新版截图已保存。Partner Center 显示 `Update in certification`：Submission 完成、Pre-processing 进行中，认证通过后自动发布；尚未确认本版上架。Submission 5 的 `1.3.0.0` 是当前已上架基线。
- GitHub 与商店更新说明均按用户要求缩短为中英文各四条，只介绍可感知的功能和修复，见[发行说明](release-notes-1.3.1.0.md)。

## 最终文件

| 文件 | SHA-256 |
| --- | --- |
| `ClearCast-1.3.1.0-Windows-x64-Setup.exe` | `2484d3a8e5c1da95d8cac75a579ef2e6e4e2fd017315a229fd0bf5988921b68d` |
| `ClearCast-1.3.1.0-Windows-x64-Store.appx` | `81b9d9acd9ee9e0f4846571d35ca5ca87787d52b3b7a898fb63b51f290cb2a32` |
| `ClearCast-1.3.1.0-source.zip` | `4a250f0ff46ea86e000a03df4b02d8ca354e1e500ea52587a88bbaa3118d84b8` |
| `ClearCast-1.3.1.0-corresponding-source.zip` | `a45e8b2ec657ef4f25e5971d430b7f32185bbcd2185717562ac2b3a361d15269` |
| `QA-summary.md` | `10205343f07e2bb1500b8f9478e43acde8361fcb81c70e2da345fc7713f6ebca` |
| `SHA256SUMS` | `1507d3a3fed37fcb91447093b7b70319d81cea3b6480acd07757271a3948aae5` |

## 验证与本地交付

详细结果见公开[QA 报告](https://github.com/NCFXZ/ClearCast/releases/download/v1.3.1.0/QA-summary.md)。本轮覆盖托管、原生协议与解析器、安全和异常输入、媒体解码、HTTP/HLS/TLS、实际应用播放、稳定性、英文与视觉，以及 Windows 沙箱安装、升级、修复、降级拒绝、卸载和商店包启动。最终分发目录也完成 Defender 扫描。

`.runtime/app/` 与 `.runtime/receiver/` 已同步最终构建并核对；真实配对和偏好未改动，独立 Android 工作未随 Windows 源码提交。沙箱测试使用任务自建实例及临时签名，测试包、证书、信任、私钥和实例已清理。完整证据留在忽略的 `.runtime/qa-release-1.3.1.0/`，产物在 `.runtime/release-1.3.1.0/`。

自动化结果不能代替真实 Apple 设备、实际账号和受保护内容、声音/GPU、长期弱网与物理多网卡验收。OpenSSL 3.5.9 的已知 Low 公告与可达性评估见公开 QA；继续跟进官方稳定修复版，不使用未固定快照或跳过完整性检查。
