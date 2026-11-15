# ClearCast 当前进展

## 当前基线：1.3.3.0

- GitHub [v1.3.3.0](https://github.com/NCFXZ/ClearCast/releases/tag/v1.3.3.0) 已正式发布，源码与标签固定为 `ac3ac8a4164e32db7f1bb50ea0672a7321cb03e0`。发行身份、交付范围与验证入口见 [发行记录](shared/docs/releases/release-status-1.3.3.0.md)。
- 微软商店 Submission 8（`1152921505702411362`）已提交，当前处于认证预处理，认证通过后自动发布；尚未确认本版上架。现有上架版本为 Submission 7 的 `1.3.2.0`。
- 默认入口 `scripts/Launch-ClearCast.cmd` 已同步到最终 `.runtime/app/`，接收器位于 `.runtime/receiver/`，真实 `.clearcast-state` 的配对与偏好保持不变。自动化使用隔离状态。

## 本轮实现

- 视频模糊背景复用可写位图，采样与处理不等待淡入或 GPU 资源退役；保留音乐背景、媒体时钟与视觉处理。
- 配对按“公钥＋规范化名称”独立保存、准入和删除，保留其他记录的接收端身份；删除后的设备可通过显示的验证码再次登记，保留 Claude 修复的握手回退。发送端名称及共享私钥的边界见 [AirPlay 媒体](shared/docs/AIRPLAY_MEDIA.md)。
- 受控 libplist 更新至官方 `2.8.1`，同步固定来源、SHA-256、构建、许可证及对应源码打包门禁；构建与验证使用 SDK `10.0.404` 和自包含 .NET `10.0.15`。
- 已核对并清理旧运行目录及无用引用；Android TV 已迁出，本仓库无其源码或构建范围。WinUI 框架隐式消费的主题资源与视觉状态保留。
- 中英文 GitHub/商店首图均为等待连接界面；更新文案各三条，只描述用户可感知的变化。本轮软件与安装验收、源码和安装包交付已完成。

## 后续维护与验证边界

- 后续确认商店认证及公开上架结果；提交成功与上架状态分别记录。
- 跟进 OpenSSL 官方 Low 级公告的正式修复版本，升级时同步固定来源及完整验证；目前不使用未验证快照或绕过完整性检查。
- 自动化及沙箱不能代替真实 Apple 设备、账号/DRM、物理音画同步、全部 GPU 或长期弱网验收。
- 验证入口：[托管与 UI](shared/docs/testing.md)、[原生](shared/docs/testing-native.md)、[打包](shared/docs/packaging.md)及[稳定性与安全](shared/docs/RELIABILITY_SECURITY.md)。详细结果与运行产物仅留在忽略的 `.runtime/qa-release-1.3.3.0-libplist281/`，不写入本文件。
