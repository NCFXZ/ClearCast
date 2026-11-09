# ClearCast 1.1.0

## 中文

- 统一使用「屏幕镜像」和「设备」文案，更新侧栏、使用说明、验证码、断开操作和错误提示；使用说明显示当前电脑的实际接收名称。
- 统一设置卡片的上下留白，文字按钮和设置下拉框采用 130 × 32 的最小尺寸，较长文字可自动撑宽。
- 更新屏幕镜像页插图，使用 80 帧、目标每秒 30 帧的本地 SVG 循环动画。按显示 DPI 解码，先显示首帧并缓冲少量帧，再继续加载后续帧。
- 切换到其他页面、最小化窗口或播放镜像画面时暂停插图动画，返回等待页面后继续播放。
- 将启动阶段的网卡查询和原生组件加载移至后台，并补齐 GStreamer 独立插件扫描器，减少首次打开时的界面阻塞。
- 按验证码请求关联配对结果，避免旧连接或旧验证码的结果覆盖当前验证码。旧密钥探测与实际验证失败分开处理，配对拒绝不再同时重复触发通用错误提示；验证码、证明和签名校验仍然保留。
- 修复部分配对因 SRP 挑战编码不一致而拒绝正确验证码的问题。
- 更新受控依赖至 libplist 2.8.0 和受支持的 OpenSSL 3.5.9 LTS，使用固定官方源码，并验证二进制、头文件和导入库清单。
- 为验证码提示增加关闭按钮，并调整关闭按钮与文字的对齐。
- README 现在同时提供完整中文和英文说明；隐私说明补充本地配对阶段日志的记录范围。

## English

- Use consistent screen mirroring and device terminology across navigation, instructions, pairing codes, disconnect actions, and errors. Instructions show the current PC's actual receiver name.
- Center settings-card content vertically and use a minimum size of 130 × 32 for text buttons and settings selectors, with room to grow for longer labels.
- Replace the waiting-page illustration with a local, 80-frame SVG loop targeting 30 frames per second. Frames are decoded for the current display DPI; the first frame appears before a small playback buffer and the remaining frames finish loading.
- Pause the illustration on other pages, while the window is minimized, and during video playback. Resume it when returning to the waiting page.
- Move adapter discovery and native component loading off the UI thread and include GStreamer's separate plugin scanner to reduce first-launch UI blocking.
- Associate pairing feedback with its code request so results from older connections or codes do not replace the current prompt. Distinguish old-key probes from real verification failures and avoid duplicate generic error banners for the same pairing rejection. Code, proof, and signature validation remain enforced.
- Fix a pairing issue where inconsistent SRP challenge encoding could reject a correct code.
- Update controlled dependencies to libplist 2.8.0 and supported OpenSSL 3.5.9 LTS, using pinned official sources and verified binary, header, and import-library manifests.
- Add a dismiss button to the pairing-code prompt and align close buttons with their text.
- Provide a complete Chinese and English README and document the scope of local pairing-stage diagnostics in the privacy policy.
