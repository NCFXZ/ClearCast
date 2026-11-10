# AirPlay 音乐与视频

## 使用方法

将发送设备与 Windows 电脑连接到允许设备互访的同一局域网，打开 ClearCast。在设备的「屏幕镜像」或兼容播放器的 AirPlay 选项中选择 ClearCast，按电脑提示完成验证码配对。

音乐连接会显示发送端提供的封面、曲名、歌手、专辑和进度。没有提供的信息使用占位内容。发送端返回有效的 DACP 定位状态时显示可拖动进度；定位后必须确认实际播放位置，拒绝或未生效时恢复只读进度。播放/暂停、上一首、下一首按钮按发送端能力显示；不支持远程控制时可继续在发送设备上操作。

音乐内容在左侧导航以外的页面区域内居中，依次展示封面、曲名、歌手、进度和上一首/播放/下一首。封面颜色形成柔和的背景并缓慢流动，覆盖标题栏和左侧导航后的整个应用区域；切换到「设置」或「关于」后仍继续流动。背景遵循当前浅色或深色主题，不改变用户的主题选择。音乐暂停、窗口最小化或隐藏、关闭系统动画时停止流动，高对比度模式关闭彩色背景。界面不增加音量、更多或歌词入口。

镜像和视频共用现有画面窗口，可使用全屏、旋转及显示大小选项。在「设置 → 播放显示」开启「投屏时自动全屏」后，下一次视频开始时进入全屏；此选项默认关闭，对纯音乐无效。当前会话中修改此设置不会立即改变窗口，手动退出全屏后也不会因后续画面更新再次进入。

封面、曲目信息、进度和控制能力取决于发送设备、播放器及内容。不同应用的 AirPlay 实现和权限要求不同，不能将某个协议或本地测试的结果推广到所有播放器。

直接视频提供电脑端暂停/继续和进度控制。只有播放管线报告有效可定位范围时才能拖动；屏幕镜像不显示这些控件。拖动和方向键调整完成后提交定位，轮询不会覆盖正在拖动的位置；换源、切歌或连接结束会使旧请求失效。

非全屏时，视频保持完整比例，在画面区域内居中，四角采用与音乐封面相同的圆角；比例不同产生的空余区域显示动态背景。控制条独立放在视频下方，已播放和剩余时间分别位于进度条两端。下一行左侧为文字断开按钮，中央为后退 10 秒、播放/暂停和前进 10 秒，右侧为向左/右旋转及全屏。镜像和直播只显示断开、旋转和全屏，包括带有 DVR 定位窗口的直播；有限时长视频可暂停，不支持定位时保留只读进度。不再展示连接、分辨率或帧率。全屏控制条改为画面内的底部浮层，空闲时隐藏，暂停、拖动、鼠标停留或键盘操作时保持可见。

在 AirPlay 页面按空格键可触发当前可用的播放/暂停操作，包括视频原生画面取得焦点时；音乐要求发送端支持远程播放控制。镜像和直播不触发该快捷键。10 秒定位按当前媒体范围限制，继续通过会话及内容代次校验。

等待页在连接后显示发送设备名称；名称只接受已验证连接提供的 SETUP 元数据或客户端名称头，缺失时保留通用 AirPlay 连接提示，断开后清除。等待页插画使用用户提供的 200 帧、25 fps、8 秒循环纯矢量 SVG，通过现有原生 `SvgImageSource` 组件按显示 DPI 解码；离开页面或最小化时暂停。

视频背景复用音乐的纹理扭曲、模糊和连续流动，覆盖标题栏、导航和控制区的整个应用界面，切换设置或关于页也保留。它使用当前解码画面的低分辨率内存采样，不裁切前景视频，不保存视频截图。画面更新经过 100 毫秒交叉淡化，旧背景保留到新背景完全覆盖，避免镜头突变时背景跟着闪变。独立 33 毫秒计时器在前一次处理和过渡结束后取最新画面；每次只生成当前相位纹理，减少准备时间。实际响应还包含取样、纹理上传和合成调度时间，不能将淡化时长视为整条链路的硬上限。全屏保持常规视频显示；系统动画、高对比度和窗口可见性约束仍然生效。

纯音乐 ALAC 通道使用发送端 RTP/NTP 映射的播放时间戳，由音频输出管线按时调度。接收后立即输出会使电脑声音领先发送端歌词，因此该通道启用时间戳同步；不添加猜测的固定延迟，不改变直接视频播放管线。手机歌词的呈现时序由发送应用控制，仍需真实设备确认。

手机锁屏可能重新准备辅助音频流。该流的 SETUP、FLUSH 或退出不会替代直接视频的生命周期，URL 播放也不依赖镜像的 `/feedback` 心跳。真正的停止、媒体结束和媒体源错误仍会结束直接视频；发送端及其媒体源本身的后台可用性仍需真机验证。

## 实现分工

- 原生接收器继续使用受控 UxPlay 和 GStreamer 处理发现、配对、音频和视频。音频的曲目信息、封面和进度进入有界的当前会话状态。
- [ReceiverMediaState](../../src/ClearCast/Services/ReceiverMediaState.cs) 解析原生状态快照，托管桥接不把原生指针或未校验字符串直接交给界面。快照最多 8 KiB，封面最多 2 MiB，时长和进度最多七天；缺失或畸形状态不会成为可执行控制能力。
- [MainWindow.Music](../../src/ClearCast/Views/MainWindow.Music.cs) 管理音乐界面、进度和异步封面解码。封面只在对应接收器、会话内容和加载代次仍有效时显示；停止连接或切换封面后，旧的异步结果不能覆盖新状态。
- 播放进度使用发送端给出的时间信息；正在播放的音频可在两次快照之间按本机经过的时间更新显示，暂停时保持不变，已知时长时不越过结尾。没有时长时显示未知时长，并在播放中使用不定进度条。
- 音乐远程控制使用当前已验证发送端的 DACP 服务和会话凭据。能力发现和命令执行在原生层进行，托管层根据能力位决定是否显示按钮，忙碌时暂时禁用已支持的按钮；界面不把无法确认的能力当作可用。
- [MainWindow.Playback](../../src/ClearCast/Views/MainWindow.Playback.cs) 处理电脑端播放控制和定位交互；原生层根据媒体来源执行 GStreamer 控制或发送 DACP 定位，并校验会话及内容代次。
- 定位滑块使用固定的 0–1000 界面范围，提交时映射到当前会话的实际毫秒范围，避免持续更改 WinUI 滑块边界。视频时长或定位能力尚未返回时隐藏定位滑块；不会发送推测的定位命令。
- 视频与音乐共用 [PlaybackSeekBar](../../src/ClearCast/Controls/PlaybackSeekBar.cs)，由 `RangeBase` 校验数值，使用有界的轨道与圆点布局，避开原生 `Slider` 冷显示零进度时的布局异常。鼠标、键盘和辅助功能的交互都走同一会话控制入口；丢失焦点、取消捕获或内容代次变化时取消未完成的定位。
- [MainWindow.Player](../../src/ClearCast/Views/MainWindow.Player.cs) 保持窗口、原生视频 HWND、全屏及恢复逻辑的单一实现。自动全屏只在视频由无到有时触发，音乐状态不进入这一分支。
- [MusicBackdrop](../../src/ClearCast/Controls/MusicBackdrop.cs) 对音乐封面和视频采样复用同一套 [ArtworkFlow](../../src/ClearCast/Controls/ArtworkFlow.cs) 纹理生成与 WinUI Composition 场景。音乐预计算 32 张 96 × 96 纹理，由单一相位时钟混合；视频按持续相位只生成当前一张纹理，保持相同局部扭曲和模糊。细节见[音乐背景](MUSIC_BACKGROUND.md)。视频通过新旧纹理的 100 毫秒交叉淡化过渡，底色在完全覆盖后更新；取消或替换时释放旧资源。
- [MainWindow.VideoBackdrop](../../src/ClearCast/Views/MainWindow.VideoBackdrop.cs) 在非全屏背景可见时通过独立 33 毫秒计时器请求采样；采样、纹理准备和过渡串行，避免更新积压或重叠淡化。原生 [clearcast_video_frame](../../src/native/clearcast_video_frame.h) 将当前视频 sink 的已解码样本转换成固定 32 × 32 BGRA 内存块；持有样本引用，在媒体锁之外转换，并在发布前复核 sink 代次。停止、换源或窗口关闭后的旧异步结果不能重新启用背景。

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

隔离 UI 构建通过 `tests/ui/MainWindow.MediaUiTests.cs` 检查音乐字段、真实图片解码、失效封面、窗口与全屏控制条位置、窄窗口、整窗视频背景及硬切画面的过渡中间帧；同时覆盖接收器在线且控件可用时，视频时长从未知变为已知、长视频范围更新及音乐拖动中的状态刷新。这里使用合成媒体状态，不发送真实播放控制。`tests/ui/direct_video_app_test.py` 使用正常应用、独立配对和本地 MP4/HLS，核对真实管线的暂停和定位，并检查窗口视频比例、整窗背景及播放中/停止后的设备发现。运行方式和截图边界见[托管与 UI 测试](testing.md)。报告、截图和日志保存在被忽略的 `.runtime/`，本文件不保存测试成绩。

真机验收仍需分别检查：

1. 从实际播放器投放音乐，确认音频、曲目信息、封面与暂停/恢复；验证发送端支持和不支持控制时的界面，并检查切歌和断开后没有旧状态。
2. 从兼容播放器投放普通、非 DRM 的视频，检查画面、音频、全屏、窗口恢复、暂停/继续及断开重连。
3. 在真实 iPhone、iPad、Mac、网络和 GPU 上检查镜像与视频播放。合成协议、软件解码或窗口布局测试不能代替音画同步、物理 GPU 或账号内容的验收。

## English

Select ClearCast from Screen Mirroring or a compatible player's AirPlay menu on the same local network. Music shows the metadata and artwork supplied by the sender. Progress is seekable when the sender supplies valid DACP playback-time information and confirms the requested position; rejected or ineffective seeks return to read-only progress. Transport buttons appear only when the sender offers usable controls; the entire transport area and its hint are hidden when remote control is unavailable. Compatibility depends on the device, player, and content.

The music content is centered in the page beside the left navigation. A muted cover-colored background extends across the whole app, including behind the title bar and navigation, while respecting the selected light or dark theme. Eight overlapping regions slowly bend and stretch at different positions and rhythms, with soft fading edges, and keep moving on Settings and About. Motion stops when paused, minimized, hidden, or when system animations are disabled; high contrast removes the colored background. The view has no volume, more, or lyrics controls.

**Settings → Playback → Automatically enter full screen** is off by default. It applies when a new video session starts, including mirroring, and never applies to audio-only music. Changing the setting during a session does not immediately change the window.

The current protocol scope includes existing mirroring and audio plus HTTP(S)/HLS video playback in the same video host. It does not establish support for AirPlay 2 multiroom, DRM, every streaming account, or every application. Automated parser, protocol, image, and window checks do not replace physical-device playback acceptance.

Direct video offers desktop pause/resume and seeking within the actual pipeline range. Screen mirroring has no transport controls. Auxiliary audio SETUP/FLUSH/closure does not replace a direct-video owner; URL playback does not depend on mirror feedback heartbeats. Device background behavior still requires physical-device acceptance.

Outside full screen, the entire video remains visible at its original aspect ratio with rounded corners. A blurred, warped background sampled from decoded video fills the whole app, including the title bar and navigation, using the same renderer as music. Background changes crossfade over 300 milliseconds instead of following scene cuts abruptly. Below the video, elapsed and remaining time flank the timeline. Disconnect is on the left, back ten seconds/play/forward ten seconds in the center, and rotation/full-screen actions on the right. Mirroring and live video omit transport and timeline controls. Space toggles supported video or music playback. Full screen uses an auto-hiding bottom overlay. The waiting page displays the verified sender's name when supplied and plays the provided 200-frame SVG loop at 25 fps.
