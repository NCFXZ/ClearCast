# 接收器测试

## 配对与重连集成测试

在 Windows 上运行，需要 Python 3 和 `cryptography`。先按项目 README 构建接收运行时，再执行：

```powershell
python -m pip install cryptography
python .\native\tests\pairing_reconnect_test.py
```

**运行前关闭 ClearCast 及其他 AirPlay 接收器。** 测试会短暂启动真实接收服务，占用 TCP/UDP 47000–47002。若 Windows 提示防火墙权限，允许可信专用网络上的本地测试。

默认加载 `.runtime\receiver`。可以用 `CLEARCAST_PROBE_RUNTIME` 环境变量选择另一个完整运行时目录。测试在 `.runtime\qa-native-*` 新建独立状态，保留日志便于排查，不使用软件的真实配对数据。

覆盖无保存设备时更新接收身份、跨 TCP 领取 PIN、来源地址限制、一次性领取、完整 SRP 配对、SETUP 前保存配对、跨 TCP 验证、拒绝未知设备，以及重启后保留已配对身份与记录。只有全部断言通过时才输出 `PASS`。这些检查不替代 iPhone/iPad 真机投屏验收。

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
& "$ucrt\bin\cmake.exe" --build $receiverBuild --target clearcast_http_headers_test clearcast_http_security_test clearcast_video_context_test
& "$ucrt\bin\ctest.exe" --test-dir $receiverBuild --output-on-failure
```

HTTP CTest 名称分别为 `http_headers_case_insensitive` 和 `http_request_security`。同次构建还包含 `video_context_lifecycle` 的 23 项上下文所有权、GPU 选择和设备移除夹具检查，完整 CTest 共 10,186 项检查。它们不启动 AirPlay 接收服务，GPU 夹具也不替代真实硬件解码测试。
