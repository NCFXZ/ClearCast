# ClearCast 1.2.1.0

## 中文

- 新增电脑端视频播放/暂停、进度拖动、后退及前进 10 秒；空格键切换可用的播放/暂停。发送端支持并确认定位时，音乐进度也可拖动。
- 修复直接视频在辅助音频流重新建立或退出时误停的问题，以及进度控件首次显示时可能导致应用退出的问题。
- 重新布局播放控件：非全屏时置于视频下方，全屏时使用自动隐藏浮层；镜像和直播仅显示断开、旋转及全屏。
- 非全屏视频保留完整比例和圆角，用与音乐相同的流动背景填充整个应用。背景采用 100 毫秒平滑过渡，减少追随画面的滞后。
- 纯音乐按发送端时间戳调度，修正立即输出可能造成的声音提前；实际歌词同步取决于发送应用和设备。
- 更新应用 Logo 和等待页矢量动画；已验证连接提供名称时显示发送设备名称，改进显示大小图标和快捷键提示。
- 清理过期界面代码和构建产物，并更新 .NET 10 安全维护运行时。

视频定位取决于媒体是否可定位；音乐定位及远程控制取决于发送端能力。不同应用、网络及受保护内容的兼容性可能不同。

## English

- Adds desktop video play/pause, seeking, and 10-second backward/forward controls. Space toggles available playback controls. Music seeking is available when the sender supports and confirms it.
- Fixes direct video stopping when an auxiliary audio stream is recreated or closed, and an application exit that could occur when the progress control first appeared.
- Reorganizes playback controls below windowed video and in an auto-hiding fullscreen overlay. Mirroring and live video show disconnect, rotation, and fullscreen controls only.
- Preserves the complete windowed picture with rounded corners and fills the application with the same flowing background used for music. A 100 ms smooth transition reduces background lag.
- Schedules music using the sender's timestamps instead of immediate output, addressing audio that could play early. Actual lyric synchronization depends on the sending app and device.
- Updates the app logo and native vector waiting animation, shows verified sender names when available, and improves the display-size icon and shortcut hints.
- Removes obsolete interface code and build outputs, and updates the .NET 10 security servicing runtime.

Video seeking depends on the media's seekable range. Music seeking and remote controls depend on sender capabilities. Compatibility varies by application, network, and protected content.
