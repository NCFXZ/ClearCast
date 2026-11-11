# ClearCast 1.2.2.0 发行状态

2026-11-11（UTC）。产品安装包与应用源码固定于 `v1.2.2.0`，提交 `ebdce445eb9e07e7626d21f8ffa672df574c84ff`。应用版本为 1.2.2.0，产品与升级身份沿用此前发行；构建使用固定 SDK 10.0.404 与自包含运行时 10.0.15。

## GitHub

[1.2.2.0 Release](https://github.com/NCFXZ/ClearCast/releases/tag/v1.2.2.0) 已公开并设为 Latest，提供桌面 EXE、应用源码 ZIP、包含依赖及许可证的对应源码 ZIP、QA 报告和 SHA256SUMS。标签、五个附件的大小及远端 SHA-256 已匿名核实。

| 产物 | SHA-256 |
| --- | --- |
| Windows x64 Setup.exe | `F644461826EB4E9A7E27DF8FCAEA0EDB1CCCB0104901C6997E370A12DBDD021F` |
| source.zip | `4C7CBED0D9B385F20A283C3B70135FF43DA48852EBEBEE80778BC778D0F826DA` |
| corresponding-source.zip | `08C4EEEE7B98403CE9CDE803F6E8DC7F188A5994AF7821E86E39ABAE9D1E372E` |
| Store.appx（提交至 Partner Center） | `AC3230B08EC70B6E5D5387AACA3CB6B90FEFA8261774ED52F2CF681A09B44352` |

GitHub EXE 没有发行签名；沙箱中的临时签名仅用于安装测试。商店交付签名由微软处理。

## Microsoft Store

产品 `9P0HD6FG5CS3`，Submission 4：`1152921505702378245`。已上传并保存经过校验的 1.2.2.0 APPX、中英文介绍/版本说明和各八张全屏截图，并更新认证测试说明及对应源码链接。

提交已成功。最后观察为 **Update in certification / Pre-processing in progress**，认证及发布尚未完成；保持“通过认证后自动发布”。商店原有公开版本仍是此前的 Submission 3，不能将本次提交误记为已上架。

## 验收与边界

- 代码、协议/安全、背压、托管状态、中英文本/可访问性、实际 MP4/HLS、UI 布局及有限生命周期验证见 [公开 QA 报告](https://github.com/NCFXZ/ClearCast/releases/download/v1.2.2.0/QA-summary.md)。原生候选、安装后所有文件、来源/补丁/许可证及源码归档之间有完整性校验。
- 禁网 Windows 沙箱完成旧版升级、降级拦截、修复、卸载重装和商店包验证。额外在沙箱最小化/恢复自有窗口采集无遮挡任务栏，实际图标、包身份及资源选择已验证。临时注册、证书和私钥已清理，没有将测试证书/安装改动施加到宿主。
- [商店截图](../screenshots/1.2.2.0/README.md) 是原生尺寸 2880×1800 的完整应用全屏，使用原创媒体与虚构信息，保留原始 PNG。保存后重开商店页面核实每语言八张、尺寸、内容和视觉效果。浏览器安全策略不允许下载后台 `data:` 内嵌预览，未宣称完成服务器 PNG 全字节对比。
- 本地启动版已同步，真实配对和偏好保持不变。现有动态模板、组件及样式因有实际消费者而保留；历史迁移脚本继续兼容既有基线。
- OpenSSL 的已知 Low 公告、真实 Apple 设备/播放器/账号内容、物理音频、不同 GPU/驱动及长期弱网等边界均在报告中说明。自动化和沙箱结果不能代替这些验收。

完整证据保留在忽略目录 `.runtime/qa-release-1.2.2.0/`，产物在 `.runtime/release-1.2.2.0/`。主分支在发行后补充本记录及发布工具的临时草稿重用修正；已发布标签、安装包和源码 ZIP 保持固定，不重新替换已验证附件。
