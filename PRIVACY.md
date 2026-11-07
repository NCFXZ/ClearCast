# ClearCast privacy policy / 隐私政策

## English

ClearCast receives the screen and audio that you choose to mirror from an AirPlay device on your local network. It decodes and plays the stream on your Windows PC. ClearCast does not record the stream, send it to the developer, or use it for analytics or advertising.

ClearCast advertises its receiver name, which includes your PC name, to devices on the local network so you can find it in the AirPlay picker. It uses a short-lived PIN to pair new devices. To recognize previously paired devices, it stores their identifiers, public keys, names when provided, and pairing timestamps in local app data on the Windows PC. It also stores its receiver key and your app settings locally.

To diagnose connection failures, ClearCast keeps a small local connection log with timestamps, local network addresses and receiver ports, request types, response status codes, and pairing stages, challenge counters, and outcomes. It excludes screen and audio data, pairing codes, keys, proofs, and request bodies. The log rotates automatically and is included when you choose **Copy details**.

You can view and remove individual or all paired devices in **Settings → Device pairing**. Removing a pairing removes its saved entry and requires the device to pair again. ClearCast also renews its receiver identity, so other saved devices may need to verify again. You can stop mirroring from either device. ClearCast does not share saved pairings with the developer or a cloud service. Links that you choose to open on GitHub are subject to GitHub’s privacy policy.

For questions or requests about your ClearCast data, contact the developer through [ClearCast on GitHub](https://github.com/NCFXZ/ClearCast).

## 中文

ClearCast 在局域网上接收你主动通过 AirPlay 屏幕镜像共享的画面和音频，并在 Windows 电脑上解码播放。ClearCast 不录制镜像内容，不向开发者发送镜像内容，也不将其用于分析或广告。

ClearCast 在局域网上广播包含电脑名称的接收器名称，方便你在 AirPlay 列表中找到它。新设备通过短时有效的验证码配对。为识别已配对设备，ClearCast 在 Windows 电脑的本地应用数据中保存设备标识、公钥、设备提供的名称和配对时间。接收器密钥和应用设置也保存在本地。

为排查连接失败，ClearCast 在本地保留少量连接日志，记录时间、局域网地址和接收端口、请求类型、响应状态码，以及配对阶段、验证码请求计数和结果，不包含镜像影音、验证码、密钥、验证证明或请求正文。日志自动轮转；点击“复制详情”时会附带连接日志。

你可以在 **设置 → 设备配对** 中查看设备，删除单个设备或清除全部配对记录。删除后，该设备下次连接需要重新配对。ClearCast 还会更新接收端身份，因此其他已保存设备也可能需要重新验证。你可以在任一设备上停止屏幕镜像。ClearCast 不向开发者或云服务共享配对记录。你主动打开 GitHub 链接时，适用 GitHub 的隐私政策。

如需咨询或处理 ClearCast 中的数据，请通过 [ClearCast GitHub 仓库](https://github.com/NCFXZ/ClearCast) 联系开发者。
