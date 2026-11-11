# AirPlay 音乐与视频

## 使用方法

将发送设备与 Windows 电脑连接到允许设备互访的同一局域网，打开 ClearCast。在设备的「屏幕镜像」或兼容播放器的 AirPlay 选项中选择 ClearCast，按电脑提示完成验证码配对。

控制中心「正在播放」中的 AirPlay 音频输出选择，不等同于开启「屏幕镜像」，也不能要求每个视频 App 随后自动发送视频。希望连接后显示手机整体画面时，选择「控制中心 → 屏幕镜像 → ClearCast」；希望直接投放视频时，使用视频 App 自身的 AirPlay 入口。接收端只能播放发送端实际提供的音频、镜像或视频来源。操作区别见 Apple 的[音频输出说明](https://support.apple.com/zh-cn/105068)及[视频投放与屏幕镜像说明](https://support.apple.com/zh-cn/102661)。

音乐连接会显示发送端提供的封面、曲名、歌手、专辑和进度。没有提供的信息使用占位内容。发送端返回有效的 DACP 定位状态时显示可拖动进度；定位后必须确认实际播放位置，拒绝或未生效时恢复只读进度。播放/暂停、上一首、下一首按钮按发送端能力显示；不支持远程控制时可继续在发送设备上操作。

音乐内容在左侧导航以外的页面区域内居中，依次展示封面、曲名、歌手、进度和上一首/播放/下一首。封面颜色形成柔和的背景并缓慢流动，覆盖标题栏和左侧导航后的整个应用区域；切换到「设置」或「关于」后仍继续流动。背景遵循当前浅色或深色主题，不改变用户的主题选择。音乐暂停、窗口最小化或隐藏、关闭系统动画时停止流动，高对比度模式关闭彩色背景。界面不增加音量或更多入口。

发送端支持远程控制时，进度下方一行中央为上一首/播放/下一首，左侧为全屏，右侧为歌词开关，下一行为「断开连接」；不支持时隐藏中央按钮，全屏和歌词开关移到「断开连接」两侧。音乐全屏隐藏标题栏和导航，按 Esc、再次点击按钮或音乐结束时退出；之后开始的视频按视频规则重新进入全屏。每首歌开始后自动查找歌词：查找中或未找到时歌词按钮置灰，找到后可用。歌词默认不显示，显示与否随设置保存；开启状态下找到歌词会自动展开。展开时播放器从居中位置平滑缩放移动到左侧，歌词淡入并从右侧滑入；收起时歌词先在原位淡出，播放器同时回到居中。两者都使用缓出曲线，关闭系统动画时直接切换。宽度足够时左右分栏，窗口较窄时歌词位于播放器下方。切歌时已展开的歌词区保持展开，旧歌词淡出，新歌词到达后淡入；新歌没有歌词时再收起。

AirPlay 音频不传送歌词，ClearCast 使用发送端的曲名、歌手、专辑和时长向 [LRCLIB](https://lrclib.net) 查找。依次尝试精确匹配、曲名加歌手搜索，以及去掉括号后缀的曲名搜索；最后一步必须由时长确认。搜索结果优先选择带时间轴的歌词，时长相差超过 2 秒的版本不采用。带时间轴的歌词按播放进度逐行高亮：当前行完全不透明并恢复原大小，其他行约 30% 不透明并缩小到 96%。换行时各行以 700 毫秒强缓出曲线上移，当前行之后的各行依次延迟 45 毫秒跟随，形成层叠滚动；当前行保持在歌词区上部约 30% 处。字号随歌词区宽度在 22–44 DIP 之间变化，粗体，行高 1.2 倍，段间距约 0.6 倍字号。纯文本歌词使用相同的字号、字重、行高和行距，底部另行显示「此歌词不支持滚动」；无时间轴时不自动跟随，仍可手动浏览。纯音乐按无歌词处理。手动滚动直接更新位置，浏览期间播放换行不会移动视口；滚轮输入停止或触控手势结束 3 秒后恢复自动跟随。非当前行按与当前行的距离加高斯模糊（相邻行 0.7 DIP，每远一行增加 0.7 DIP，最多 4 DIP），并随换行缓出过渡；手动滚动浏览时所有行恢复清晰。歌词区上方约 16% 与下方约 22% 渐隐。高对比度模式关闭模糊和渐隐。关闭系统动画时直接定位。逐字高亮未实现。歌词与发送端或其他播放器显示的时间轴可能存在差异；匹配率取决于 LRCLIB 的收录情况。

镜像和视频共用现有画面窗口，可使用全屏、旋转及显示大小选项。在「设置 → 播放显示」开启「投屏时自动全屏」后，下一次视频开始时进入全屏；此选项默认关闭，对纯音乐无效。当前会话中修改此设置不会立即改变窗口，手动退出全屏后也不会因后续画面更新再次进入。

封面、曲目信息、进度和控制能力取决于发送设备、播放器及内容。不同应用的 AirPlay 实现和权限要求不同，不能将某个协议或本地测试的结果推广到所有播放器。

直接视频提供电脑端暂停/继续和进度控制。只有播放管线报告有效可定位范围时才能拖动；屏幕镜像不显示这些控件。拖动和方向键调整完成后提交定位，轮询不会覆盖正在拖动的位置；换源、切歌或连接结束会使旧请求失效。

直接视频的 HTTP(S)/HLS 解码由 GStreamer 负责。ClearCast 禁用 UxPlay 的旧 `mlhls://`、`/action` 播放列表改写及本地桥接路由，避免进入不需要的旧解析器；普通 HTTP(S) 媒体和已有安全来源的缓存续播继续使用现有路径。资源限额及安全边界见[稳定性与安全说明](RELIABILITY_SECURITY.md)。

有效的新视频播放请求会准备并自动启动当前视频，无须另行补发继续播放指令。管线创建与播放状态更新共用生命周期锁，避免创建期间的最新状态被旧值覆盖；准备完成与播放速率按当前代管线实际状态报告。加载期间明确收到的暂停仍然生效，不能通过定时强制续播覆盖用户的暂停操作。HTTP `/server-info` 保留兼容的上游能力掩码 `0x27F`，RTSP 和 TXT 使用各自既有的发现配置；不能直接把完整能力位复制到旧 HTTP 回复，这已在真实发送端触发过不同握手且不再发送视频的回归。

非全屏时，视频保持完整比例，在画面区域内居中，四角采用与音乐封面相同的圆角；比例不同产生的空余区域显示动态背景。控制条独立放在视频下方，已播放和剩余时间分别位于进度条两端。下一行左侧为「断开连接」，中央为后退 10 秒、播放/暂停和前进 10 秒，右侧为向左/右旋转及全屏。镜像和直播只显示断开、旋转和全屏，包括带有 DVR 定位窗口的直播；有限时长视频可暂停，不支持定位时保留只读进度。全屏控制条改为画面内的底部浮层，空闲时隐藏，暂停、拖动、鼠标停留或键盘操作时保持可见。

播放时，在 AirPlay 页面按 F12 可显示或隐藏媒体信息，默认隐藏。信息字体为 11 DIP，背景半透明。窗口模式贴齐实际可见视频左上角，不留外边距，随缩放、旋转和原尺寸裁剪更新；全屏模式距屏幕左上角保留 16 DIP，控制条隐藏后仍保留。面板按可用范围限制大小。XAML 按当前 DPI 栅格化信息面板，通过单个分层窗口以预乘 Alpha 叠在视频上，透明部分直接透出视频，不对画面挖孔；关闭、隐藏、换源及过期异步捕获均受当前状态约束。音乐显示在专辑封面正上方。视频可显示源分辨率、源帧率、视频编码与码率，以及伴随音轨的编码、采样率、声道数与码率；纯音乐显示音频信息。源帧率来自协商的媒体格式；另外每秒从现有视频 sink 计数计算渲染帧率，并显示该 sink 累积丢帧数，首次取样或计数重置时等待下一次有效取样。码率来自媒体流报告的标签，不是网络吞吐量，也不根据解码后的像素或 PCM 推算。未知字段不显示，尚无信息时显示等待提示。信息仅用于当前内存中的播放状态，不保存到用户设置；断开后隐藏，切换媒体重新读取。

前后 10 秒由原生层在命令执行时读取当前实际播放位置，再增加或减少 10 秒，不使用界面轮询缓存的位置。定位采用准确模式，避免吸附到附近关键帧。上一次定位尚未完成时，连续点击按最新已接受的目标累计；完成后重新使用实际播放时间。目标限制在当前有限时长视频的可定位范围内，并继续校验会话及内容代次；镜像和直播不使用此操作。

在 AirPlay 页面按空格键可触发当前可用的播放/暂停操作，包括视频原生画面取得焦点时；音乐要求发送端支持远程播放控制。WinUI 预览键盘事件先消费空格按下及松开，避免已获焦的旋转、全屏或断开按钮执行默认点击；长按只切换一次。镜像和直播会消费空格，但不执行播放控制或其他按钮操作；设置和关于页保留正常键盘行为。

等待页在连接后显示发送设备名称；名称只接受已验证连接提供的 SETUP 元数据或客户端名称头，缺失时保留通用 AirPlay 连接提示，断开后清除。等待页插画使用用户提供的 200 帧、25 fps、8 秒循环纯矢量 SVG，通过现有原生 `SvgImageSource` 组件按显示 DPI 解码；离开页面或最小化时暂停。

视频背景复用音乐的纹理扭曲、模糊和连续流动，覆盖标题栏、导航和控制区的整个应用界面，切换设置或关于页也保留。它使用当前解码画面的低分辨率内存采样，不裁切前景视频，不保存视频截图。画面更新经过 100 毫秒交叉淡化，旧背景保留到新背景完全覆盖，避免镜头突变时背景跟着闪变。独立 33 毫秒计时器在前一次处理和过渡结束后取最新画面；每次只生成当前相位纹理，减少准备时间。实际响应还包含取样、纹理上传和合成调度时间，不能将淡化时长视为整条链路的硬上限。全屏保持常规视频显示；系统动画、高对比度和窗口可见性约束仍然生效。

纯音乐 ALAC 通道使用发送端 RTP/NTP 映射的播放时间戳，由音频输出管线按时调度。接收后立即输出会使电脑声音领先发送端歌词，因此该通道启用时间戳同步；不添加猜测的固定延迟，不改变直接视频播放管线。手机歌词的呈现时序由发送应用控制，仍需真实设备确认。

手机锁屏可能重新准备辅助音频流。该流的 SETUP、FLUSH 或退出不会替代直接视频的生命周期，URL 播放也不依赖镜像的 `/feedback` 心跳。真正的停止、媒体结束和媒体源错误仍会结束直接视频；发送端及其媒体源本身的后台可用性仍需真机验证。

歌词候选优先考虑相同专辑，再选择逐行歌词和接近的时长；专辑标注不同不再一律拒绝。精确查询和歌手限定搜索允许接口返回别名及缺失的时长，明确不合法或相差超过 2 秒的时长仍不采用；宽泛曲名搜索要求时长，并在有歌手时核对其合理别名。简繁体通过 Windows 文字映射进行等价比较，查询也保留去重后的简体/繁体变体；必要时搜索歌手与专辑，再核对完整曲名，以减少单个字的语境转换差异。所有歌词底部统一显示「歌词由 LRCLIB 提供，内容及时间轴可能不准确。」数据库内容本身错误或发送端时间偏差无法完全自动识别。

歌词图形尺寸仅在实际布局变化时刷新；滚动复用动画模板，并只向原有可见预留范围提交图形更新，不重复启动相同强调状态的动画。层叠延迟、缓出曲线、模糊、渐隐与展开/收起参数保持原样。

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
- [MainWindow.MediaDiagnostics](../../src/ClearCast/Views/MainWindow.MediaDiagnostics.cs) 共用 F12 切换及中英文格式化，原生视频子窗口和 WinUI 控件取得焦点时均可操作，长按不重复切换。信息面板由单个分层窗口按预乘 Alpha 合成在原生视频上，位图按文本、尺寸或 DPI 变化更新，避免双重 DPI 缩放；原生视频区域只为全屏控制条留出 XAML 显示区域；[clearcast_media_diagnostics](../../src/native/clearcast_media_diagnostics.h) 只从实际活动管线的解码器格式及媒体标签读取参数，可选快照字段沿用 8 KiB 总上限及托管边界校验。
- [Lyrics](../../src/ClearCast/Services/Lyrics.cs) 解析 LRC 时间标签与 `offset`，最多保留 2000 行、每行 400 字符并去除控制字符。[LyricsClient](../../src/ClearCast/Services/LyricsClient.cs) 只访问 `https://lrclib.net/api/`，保留默认 TLS 校验，不跟随重定向，超时 15 秒，响应解压后最多 1 MiB；本次运行在内存中缓存最多 64 首结果，网络失败不缓存。[MainWindow.Lyrics](../../src/ClearCast/Views/MainWindow.Lyrics.cs) 在曲目变化 600 毫秒后查询，用代次丢弃旧曲目的结果，并按下一行的时间设置单次计时器，不增加持续轮询；展开和收起记录播放器前后布局，以 Composition 平移和缩放过渡。[LyricsView](../../src/ClearCast/Controls/LyricsView.cs) 只用隐藏的 XAML 文本排版，每行通过 `CompositionVisualSurface` 绘制到独立精灵，再由渐变遮罩合成整块歌词区；逐行移动、强调、模糊和淡入都是 Composition 动画，不使用 ScrollViewer 滚动。[LyricsBlurEffect](../../src/ClearCast/Controls/LyricsBlurEffect.cs) 以固定虚函数表实现 Direct2D 高斯模糊所需的 `IGraphicsEffectD2D1Interop`，不引入 Win2D；效果描述在创建时一次性准备属性值并聚合系统自由线程封送器，视图在生命周期内持有它；效果工厂未编译成功时歌词保持清晰，其余行为不变。歌词的计时器、网络查找续接和收起动画回调捕获意外异常，写入 `.clearcast-state/ui-error.log` 并在本次运行中停用歌词，不中断播放。
- [MusicBackdrop](../../src/ClearCast/Controls/MusicBackdrop.cs) 对音乐封面和视频采样复用同一套 [ArtworkFlow](../../src/ClearCast/Controls/ArtworkFlow.cs) 纹理生成与 WinUI Composition 场景。音乐预计算 32 张 96 × 96 纹理，由单一相位时钟混合；视频按持续相位只生成当前一张纹理，保持相同局部扭曲和模糊。细节见[音乐背景](MUSIC_BACKGROUND.md)。视频通过新旧纹理的 100 毫秒交叉淡化过渡，底色在完全覆盖后更新；取消或替换时释放旧资源。
- [MainWindow.VideoBackdrop](../../src/ClearCast/Views/MainWindow.VideoBackdrop.cs) 在非全屏背景可见时通过独立 33 毫秒计时器请求采样；采样、纹理准备和过渡串行，避免更新积压或重叠淡化。原生 [clearcast_video_frame](../../src/native/clearcast_video_frame.h) 将当前视频 sink 的已解码样本转换成固定 32 × 32 BGRA 内存块；持有样本引用，在媒体锁之外转换，并在发布前复核 sink 代次。停止、换源或窗口关闭后的旧异步结果不能重新启用背景。

媒体状态、曲目信息、封面、歌词和控制凭据用于当前播放，不写入偏好文件或连接日志；播放音乐时曲目信息和时长会自动发送给 LRCLIB 查找歌词。偏好文件保存包括自动全屏在内的用户设置；配对记录仍沿用现有本地保存方式。数据处理范围见[隐私政策](legal/PRIVACY.md)。

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

Direct video offers desktop pause/resume and seeking within the actual pipeline range. Ten-second skips read the actual pipeline position when each command executes and use accurate seeking instead of snapping to a nearby keyframe. While a seek is pending, consecutive skips accumulate from the latest accepted target; after completion they use the advancing playback position again. Targets stay within the current finite video's seekable range and retain session/content validation. Screen mirroring has no transport controls. Auxiliary audio SETUP/FLUSH/closure does not replace a direct-video owner; URL playback does not depend on mirror feedback heartbeats. Device background behavior still requires physical-device acceptance.

On the AirPlay page, Space toggles supported video or music playback once per press. Preview key-down and key-up handlers consume it before a focused rotation, full-screen or disconnect button can perform its default action. Mirroring and live video consume Space without invoking playback or another action. Settings and About retain normal keyboard behavior.

Outside full screen, the entire video remains visible at its original aspect ratio with rounded corners. A blurred, warped background sampled from decoded video fills the whole app, including the title bar and navigation, using the same renderer as music. Background changes crossfade over 300 milliseconds instead of following scene cuts abruptly. Below the video, elapsed and remaining time flank the timeline. Disconnect is on the left, back ten seconds/play/forward ten seconds in the center, and rotation/full-screen actions on the right. Mirroring and live video omit transport and timeline controls. Space toggles supported video or music playback. Full screen uses an auto-hiding bottom overlay. The waiting page displays the verified sender's name when supplied and plays the provided 200-frame SVG loop at 25 fps.

Press F12 on the AirPlay page during playback to toggle media information. The 11-DIP information panel sits flush with the actual video upper-left corner in a window, and 16 DIP from the screen upper-left corner in full screen. A single layered window alpha-composites its WinUI-rendered text and translucent rounded background over the native video without cutting out the picture. Its size stays within the available bounds. Music places it directly above the artwork. Available fields include source resolution/frame rate, video/audio codecs and reported stream bitrates, sample rate and channel count. Rendered frame rate is sampled from the existing video sink counters once per second, alongside its cumulative dropped-frame count. Source frame rate is the negotiated format rate; bitrate is stream metadata, not measured network throughput. Unknown fields are omitted. The panel starts hidden, stays independent of the auto-hiding controls, and hides when playback ends.
