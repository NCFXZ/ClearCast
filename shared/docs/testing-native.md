# 接收器测试

## 配对与重连集成测试

在 Windows 上运行，需要 Python 3 和 `cryptography`。先按[项目 README](../../README.md#准备-windows-接收运行时)构建接收运行时，再从仓库根目录执行以下命令。原生测试文件集中在 `tests/native/`，CMake 工程在 `src/native/`。

```powershell
python -m pip install cryptography
python .\tests\native\pairing_reconnect_test.py
```

**运行前关闭 ClearCast 及其他 AirPlay 接收器。** 测试会短暂启动真实接收服务，占用 TCP/UDP 47000–47002。若 Windows 提示防火墙权限，允许可信专用网络上的本地测试。

默认加载 `.runtime\receiver`。可以用 `CLEARCAST_PROBE_RUNTIME` 环境变量选择另一个完整运行时目录。测试在 `.runtime\qa-native-*` 新建独立状态，保留日志便于排查，不使用软件的真实配对数据。

测试会在导入 Python 的散列/加密扩展前，从所选运行时的绝对路径加载 OpenSSL 和 libplist，并记录实际加载模块的路径、SHA-256 和版本。若 Python 已预载其他目录下的同名 DLL，测试会拒绝继续；使用新的 Python 进程重试。

覆盖无保存设备时更新接收身份、跨 TCP 领取 PIN、来源地址限制、单活动连接独占领取、完整 SRP 配对、SETUP 前保存配对、跨 TCP 验证、拒绝未知设备，以及重启后保留已配对身份与记录。只有全部断言通过时才输出 `PASS`。这些检查不替代 iPhone/iPad 真机投屏验收。

## 首次可见验证码与失败连接恢复

```powershell
python tests/native/first_pin_retry_test.py
```

此夹具复现发送端先用另一凭据提交 SRP 证明、随后关闭该 TCP 的情况，再使用最初显示的同一验证码在新 TCP 上完成真实 SRP 和首次签名验证，无需重新发起验证码挑战。它同时检查来源地址和单活动认领限制、成功后消耗、旧连接关闭不能恢复旧代验证码、跨重连累计失败仍触发冷却，以及原始时限不会因重新交接延长。

完成一次完整时限检查后，可用 `--skip-expiry` 只重跑短协议场景；报告会明确标记跳过了真实等待的有效期检查。

该检查约需一分钟，包含真实等待：约第 55 秒交接仍有效，第 61 秒必须按原始创建时间失效。默认报告写入 `.runtime/qa-pairing/first-pin-retry-results.json`，可用 `CLEARCAST_FIRST_PIN_REPORT` 指定路径；报告不包含验证码、密钥或 SRP 正文。测试使用独立状态并占用接收端口，运行前关闭应用，且与其他接收器协议测试串行执行。

双栈切换另由不占端口的 CTest `pairing_dual_stack_handoff` 覆盖：独立 `clearcast_pairing_peer_fixture` DLL 使用生产配对处理器和真实 SRP，仅在测试编译单元中注入合成 Windows 邻居缓存。它复现 IPv6 发现、错误证明关闭、第一次人工 IPv4 重试，以及反向和交替切换；检查同代验证码、原始期限、单认领、成功消费与新旧代际隔离。直接运行时可指定 `--fixture <build>/clearcast_pairing_peer_fixture.dll` 执行 `tests/native/pairing_peer_test.py`，用 `CLEARCAST_PEER_REPORT` 设置报告位置。

该夹具还检查证明失败的反馈原因：前四次仍为 `ProofRejected`，第五次开始原有冷却的当次即为 `RateLimited`，无需等待第六次请求。此预算跨验证码代际和地址族共享，反馈调整不改变 HTTP 470、验证码验证、原始期限或冷却状态。

生产来源判断优先使用精确 IP 和 scope；只在跨 IPv4/link-local IPv6 时读取现有邻居缓存，要求唯一有效条目、相同接口及有效单播链路地址。缺项、多义或 API 错误继续拒绝，既不主动解析也不采信客户端声明的设备名。邻居缓存不是密码学身份，不能代替 SRP 证明，也不授予媒体控制权限。测试 DLL 不进入接收器或应用产物。

## 配对提示与验证码关联

使用同样的独立状态和运行时选择方式执行：

```powershell
python .\tests\native\pairing_feedback_test.py
```

检查旧密钥探测只提示需要验证、旧连接的拒绝不会关联到新验证码、错误证明只上报一次、不同来源无法领取当前验证码，以及成功配对、签名拒绝和不含敏感内容的阶段日志。结果默认写入 `.runtime/qa-pairing-feedback-results.json`，可用 `CLEARCAST_PAIRING_FEEDBACK_REPORT` 指定报告路径。测试同样占用接收端口，运行前需关闭其他接收器。

## 插件扫描器冷启动与取消

```powershell
python .\tests\native\plugin_scanner_lifecycle_test.py
```

每项检查都在新进程和独立状态中运行，验证实际启动的是随包扫描器、扫描器没有可见窗口、冷启动生成注册表并可接收请求，以及扫描期间停止和立即停止后没有遗留子进程。结果默认写入 `.runtime/qa-plugin-scanner-results.json`，可用 `CLEARCAST_SCANNER_REPORT` 指定路径；运行时仍由 `CLEARCAST_PROBE_RUNTIME` 选择。运行前关闭其他接收器。

## 安全、稳定性与生命周期

构建正式接收运行时后，关闭其他接收器，再运行独立状态下的真实 DLL 测试：

```powershell
$env:CLEARCAST_PROBE_RUNTIME = (Resolve-Path .runtime/receiver).Path
$env:CLEARCAST_QA_REPORT = Join-Path $PWD '.runtime/qa-native-security-results.json'
python tests/native/receiver_security_test.py
```

通常需要约 3–4 分钟，包含真实等待的 61 秒验证码过期和 61 秒失败冷却，以及完整启停、收到半个请求时停止、立即停止等循环。测试在 `.runtime/qa-native-*` 使用独立状态，保留 JSON 和日志供检查，不使用软件的真实 `.clearcast-state`。运行目录也可通过 `CLEARCAST_PROBE_RUNTIME` 指向另一个完整的候选运行时。

仅复查生命周期时可使用 `--lifecycle-only`：新建隔离配对后检查 40 次带未完成请求的重启、30 次立即停止及配对文件读取失败时的身份保护，不重复两个 61 秒等待。重启失败会报告具体轮次。整个测试期间须避免启动其他接收应用，否则重启间隙的新连接可能被另一实例接收。

覆盖验证码与签名校验、未验证媒体请求、畸形请求、慢速/不读响应客户端、日志大小与隐私、重复配对记录、配对文件读取失败、空闲 CPU 和启停资源回收。确定性变异和本机协议夹具不构成穷尽模糊测试，也不替代真实音视频流、硬件解码或网络条件测试。发布时应记录实际结果和对应 DLL 哈希。

## HTTP 解析器与请求头

`http_headers_test.c` 检查请求头字段名大小写；`http_request_security_test.c` 检查请求边界、分段和确定性畸形输入。两者共包含 10,163 项检查。使用接收器构建输出中实际的 `build` 目录：

```powershell
$ucrt = (Resolve-Path .runtime/msys64/ucrt64).Path # 或设为 C:\msys64\ucrt64
$receiverBuild = '<Build-Receiver.ps1 输出的实际 build 目录>'
$env:PATH = "$ucrt\bin;$env:PATH"
& "$ucrt\bin\cmake.exe" --build $receiverBuild --target clearcast_http_headers_test clearcast_http_security_test clearcast_video_context_test clearcast_srp_salt_fixture clearcast_media_audio_test clearcast_media_http_test clearcast_pairing_peer_fixture clearcast_audio_rhythm_test clearcast_audio_rhythm_gst_test
& "$ucrt\bin\ctest.exe" --test-dir $receiverBuild --output-on-failure
```

HTTP CTest 名称分别为 `http_headers_case_insensitive` 和 `http_request_security`。同次构建还包含 `video_context_lifecycle` 的 23 项上下文所有权、GPU 选择和设备移除夹具检查，上述三个 C 夹具共 10,186 项检查。它们不启动 AirPlay 接收服务，GPU 夹具也不替代真实硬件解码测试。

`srp_leading_zero_salt_proofs` 额外运行 11 项真实 SRP 证明检查：仅在独立测试 DLL 内固定随机 salt 的有效长度为 16、15、8、1 字节，使用独立客户端计算证明并验证服务器证明，覆盖错误 PIN 与旧截断编码的负向对照。生产 DLL 的随机数实现和导出不受测试夹具影响。该项要求 CMake 找到安装了 `cryptography` 的 Python；可通过 `-DPython3_EXECUTABLE=<python.exe>` 明确指定。结果默认写入 `.runtime/qa-srp-salt-results.json`。

## libplist 解析与复制边界

先使用 `scripts/native/Build-LibplistSecurity.ps1` 构建受控依赖；该构建也会运行上游完整 `make check`。额外的本地回归不占用接收端口：

```powershell
python tests/native/libplist_security_test.py --library .runtime/receiver/libplist-2.0.dll
```

覆盖 Unicode、整数与数据的二进制/XML 往返和复制，畸形 offset/reference/长度/嵌套边界，以及 1,000 个确定性有界变异。报告默认写入 `.runtime/qa-libplist-security-results.json`，可用 `--output` 指定。此检查验证实际加载的 2.8.0 DLL，不能替代穷尽模糊测试。

## 已解码音乐的节奏活动

`audio_rhythm_pcm` 直接生成合成 PCM，比较等 RMS 的不同低频脉冲密度、持续音、静音、输入增益变化、反相立体声和采样率。它检查由瞬态事件形成的密度，而不以 RTP 包数量或音量代替节奏。

`audio_rhythm_decoded_pipeline` 使用随包实际 AAC/ALAC 编解码器、`audioconvert`、`audioresample` 与生产 PCM tap，输出至 `fakesink`。夹具检查本机 epoch 标签保留、旧 source/缺失/混合标签拒绝，以及分析失败时仍传递音频；同时覆盖常见采样格式、声道、缓冲分段和 FLUSH 的分析清理。它不打开音频设备、不录制系统声音，也不保存 PCM。

```powershell
& "$ucrt\bin\cmake.exe" --build $receiverBuild --target clearcast_audio_rhythm_test clearcast_audio_rhythm_gst_test clearcast_media_audio_test
& "$ucrt\bin\ctest.exe" --test-dir $receiverBuild -R 'audio_rhythm_|media_audio_bounds' --output-on-failure
```

现有媒体状态夹具还检查 `audioActivity` 的 900 毫秒过期、暂停/切换会话/曲目/跳转时清理、旧 epoch 拒绝、有限数与区域设置无关的 JSON。密度用于背景运动，不是 BPM 或节拍位置估计；合成 AAC-LC/ALAC 与 `fakesink` 不能替代真机音乐播放及音画体验验证。

图形侧使用 32 张封面纹理相位、33 个图层且最多两层同时可见；运动时为 33 个不透明度表达式加一个相位时钟，共 34 个动画通道，由一个控制器调速。原生夹具验证 PCM、来源和活动快照，不验证这些 Composition 图层；图形结构、连续相位与实际窗口捕获见[音乐背景设计](MUSIC_BACKGROUND.md)和[托管与 UI 测试](testing.md)。

## 直接视频与 HTTP(S)/HLS 来源限制

原生测试目标 `clearcast_media_http_test` 使用生产 HTTP 来源策略，可由 CTest 的 `media_http_source_policy` 检查 URL 协议、凭据边界，以及策略启用后镜像和音频需要的 `appsrc` 是否仍可创建并通过 `need-data`、`push-buffer` 和 EOS 完整传递已知缓冲区。以下集成夹具从本地 FFmpeg 生成合成视频，只在随机回环端口运行 HTTP/HTTPS 服务：

```powershell
& "$ucrt\bin\cmake.exe" --build $receiverBuild --target clearcast_media_http_test
python tests/native/direct_video_http_test.py `
  --fixture "$receiverBuild/clearcast_media_http_test.exe" --runtime .runtime/receiver `
  --report .runtime/qa-media/direct-video-http-results.json
```

该项通过真实便携运行时解码 MP4/HLS 到 `fakesink`，验证实际帧数及尺寸，检查顶层和嵌套 HLS 重定向、带凭据 URL、文件和 `appsrc` URI 被拒绝，并分别验证不可信 TLS 证书拒绝与可信测试 CA 下的 HTTPS 播放。测试 CA 和私钥只存在于独立临时目录；生产运行时保留严格证书验证及随包 CA。

下面的夹具占用接收端口，必须与应用及其他真实 DLL 协议测试串行运行：

```powershell
python tests/native/direct_video_protocol_test.py --runtime .runtime/receiver `
  --report .runtime/qa-media/direct-video-protocol-results.json
```

它使用独立配对状态完成真实 PIN/SRP 和签名验证，再经 HTTP `/play` 将合成 MP4/HLS 送入实际接收 DLL 和共用 D3D11 子窗口，检查解码尺寸、已显示帧、对端与会话授权、暂停/定位/恢复、播放信息、停止/重连、解码方式切换及流结束。此项不访问真实设备或受 DRM 保护的内容，也不等同于物理显示、音频输出及各视频应用的兼容性验收。

## 音乐元数据、封面、进度与发送端控制

CTest 的 `media_audio_bounds` 使用生产音频媒体桥接头文件，检查有界 DMAP 标签、UTF-8 与 JSON 转义、PNG/JPEG 尺寸和像素上限、封面版本与缓冲区边界、RTP 进度回绕、播放状态和会话清理，以及 DNS 压缩名、服务实例和凭据校验。默认检查不启动网络服务；加入 `--network` 后，在随机回环端口提供模拟 DACP 发送端：

```powershell
& "$ucrt\bin\cmake.exe" --build $receiverBuild --target clearcast_media_audio_test
& "$receiverBuild/clearcast_media_audio_test.exe" --network
```

回环用例检查固定控制命令、IPv6 Host、拒绝响应、重定向拒绝、响应头和 Content-Length 上限、截断响应与超时，以及发送端返回暂停状态时持续收到 RTP 数据不会错误恢复播放。成功的控制请求不直接切换本地播放状态；有有效 DACP 状态时使用发送端状态，否则依据 RTP 活动与 FLUSH 更新。压缩封面只保留在当前会话内存中，不写入接收日志。

以下协议夹具使用完整接收 DLL 和独立配对状态，占用 TCP/UDP 47000–47002，必须与应用及其他接收器测试串行运行：

```powershell
python tests/native/media_metadata_protocol_test.py
```

它覆盖未验证请求拒绝、验证后的有界畸形 DMAP/图像/进度请求、媒体快照和封面 ABI 边界、不记录曲名及停止后清理。默认报告为 `.runtime/qa-media/metadata-protocol-results.json`，可通过 `CLEARCAST_MEDIA_PROTOCOL_REPORT` 更改。该项没有发送加密 RTP 音频；它与内存夹具及回环 DACP 用例不能替代真实 iPhone/iPad/Mac 音乐投放、歌曲切换、设备音频和不同发送应用的验收。

媒体 JSON 顶层的 `connected` 表示仍有存活的已认证控制连接，或已建立音频、镜像或直接视频会话；未认证 TCP、仅完成 PIN 设置及已关闭连接的 60 秒授权缓存均不算在线。协议夹具检查认证后尚无媒体时为真、关闭一个兄弟连接仍为真、最后控制连接关闭且无媒体时及接收器停止后为假；暂停和音频 SETUP 尚无首包的边界由内存状态夹具覆盖。
