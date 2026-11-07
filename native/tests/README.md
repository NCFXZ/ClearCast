# 接收器测试

## 配对与重连集成测试

在 Windows 上运行，需要 Python 3 和 `cryptography`。先按项目 README 构建接收运行时，再执行：

```powershell
python -m pip install cryptography
python .\native\tests\pairing_reconnect_test.py
```

**运行前关闭 ClearCast 及其他 AirPlay 接收器。** 测试会短暂启动真实接收服务，占用 TCP/UDP 47000–47002。若 Windows 提示防火墙权限，允许可信专用网络上的本地测试。

默认加载 `.runtime\receiver`。可以用 `CLEARCAST_PROBE_RUNTIME` 环境变量选择另一个完整运行时目录。测试在 `.runtime\qa-native-*` 新建独立状态，保留日志便于排查，不使用软件的真实配对数据。

测试会在导入 Python 的散列/加密扩展前，从所选运行时的绝对路径加载 OpenSSL 和 libplist，并记录实际加载模块的路径、SHA-256 和版本。若 Python 已预载其他目录下的同名 DLL，测试会拒绝继续；使用新的 Python 进程重试。

覆盖无保存设备时更新接收身份、跨 TCP 领取 PIN、来源地址限制、一次性领取、完整 SRP 配对、SETUP 前保存配对、跨 TCP 验证、拒绝未知设备，以及重启后保留已配对身份与记录。只有全部断言通过时才输出 `PASS`。这些检查不替代 iPhone/iPad 真机投屏验收。

## 配对提示与验证码关联

使用同样的独立状态和运行时选择方式执行：

```powershell
python .\native\tests\pairing_feedback_test.py
```

检查旧密钥探测只提示需要验证、旧连接的拒绝不会关联到新验证码、错误证明只上报一次、不同来源无法领取当前验证码，以及成功配对、签名拒绝和不含敏感内容的阶段日志。结果默认写入 `.runtime/qa-pairing-feedback-results.json`，可用 `CLEARCAST_PAIRING_FEEDBACK_REPORT` 指定报告路径。测试同样占用接收端口，运行前需关闭其他接收器。

## 插件扫描器冷启动与取消

```powershell
python .\native\tests\plugin_scanner_lifecycle_test.py
```

每项检查都在新进程和独立状态中运行，验证实际启动的是随包扫描器、扫描器没有可见窗口、冷启动生成注册表并可接收请求，以及扫描期间停止和立即停止后没有遗留子进程。结果默认写入 `.runtime/qa-plugin-scanner-results.json`，可用 `CLEARCAST_SCANNER_REPORT` 指定路径；运行时仍由 `CLEARCAST_PROBE_RUNTIME` 选择。运行前关闭其他接收器。

## 安全、稳定性与生命周期

构建正式接收运行时后，关闭其他接收器，再运行独立状态下的真实 DLL 测试：

```powershell
$env:CLEARCAST_PROBE_RUNTIME = (Resolve-Path .runtime/receiver).Path
$env:CLEARCAST_QA_REPORT = Join-Path $PWD '.runtime/qa-native-security-results.json'
python native/tests/receiver_security_test.py
```

通常需要约 3–4 分钟，包含真实等待的 61 秒验证码过期和 61 秒失败冷却，以及完整启停、收到半个请求时停止、立即停止等循环。测试在 `.runtime/qa-native-*` 使用独立状态，保留 JSON 和日志供检查，不使用软件的真实 `.clearcast-state`。运行目录也可通过 `CLEARCAST_PROBE_RUNTIME` 指向另一个完整的候选运行时。

覆盖验证码与签名校验、未验证媒体请求、畸形请求、慢速/不读响应客户端、日志大小与隐私、重复配对记录、配对文件读取失败、空闲 CPU 和启停资源回收。确定性变异和本机协议夹具不构成穷尽模糊测试，也不替代真实音视频流、硬件解码或网络条件测试。发布时应记录实际结果和对应 DLL 哈希。

## HTTP 解析器与请求头

`http_headers_test.c` 检查请求头字段名大小写；`http_request_security_test.c` 检查请求边界、分段和确定性畸形输入。两者共包含 10,163 项检查。使用接收器构建输出中实际的 `build` 目录：

```powershell
$ucrt = (Resolve-Path .runtime/msys64/ucrt64).Path # 或设为 C:\msys64\ucrt64
$receiverBuild = '<Build-Receiver.ps1 输出的实际 build 目录>'
$env:PATH = "$ucrt\bin;$env:PATH"
& "$ucrt\bin\cmake.exe" --build $receiverBuild --target clearcast_http_headers_test clearcast_http_security_test clearcast_video_context_test clearcast_srp_salt_fixture
& "$ucrt\bin\ctest.exe" --test-dir $receiverBuild --output-on-failure
```

HTTP CTest 名称分别为 `http_headers_case_insensitive` 和 `http_request_security`。同次构建还包含 `video_context_lifecycle` 的 23 项上下文所有权、GPU 选择和设备移除夹具检查，完整 CTest 共 10,186 项检查。它们不启动 AirPlay 接收服务，GPU 夹具也不替代真实硬件解码测试。

`srp_leading_zero_salt_proofs` 额外运行 11 项真实 SRP 证明检查：仅在独立测试 DLL 内固定随机 salt 的有效长度为 16、15、8、1 字节，使用独立客户端计算证明并验证服务器证明，覆盖错误 PIN 与旧截断编码的负向对照。生产 DLL 的随机数实现和导出不受测试夹具影响。该项要求 CMake 找到安装了 `cryptography` 的 Python；可通过 `-DPython3_EXECUTABLE=<python.exe>` 明确指定。结果默认写入 `.runtime/qa-srp-salt-results.json`。

## libplist 解析与复制边界

先使用 `native/Build-LibplistSecurity.ps1` 构建受控依赖；该构建也会运行上游完整 `make check`。额外的本地回归不占用接收端口：

```powershell
python native/tests/libplist_security_test.py --library .runtime/receiver/libplist-2.0.dll
```

覆盖 Unicode、整数与数据的二进制/XML 往返和复制，畸形 offset/reference/长度/嵌套边界，以及 1,000 个确定性有界变异。报告默认写入 `.runtime/qa-libplist-security-results.json`，可用 `--output` 指定。此检查验证实际加载的 2.8.0 DLL，不能替代穷尽模糊测试。
