# ClearCast 1.2.2.0

## 中文

- 新增 F12 媒体信息面板，显示可获得的分辨率、帧率、编码、码率及音频参数；视频窗口和全屏分别定位，音乐信息显示在封面上方。
- 修正前后跳转 10 秒，按执行时的当前播放位置计算；空格键只切换可用的播放/暂停。更新专用跳转图标与“断开连接”文案，修正直接视频自动播放流程。
- 完善媒体、事件、封面与渲染任务的背压、取消和过载处理；修正过期控制命令、音频管线释放与重复启动/停止的边界。
- 修复 HLS 分片下载耗时为零时的速率采样，保留正常清晰度自适应。
- 收紧已验证设备与媒体会话授权，限制回环媒体 URL 和网卡监听，补齐请求/配对输入校验、失败退避及原生编译加固。
- 补齐 Windows Shell 专用图标与资源索引，重新采集完整应用全屏商店截图；清理不再使用的代码和构建副本，完善英文及可访问性检查。

视频、音乐控制、定位和音画兼容性取决于发送设备、播放器及内容。媒体下载使用直接连接；只可通过 HTTP/SOCKS 代理访问的视频源可能不可达，系统代理设置不会被修改，TUN 路由仍由 Windows 处理。

## English

- Adds an F12 media information panel for available resolution, frame rate, codecs, bitrate, and audio details. Placement follows windowed/full-screen video or appears above music artwork.
- Calculates 10-second skips from the current playback position at execution time. Space toggles available play/pause controls. Updates dedicated skip icons, disconnect wording, and direct-video autoplay.
- Improves bounded queues, cancellation, and overload handling for media, events, artwork, and rendering. Fixes stale control commands, audio-pipeline teardown, and repeated start/stop boundaries.
- Fixes HLS throughput sampling for fragments with zero measured download time while preserving adaptive quality selection.
- Tightens verified-device and media-session authorization, loopback media URL access, and listener interfaces. Improves request/pairing validation, failure backoff, and native compiler hardening.
- Adds Windows Shell icon resources and indexing, replaces Store images with full-screen application captures, removes unused code/build copies, and extends English/accessibility checks.

Playback, controls, seeking, and audiovisual compatibility depend on the sending device, player, and content. Media downloads connect directly; sources that require an HTTP/SOCKS proxy may be unreachable. Global proxy settings are unchanged, and Windows continues to handle TUN routing.
