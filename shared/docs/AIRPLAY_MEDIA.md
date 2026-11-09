# AirPlay 音乐与视频

## 使用方法

将发送设备与 Windows 电脑连接到允许设备互访的同一局域网，打开 ClearCast。在设备的「屏幕镜像」或兼容播放器的 AirPlay 选项中选择 ClearCast，按电脑提示完成验证码配对。

音乐连接会显示发送端提供的封面、曲名、歌手、专辑和进度。没有提供的信息使用占位内容；进度仅用于查看，不能拖动定位。发送端提供可用控制能力时，才显示相应的播放/暂停、上一首、下一首按钮；不支持远程控制时隐藏整个控制区及提示，可继续在发送设备上操作。

音乐内容在左侧导航以外的页面区域内居中，依次展示封面、曲名、歌手、进度和上一首/播放/下一首。封面颜色形成柔和的背景并缓慢流动，覆盖标题栏和左侧导航后的整个应用区域；切换到「设置」或「关于」后仍继续流动。背景遵循当前浅色或深色主题，不改变用户的主题选择。音乐暂停、窗口最小化或隐藏、关闭系统动画时停止流动，高对比度模式关闭彩色背景。界面不增加音量、更多或歌词入口。

镜像和视频共用现有画面窗口，可使用全屏、旋转及显示大小选项。在「设置 → 播放显示」开启「投屏时自动全屏」后，下一次视频开始时进入全屏；此选项默认关闭，对纯音乐无效。当前会话中修改此设置不会立即改变窗口，手动退出全屏后也不会因后续画面更新再次进入。

封面、曲目信息、进度和控制能力取决于发送设备、播放器及内容。不同应用的 AirPlay 实现和权限要求不同，不能将某个协议或本地测试的结果推广到所有播放器。

## 实现分工

- 原生接收器继续使用受控 UxPlay 和 GStreamer 处理发现、配对、音频和视频。音频的曲目信息、封面和进度进入有界的当前会话状态。
- [ReceiverMediaState](../../src/ClearCast/Services/ReceiverMediaState.cs) 解析原生状态快照，托管桥接不把原生指针或未校验字符串直接交给界面。快照最多 8 KiB，封面最多 2 MiB，时长和进度最多七天；缺失或畸形状态不会成为可执行控制能力。
- [MainWindow.Music](../../src/ClearCast/Views/MainWindow.Music.cs) 管理音乐界面、只读进度和异步封面解码。封面只在对应接收器、会话内容和加载代次仍有效时显示；停止连接或切换封面后，旧的异步结果不能覆盖新状态。
- 播放进度使用发送端给出的时间信息；正在播放的音频可在两次快照之间按本机经过的时间更新显示，暂停时保持不变，已知时长时不越过结尾。没有时长时显示未知时长，并在播放中使用不定进度条。
- 音乐远程控制使用当前已验证发送端的 DACP 服务和会话凭据。能力发现和命令执行在原生层进行，托管层根据能力位决定是否显示按钮，忙碌时暂时禁用已支持的按钮；界面不把无法确认的能力当作可用。
- [MainWindow.Player](../../src/ClearCast/Views/MainWindow.Player.cs) 保持窗口、原生视频 HWND、全屏及恢复逻辑的单一实现。自动全屏只在视频由无到有时触发，音乐状态不进入这一分支。
- [MusicBackdrop](../../src/ClearCast/Controls/MusicBackdrop.cs) 将有界封面采样为 32 × 32 像素，从中选择柔和色调；四层宽阔色流与四层局部色域分布在不同位置，由合成器以错开的 13–24 秒周期和初始相位改变长短轴、偏心渐变原点、旋转轴心及位置。不同方向的收拢与舒展在八处形成柔和的局部形变，渐变在边缘完全淡出，不使用逐帧界面计时器。XAML 底色供普通截图保留配色，合成器渐变的实际外观与运动需要窗口级截图或观察验证。

媒体状态、曲目信息、封面和控制凭据用于当前播放，不写入偏好文件或连接日志。偏好文件保存包括自动全屏在内的用户设置；配对记录仍沿用现有本地保存方式。数据处理范围见[隐私政策](legal/PRIVACY.md)。

## 协议范围

当前实现覆盖现有屏幕镜像、AirPlay 音频，以及通过 HTTP(S) 地址或 HLS 播放列表提供的视频。直接视频沿用上游 AirPlay 请求处理，并由 GStreamer 在现有视频窗口中播放；它与设备整屏镜像是不同的数据入口。

直接视频可能需要电脑从发送设备或内容服务器读取媒体，因此除了设备互访，有时还需要电脑能够访问相应媒体地址。当前 HTTP 源保留 TLS 证书验证，禁用自动重定向，并拒绝文件、data、FTP、RTSP 等其他 URI 来源。需要特殊登录、重定向或受保护播放流程的内容可能不能直接播放。

当前范围不包含 AirPlay 2 多房间同步或 DRM 解密。AirPlay 入口可见、请求被接受或普通本地媒体可以播放，都不能证明受保护内容、每个播放器账号或所有设备组合可用。

## 验证入口

从仓库根目录使用现有入口：

```powershell
.runtime/dotnet/dotnet.exe run --project tests/managed/ManagedStateTests.csproj -c Release
```

托管回归包含快照解析、整数和进度边界、能力位过滤、会话身份及偏好兼容性。原生 CTest 包含 `media_audio_bounds` 和 `media_http_source_policy`；具体构建与运行方式见[原生测试](testing-native.md)。

隔离 UI 构建通过 `tests/ui/MainWindow.MediaUiTests.cs` 检查音乐字段、真实图片解码、失效封面、布局和自动全屏窗口行为，使用合成媒体状态，不发送真实播放控制。运行方式和截图边界见[托管与 UI 测试](testing.md)。报告、截图和日志保存在被忽略的 `.runtime/`，本文件不保存测试成绩。

真机验收仍需分别检查：

1. 从实际播放器投放音乐，确认音频、曲目信息、封面与暂停/恢复；验证发送端支持和不支持控制时的界面，并检查切歌和断开后没有旧状态。
2. 从兼容播放器投放普通、非 DRM 的视频，检查画面、音频、全屏、窗口恢复、暂停/继续及断开重连。
3. 在真实 iPhone、iPad、Mac、网络和 GPU 上检查镜像与视频播放。合成协议、软件解码或窗口布局测试不能代替音画同步、物理 GPU 或账号内容的验收。

## English

Select ClearCast from Screen Mirroring or a compatible player's AirPlay menu on the same local network. Music shows the metadata and artwork supplied by the sender. Progress is read-only; transport buttons appear only when the sender offers usable controls; the entire transport area and its hint are hidden when remote control is unavailable. Compatibility depends on the device, player, and content.

The music content is centered in the page beside the left navigation. A muted cover-colored background extends across the whole app, including behind the title bar and navigation, while respecting the selected light or dark theme. Eight overlapping regions slowly bend and stretch at different positions and rhythms, with soft fading edges, and keep moving on Settings and About. Motion stops when paused, minimized, hidden, or when system animations are disabled; high contrast removes the colored background. The view has no volume, more, or lyrics controls.

**Settings → Playback → Automatically enter full screen** is off by default. It applies when a new video session starts, including mirroring, and never applies to audio-only music. Changing the setting during a session does not immediately change the window.

The current protocol scope includes existing mirroring and audio plus HTTP(S)/HLS video playback in the same video host. It does not establish support for AirPlay 2 multiroom, DRM, every streaming account, or every application. Automated parser, protocol, image, and window checks do not replace physical-device playback acceptance.
