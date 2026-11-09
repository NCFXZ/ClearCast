# ClearCast 1.2.0.0

## 中文

- 新增 AirPlay 音乐展示：显示发送端提供的封面、曲名、歌手、专辑和播放进度；发送端支持时可播放、暂停及切换歌曲，不支持的控件自动隐藏。
- 接收兼容播放器的视频投放，在现有窗口中播放；视频与屏幕镜像沿用等比显示、旋转及全屏操作。
- 新增覆盖窗口、侧栏和标题栏的封面纹理背景。局部扭曲与模糊保留封面结构，按真实音乐的节奏疏密平滑调速；强低频鼓点使背景短促放大并自然回弹，前景封面、文字和按钮保持稳定。
- 增强浅色主题的封面颜色，保留文字可读性；音乐播放时切换到设置和关于页，背景继续流动，并遵循系统动画、可见性和高对比度设置。
- 新增「投屏时自动全屏」设置，用于新的视频投放，默认关闭，纯音乐不会自动全屏。
- 已认证设备连接后显示「AirPlay 已连接」，断开后恢复等待；暂停或播放结束时保留仍有效的连接状态。
- 改进首次验证码反馈和短连接重试；同设备在 IPv4 与 IPv6 间切换时，使用系统邻居缓存确认来源，不降低配对验证或失败冷却要求。
- 统一应用、窗口和打包图标，缩小标题栏 Logo，并调整音乐布局、设置图标和自动全屏开关。

播放进度仅供查看，不能拖动定位。封面、曲目信息、控制和视频兼容性取决于发送设备、播放器与内容。背景响应已解码的音乐信号，不保证与物理扬声器逐拍精确同步。

## English

- Add AirPlay music presentation with artwork, title, artist, album, and playback progress supplied by the sender. Show play, pause, and track controls only when the sender supports them.
- Receive video from compatible players in the existing playback window, using the same aspect-ratio, rotation, and full-screen controls as screen mirroring.
- Add an artwork-textured background across the window, sidebar, and title bar. Local warping and blur retain the artwork's structure, while rhythmic density from decoded music adjusts its pace smoothly. Strong low-frequency beats add a short, naturally damped zoom and rebound without moving foreground artwork, text, or controls.
- Retain richer artwork colors in the light theme while preserving text readability. Keep the music background flowing on Settings and About, respecting system animation, visibility, and high-contrast settings.
- Add an optional automatic full-screen setting for new video sessions. It is off by default and does not apply to audio-only sessions.
- Show AirPlay connected after authentication and return to waiting after disconnection. Pausing or ending playback retains a still-active connection.
- Improve first-code feedback and retries across short connections. Confirm same-device IPv4/IPv6 changes through the system neighbor cache while preserving pairing verification and failure cooldown requirements.
- Unify application, window, and package icons, use a smaller title-bar logo, and refine the music layout, settings icons, and automatic full-screen switch.

Progress is read-only and cannot be dragged to seek. Artwork, metadata, controls, and video compatibility depend on the sending device, player, and content. Background motion responds to decoded music and does not guarantee exact alignment with physical speaker output.

## Version mapping

The application, file version, installer bundle, and Store package use **1.2.0.0**. Windows Installer uses the first three version fields, so its MSI ProductVersion is **1.2.0**. The upgrade family and Store identity remain unchanged.
