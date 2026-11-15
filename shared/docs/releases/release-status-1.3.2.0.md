# ClearCast 1.3.2.0 发行状态

截至 2026-11-15：GitHub 已发布，默认本地启动入口已更新，微软商店更新已提交认证。

- [GitHub v1.3.2.0](https://github.com/NCFXZ/ClearCast/releases/tag/v1.3.2.0) 为正式 Latest 版本，五个附件已匿名完整下载并核对大小与 SHA-256。标签及发行源码快照固定为 `3f737d92bb3a97afe8a363b956fbea276037a91c`。
- 安装包编译源为 `f90f46941bf9f5ab281b663395f68a0e5faf911d`。后续源码快照只更新 `scripts/Launch-ClearCast.cmd` 和 `PROGRESS.md`；逐文件 Git blob 比较确认其余应用、原生、依赖与打包输入完全相同。已验收安装包保持字节不变，程序信息版本如实记录原编译提交。后续发行记录和更新文案编辑不改变资产、标签或应用身份。
- 应用、Bundle 和 APPX 为 `1.3.2.0`，MSI 为 `1.3.2`；沿用既有升级身份、x64 桌面范围及商店 Name/Publisher。
- 微软商店产品 `9P0HD6FG5CS3` 的 Submission 7（`1152921505702410216`）已提交。最终 APPX 已验证，中英文各十张本版截图及[简明更新说明](release-notes-1.3.2.0.md)均已保存。Partner Center 显示 `Update in certification`：Submission 完成、Pre-processing 进行中，认证通过后自动发布；尚未确认本版公开上架。当前已上架基线为 Submission 6 的 `1.3.1.0`。

## 最终文件

| 文件 | SHA-256 |
| --- | --- |
| `ClearCast-1.3.2.0-Windows-x64-Setup.exe` | `3897762226bc14b0627f53ca2d47d654d3936db71c6d6f0addeb5063ad36f4e2` |
| `ClearCast-1.3.2.0-Windows-x64-Store.appx` | `5f632503db00d083dbad849f58cae1f0ef641c1e3098b2c36798d8f4fd05d8fd` |
| `ClearCast-1.3.2.0-source.zip` | `5727f8fb7770a2e995b64e651ed0d2585385c8f4ab3b3ba12d0d5db2aed4fb99` |
| `ClearCast-1.3.2.0-corresponding-source.zip` | `37b3413d14d53a0603fe663401a69ac6f5e17ed300dd438af89328990696e1c7` |
| `QA-summary.md` | `b31a928016d6e424c4fc00ab106c098664a78f95cb9a36614c51f8f192245605` |
| `SHA256SUMS` | `5e9a714d659aa0a4abfcabb2acd302e16ec2118fddb31a0f1457b6260e5df6d2` |

## 验证与本地交付

详细范围、成绩和限制见公开 [QA 报告](https://github.com/NCFXZ/ClearCast/releases/download/v1.3.2.0/QA-summary.md)。本轮包括托管状态和原生协议、安全/畸形输入、HTTP/HLS/TLS、媒体解码、真实应用播放、歌词与背景压力、英文及视觉、Defender，以及 Windows 沙箱的安装、升级、降级拒绝、修复、卸载和 Store 包启动。新增停止对抗覆盖停发、截断、重复停止与配对重连；延长循环核对窗口消失和资源回落，未将短期预算通过冒充长期零泄漏或物理音画同步验收。

`Launch-ClearCast.cmd` 已同步固定工作目录和缺文件处理；空格、中文路径及隔离启动检查通过。`.runtime/app/` 和 `.runtime/receiver/` 已核对并同步最终运行时，替换文件均有备份，真实配对及偏好的路径和字节保持不变。沙箱仅使用本次自建实例和临时签名，测试包、证书、信任、私钥及实例均已清理。

完整证据留在忽略的 `.runtime/qa-release-1.3.2.0/`，正式产物在 `.runtime/release-1.3.2.0/`。商店提交证据为 `store-submission-results.json`、`store-submitted-state.txt` 与 `store-submitted.jpg`。公开说明已按用户要求缩为中英文各四条，仅描述可感知的功能和修复；编辑后重新确认所有发布资产保持不变。

自动化不能代替真实 Apple 设备、账号/DRM、物理 GPU/声音、多网卡和长期弱网体验。永久合成器或驱动失效仍可能影响安全窗口销毁；OpenSSL 已知 Low 公告、受影响功能的可达性评估和官方修复维护项见 QA，不使用未验证快照或跳过完整性检查。
