# 音乐背景、节奏调速与强拍脉冲

本文记录音乐背景的设计、来源、数据边界及验证入口。运行成绩、截图、日志和载荷哈希保存在已忽略的 `.runtime/`，不在本文累积。

## 视觉参考与实现范围

视觉方向参考 [Sam Henri Gold 的原始动态演示](https://x.com/samhenrigold/status/1765220903963574290)、[Aadish Verma 的背景研究](https://www.aadishv.dev/music)及 [AMLL 的动态背景文档](https://amll.dev/en/guides/component/background.html)。这些资料分别用于观察真实运动、理解封面纹理叠加与坐标扭曲、检查宿主对播放状态及资源生命周期的管理。研究文章也区分了网页与原生版本，不能据此断言 ClearCast 复现了 Apple 的内部实现。

ClearCast 使用独立编写的 C# 纹理生成和 WinUI Composition 场景，不引入网页播放器、AMLL 渲染依赖或反编译代码。参考的是封面结构被拉伸、局部扭曲并模糊融合的视觉行为。背景应保留封面的空间信息；相同调色板、不同图案排布的封面应产生不同纹理。

背景覆盖窗口内容、侧栏和标题栏，在音乐播放时切换设置或关于页继续运动。文字、按钮和封面卡片保持独立前景层，背景不参与命中测试。暂停、系统关闭动画、高对比度、不可见、最小化、卸载和释放仍由宿主控制。

## 封面纹理与单相位动画

封面先在有界尺寸下解码，再预计算 32 张 96×96 的循环相位纹理。每张纹理由多份封面采样、八处局部坐标扭曲、可分离模糊和主题安全处理组成。浅色主题增强封面色彩，以 1.20 的色度系数和 0.60 的封面混合权重保留颜色，并将过暗纹理向浅色中性底混合以保持约 175 的最低亮度；深色主题沿用原有色度、混合及亮度约束，不随本轮浅色调优改变。不将所有颜色通道裁成同一上限。局部扭曲改变采样坐标，不能以整体旋转、色块移动或只提取平均颜色替代。相位函数应周期连续，使末帧回到首帧时不出现空间跳变。

每张纹理输出不透明 BGRA 像素。32 张原始像素约占 1.125 MiB；Composition surface、暂存缓冲和封面切换期间的资源另计。预计算在工作线程完成，不在每个显示帧或音频回调中重新生成。新的封面、主题或会话使旧异步任务失效，过期任务不能覆盖当前内容，未采用的结果须释放。无可用封面时使用主题安全静态底色。

Composition 由一个 `Phase` 属性时钟推进全部纹理的不透明度表达式，只保存一个动画控制器。32 张 surface 可由 33 个 sprite 引用：底层首帧、其余相位帧，以及顶部复用首帧的别名。顶部别名覆盖末帧到首帧的交接，周期复位时由底层首帧接住。

任一时刻最多两层可见。原有流动由 33 个不透明度表达式与一个相位时钟组成，共 34 个动画通道；只有相位时钟使用调速控制器。独立强拍效果增加背景缩放和偏移的两个常驻表达式，正常运动共 36 个通道，360ms 脉冲标量动画运行期间为 37 个。强拍不可用时保留原流动；暂停状态不计为正在运行的动画通道。

相邻帧的目标混色为 `(1-t)A+tB`。采用 opaque-over 时，实际下层必须保持不透明，上层使用其目标权重；两层分别设置 `1-t` 和 `t` 会使底色透出，造成周期性变暗。首尾交接也必须遵守相同的总覆盖率，不能依赖背景底色掩盖接缝。

调速只更新同一控制器的 `PlaybackRate`，不写 `Progress`，不因音频快照重建场景或重新启动相位时钟。[Microsoft 的控制器接口](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionobject.trygetanimationcontroller?view=windows-app-sdk-1.8)可取得现有动画控制器；[PlaybackRate](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.animationcontroller.playbackrate?view=windows-app-sdk-1.8)用于改变播放速率。控制器不可用时使用稳定的基础速率，不按每个快照重复尝试或反复挂载动画。

## 从实际 PCM 得到音乐活动

原生音频链路在 `appsrc → queue → AAC/ALAC 解码 → audioconvert → audioresample → volume → level → sink` 中，于 `volume` 输入 pad 安装只读 probe。这里得到已解码、未受接收端输出音量缩放影响的 PCM。保持现有协商格式，不为背景强制 F32、双声道、采样率或新的 sink。[GStreamer 的 GstAudioInfo](https://gstreamer.freedesktop.org/documentation/audio/audio-info.html)用于解析实际采样格式、声道、布局和帧大小；不支持的格式只使活动值不可用，不影响播放。

分析使用固定状态的短窗全频与低频能量变化、自适应基线和瞬态不应期，汇总一段时间内的事件密度。它不是 BPM 估计器。持续响音、输出音量、RTP 包到达频率、歌曲进度和封面颜色都不能代替鼓点密度。声道按功率合并，避免反相立体声先相加后抵消。

probe 只读映射音频，工作量和暂存空间均有上限，不保留或写出 PCM，不访问文件、网络或 UI，不改变 pipeline 状态，也不等待媒体状态锁。映射、格式、数值或帧边界不合法时不发布活动值。[GStreamer 官方说明](https://gstreamer.freedesktop.org/documentation/application-development/advanced/pipeline-manipulation.html?gi-language=c)指出 probe 在流线程执行，阻塞或直接改变管线状态可能影响播放或产生死锁。

媒体快照通过可选的 `audioActivity` 对象传递 `valid`、`energy`、`density` 和 `ageMs`，并以 `beatEpoch` 和最多 16 条 `beats` 增补强拍历史。数值有界且必须有限；缺活动对象的旧快照按无活动处理，只有原四字段的快照仍可调速。活动仅允许用于当前纯音乐会话；暂停、镜像、直接视频或超过 900ms 未更新时无效。PCM 活动更新不增加歌曲文字、封面等元数据的 revision。

托管平滑器以瞬态密度为主要输入，能量只作小幅辅助。目标速率限制在温和正值区间，采用按经过时间计算的较快加速和较慢回落，避免轮询频率改变响应。无效、过期或缺失活动回落到基础速率；暂停和会话替换清除旧曲目的加速状态。系统动画及窗口可见性限制优先于活动值。

## 独立强低频脉冲

用户提出强鼓点使背景短促震动的补充需求。本轮查阅了 [tvOS 17 实机背景录屏](https://www.bilibili.com/video/BV1zB4y1Z7Jh/)；录屏可用于观察背景变化，不能据此测得音画逐拍同步或推断 Apple 的内部算法。[AMLL 作者的接口源码](https://github.com/amll-dev/applemusic-like-lyrics/blob/main/packages/core/src/bg-render/base.ts)将低频音量与流动速度分开，建议低频区间约 80–120Hz；其[网格片元着色器](https://github.com/amll-dev/applemusic-like-lyrics/blob/main/packages/core/src/bg-render/mesh-renderer/mesh.frag.glsl)使用低频输入改变纹理采样缩放，证明同类短促收放可以独立于持续流动实现。这是第三方开源实现的证据，不代表 Apple 原生实现。

强拍使用同一个 pre-volume PCM tap 中的独立低频检测器，以 45Hz 高通和 160Hz 二阶低通获取低频包络，依据包络陡增、低频占比、噪声门限、短时回落确认和 120ms 检测不应期判定，不复用原密度检测器的宽带瞬态条件。持续低音、较响人声或全频音量变化不能直接等同于强鼓点。声道仍按功率合并；强度保持有界，不把很弱的低频拍点一律归一化成最强脉冲。

每个已确认事件带非零递增 `sequence`、`strength` 和 `ageMs`，保存在固定 16 条环形历史中；读取不会消费历史。`beatEpoch` 绑定当前本机会话代次，旧来源或旧编码缓冲不能取得新代次。probe 在解码批量内逐事件记录采样位置，年龄包含缓冲内偏移和检测确认时间。历史只保存强度、身份与时间，不保存音频；原 8192 字节 JSON 上限保持不变。

同一缓冲中的事件先暂存在固定数组，整块 PCM 数值与边界、epoch 及 flushing 状态校验完成后才发布，避免尾部异常时提前提交前半段拍点。活动标量和事件历史分别使用独立短锁，锁内仅复制固定数据或更新标量，不取得媒体状态锁；快照格式化、分配和 JSON 拼接均在这些短锁之外进行。活动发布不因一次尝试获取锁失败而丢掉首个且唯一的有效窗口。

宿主在音乐背景实际可动画时，以 50ms 计时器读取活动；曲目信息、布局和播放进度仍使用正常 150ms 更新。快速路径只应用音频活动，常规路径在快速计时器运行期间不重复喂入旧历史。异步封面完成后重新判断计时器启停；停止接收、关闭窗口和背景不可动画时停止快速轮询。UI 取历史中最新事件，年龄同时计算原生 `ageMs` 与读取后的本地单调经过时间，250ms 到期后不再触发，迟到事件也会衰减强度。

控件按代次和序号去重，陈旧、较弱、关闭动画时收到或落在 200ms 短不应期内的事件均消费而不排队，避免恢复和卡顿后补播。新代次即使没有有效活动样本或拍点，也清除旧脉冲；合法的 `valid=false` 活动仍保留经过校验的新 `beatEpoch`。旧代次不能使状态回退。暂停、禁用系统动画、高对比度、隐藏、换封面及会话失效会停止脉冲并归中，保留已见身份防止旧拍重播。

视觉效果只施加到纹理父容器，前景封面、文字、进度和按钮保持稳定。一个预建 360ms 标量动画驱动缩放和偏移表达式：背景基础缩放为 1.05，强拍上限为 1.17，满强回弹的实际采样谷约为 1.0336，偏移不超过窗口对应尺寸的 0.8% 且最多 10 个逻辑像素。逐轴满足 `size × (scale - 1) / 2 ≥ abs(offset)`，保证回弹和偏移时不露底。

脉冲曲线预先采样 `exp(-6u) × sin(3πu)` 的阻尼响应，尾段再以 smoothstep 收束，按采样正峰归一，首尾明确为零。33 个线性 GPU 关键帧连接同一条曲线，避免在每个手写回弹点反复缓入、缓出而停顿。收到的强度已包含年龄衰减，再经 `s²(3-2s)` 单调映射，让弱拍克制、强拍更突出，不提前截成统一幅度。

200ms 不应期后的新事件可以接上尚未结束的 360ms 尾段。此时使用当前标量值作为动画起点并保留偏移方向，避免翻转方向向量造成位置跳变；只有静止或新代次归零后才选择新方向。这保证位置衔接，不把采样动画描述为速度连续的物理弹簧。重用模板不会重新生成纹理、重启 `Phase` 或改写其进度；不支持脉冲 API 时释放额外资源并保留原流动。

该效果响应已解码信号，不是节拍标注器，也不承诺每个音乐拍点都产生脉冲。PCM probe 位于输出 sink 之前，部分链路没有有效呈现时间戳，因此样本解码年龄不等于扬声器呈现年龄；不为背景更改现有音频同步或添加未经验证的固定延迟。实际音乐与设备延迟仍需真机验收。

## epoch 与来源隔离

本地 epoch 在编码 `GstBuffer` 进入 `appsrc` 前附加为自定义 `GstMeta`，随真实 AAC/ALAC 解码、转换和重采样到达 PCM probe。probe 只分析与当前 epoch 一致的非零标记；缺失、旧值、冲突或过多标记均不发布活动。不能在 PCM 到达时简单读取当前 owner，给已经排队的旧音频补贴新身份。

[GstAudioDecoder 的默认元数据规则](https://gstreamer.freedesktop.org/documentation/audio/gstaudiodecoder.html)支持复制无标签及仅带音频标签的元数据，但具体插件可以覆盖该行为。因此必须用本项目固定的 AAC、ALAC、audioconvert 和 audioresample 进行真实传播测试，不能只以 `gst_buffer_copy` 或纯 DSP 测试代替。

epoch 保护的是附加标记之后的排队路径。附加标记之前，RTP 实例在线程启动前绑定控制连接的来源 ID，再通过内部 `audio_decode_struct.clearcast_source_id` 传给编码回调。只有来源匹配当前 owner 才更新音乐活动、取得对应 epoch；来源、启用状态和 epoch 在读取前后复核。旧 producer 晚到的数据不发布活动，原有音频播放路径不因背景分析而改变。关闭连接时的 RTP 线程 join 和媒体 slot 限制是现有生命周期的一部分，但不能把所有路由都视为已有此保证。内部来源标记的传递不改变公开 receiver ABI。

暂停、flush、采样格式变化、DISCONT、seek、曲目及会话替换会清除分析历史并使旧 publication 失效。仅重置分析，不为背景引入 READY/PLAYING 循环、音频丢帧或设备重协商。GStreamer 的 [状态转换](https://gstreamer.freedesktop.org/documentation/additional/design/states.html)会停止流线程并改变协商和时钟状态，不能当作无影响的可视化操作。

`FLUSH_START` 与普通数据不同：[官方事件设计](https://gstreamer.freedesktop.org/documentation/additional/design/events.html)允许它不持流锁、与缓冲处理并发。此时只请求原子失效或重置，不能直接改写正在使用的非原子 DSP 状态；实际清理在串行的 `FLUSH_STOP` 或后续数据路径完成。失效后，正在结束的旧回调也不得重新发布旧活动。

## 代码与验证入口

| 职责 | 入口 |
| --- | --- |
| Composition 场景与生命周期 | [MusicBackdrop.cs](../../src/ClearCast/Controls/MusicBackdrop.cs) |
| 纯封面纹理与相位混合计算 | [ArtworkFlow.cs](../../src/ClearCast/Controls/ArtworkFlow.cs) |
| 纯托管调速平滑器 | [MusicMotion.cs](../../src/ClearCast/Controls/MusicMotion.cs) |
| 强拍去重、不应期与视觉边界 | [BeatImpulse.cs](../../src/ClearCast/Controls/BeatImpulse.cs) |
| 媒体快照解析与宿主状态 | [ReceiverMediaState.cs](../../src/ClearCast/Services/ReceiverMediaState.cs)、[MainWindow.Music.cs](../../src/ClearCast/Views/MainWindow.Music.cs) |
| 固定状态 PCM 分析 | [clearcast_audio_rhythm.h](../../src/native/clearcast_audio_rhythm.h) |
| PCM probe 与本地元数据 | [clearcast_audio_activity_gst.h](../../src/native/clearcast_audio_activity_gst.h)、[uxplay-audio-activity.patch](../../src/native/patches/uxplay-audio-activity.patch) |
| 会话与活动快照发布 | [clearcast_media_audio.h](../../src/native/clearcast_media_audio.h) |
| 纯调速测试 | [MusicMotionTests.cs](../../tests/managed/MusicMotionTests.cs) |
| 强拍 JSON 与纯脉冲测试 | [MediaBeatTests.cs](../../tests/managed/MediaBeatTests.cs)、[BeatImpulseTests.cs](../../tests/managed/BeatImpulseTests.cs) |
| 纯纹理与混合测试 | [ArtworkFlowTests.cs](../../tests/managed/ArtworkFlowTests.cs) |
| 实际插件解码与 epoch 传播夹具 | [audio_rhythm_gst_test.cpp](../../tests/native/audio_rhythm_gst_test.cpp) |
| 实际窗口合成捕获 | [MainWindow.CompositorUiTests.cs](../../tests/ui/MainWindow.CompositorUiTests.cs) |
| 强拍实际窗口与快速轮询检查 | [MainWindow.BeatUiTests.cs](../../tests/ui/MainWindow.BeatUiTests.cs) |

从仓库根目录运行纯托管测试：

```powershell
.runtime/dotnet/dotnet.exe run --project tests/managed/ManagedStateTests.csproj -c Release
```

按[原生测试说明](testing-native.md)设置 `$ucrt`、`$receiverBuild` 和受控依赖环境，再构建并运行纯 PCM 及真实解码管线夹具：

```powershell
& "$ucrt\bin\cmake.exe" --build $receiverBuild --target clearcast_audio_rhythm_test clearcast_audio_rhythm_gst_test
& "$ucrt\bin\ctest.exe" --test-dir $receiverBuild --output-on-failure -R '^audio_rhythm_(pcm|decoded_pipeline)$'
```

真实解码夹具使用本地合成音频、固定插件和 `fakesink`，不启动 AirPlay 服务或占用接收端口。实际窗口验证及截图按[托管与 UI 测试说明](testing.md)运行，与接收器测试串行，使用独立状态。

## 必须覆盖的边界

| 检查 | 验证重点 |
| --- | --- |
| 同 RMS 的稀疏、较密、密集鼓点 | 瞬态越密，稳定活动值与动画速率有序增大；同 BPM 的细分节奏也应产生区别 |
| 同节奏不同输入增益与输出音量 | 响度变化不能替代密度；输出音量不改变 pre-volume 分析 |
| 强低频瞬态与非鼓点对照 | 强低频短拍与持续低音、较弱拍点、高频瞬态和等响输入区分；批量解码保留各事件年龄 |
| 事件历史与托管时效 | 最多 16 条、uint64 身份不丢精度、严格递增且无重复；原生年龄加本地时间超过 250ms 不补播 |
| 持续响音、静音与缺失数据 | 持续响音不维持最高速；无新 PCM 后活动按失效窗口清除 |
| 声道、格式、采样率与缓冲切分 | 反相不抵消；同内容在合法格式及不同块大小下结果近似；异常输入不越界或产生非有限值 |
| 实际 AAC/ALAC 解码与重采样 | 当前 meta 保留；缺失、旧 meta 及多个冲突 epoch 不发布；PCM 内容、数量与 EOS 保持 |
| 来源与会话切换 | 旧编码 producer、旧队列 PCM、旧异步封面任务都不能污染新会话 |
| 封面空间结构与局部扭曲 | 同色直方图、不同排布得到不同纹理；局部变形不退化为整体旋转 |
| 相邻帧与首尾混合 | 全纹理 alpha 为 255；中点符合两帧平均；不露底、不变暗，末帧回首帧连续 |
| 动态调速 | 更新同一控制器；相位连续；不按快照重建 surface、场景或时钟 |
| 独立强拍视觉 | 强拍、回弹与归中只改变背景；不重启原相位，前景像素与布局稳定；重复或旧代次不触发 |
| 快速轮询与无样本 FLUSH | 50ms 仅读取并应用活动，不改写曲目信息；异步封面完成后启停正确；invalid 样本的新代次也撤销旧脉冲 |
| 主题、跨 Tab 与生命周期 | 浅深主题可读；设置和关于页继续运动；关闭动画、高对比度、隐藏、最小化及释放正确收敛 |

纯数值断言、真实插件管线和实际窗口图像分别证明不同层面的行为，不能互相替代。合成音频与自动截图也不能代替真实设备音乐、物理音频输出及主观视觉验收。
