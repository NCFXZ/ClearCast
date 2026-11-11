# ClearCast privacy policy / 隐私政策

## English

ClearCast receives the screen, music, or video that you choose to send using AirPlay on your local network. It decodes and plays the content on your Windows PC. ClearCast does not record the stream, send it to the developer, or use it for analytics or advertising.

For music, ClearCast can display the track title, artist, album, artwork, and timing supplied by the sending device. This information is processed in memory for playback, is not saved in the app's settings or connection log, and is not sent to the developer. When you use an available playback control, ClearCast sends the requested command back to the current sending device over the local network.

While music is playing, ClearCast automatically sends the current track title, artist, album, and duration over HTTPS to [LRCLIB](https://lrclib.net), a third-party lyrics service, to find matching lyrics. This happens even when lyrics are hidden, so the lyrics button can show whether lyrics are available. LRCLIB receives the usual network request information, such as the connection's public IP address, and its own terms apply. Lyrics and lookup results are kept in memory only for the current session and are not sent to the developer. Your choice to show or hide lyrics is saved with your app settings.

Some video players provide a media address for the PC to play directly. In that case, ClearCast requests the video or playlist from the sending device or the content servers specified by that address. This may require Internet access. Those servers receive the usual network request information, such as the connection's public IP address, and their own privacy terms apply. ClearCast does not route playback through a developer-operated server.

ClearCast advertises its receiver name, which includes your PC name, to devices on the local network so you can find it in the AirPlay picker. It uses a short-lived PIN to pair new devices. To recognize previously paired devices, it stores their identifiers, public keys, names when provided, and pairing timestamps in local app data on the Windows PC. It also stores its receiver key and your app settings locally.

To diagnose connection failures, ClearCast keeps a small local connection log with timestamps, local network addresses and receiver ports, request types, response status codes, and pairing stages, challenge counters, and outcomes. It excludes screen and audio data, pairing codes, keys, proofs, and request bodies. The log rotates automatically and is included when you choose **Copy details**.

You can view and remove individual or all paired devices in **Settings → Device pairing**. Removing a pairing removes its saved entry and requires the device to pair again. ClearCast also renews its receiver identity, so other saved devices may need to verify again. You can stop mirroring from either device. ClearCast does not share saved pairings with the developer or a cloud service. Links that you choose to open on GitHub are subject to GitHub’s privacy policy.

For questions or requests about your ClearCast data, contact the developer through [ClearCast on GitHub](https://github.com/NCFXZ/ClearCast).

## 中文

ClearCast 在局域网上接收你主动通过 AirPlay 投放的屏幕、音乐或视频，并在 Windows 电脑上解码播放。ClearCast 不录制投放内容，不向开发者发送投放内容，也不将其用于分析或广告。

播放音乐时，ClearCast 可以显示发送设备提供的曲名、歌手、专辑、封面和时间信息。这些信息在内存中用于播放，不写入应用设置或连接日志，也不发送给开发者。使用可用的播放控制时，ClearCast 通过局域网把相应指令发回当前发送设备。

播放音乐时，ClearCast 会自动通过 HTTPS 将当前曲名、歌手、专辑和时长发送给第三方歌词服务 [LRCLIB](https://lrclib.net) 查找匹配的歌词；即使歌词处于隐藏状态也会查找，以便歌词按钮显示是否有可用歌词。LRCLIB 会获得普通网络请求信息，例如连接使用的公网 IP 地址，并适用其自身条款。歌词和查找结果只在本次运行的内存中保留，不发送给开发者。显示或隐藏歌词的选择随应用设置保存。

部分视频播放器会提供供电脑直接播放的媒体地址。此时，ClearCast 会从发送设备或该地址指定的内容服务器读取视频或播放列表，可能需要访问互联网。这些服务器会获得普通网络请求信息，例如连接使用的公网 IP 地址，并适用其自身的隐私条款。ClearCast 不通过开发者运营的服务器中转播放。

ClearCast 在局域网上广播包含电脑名称的接收器名称，方便你在 AirPlay 列表中找到它。新设备通过短时有效的验证码配对。为识别已配对设备，ClearCast 在 Windows 电脑的本地应用数据中保存设备标识、公钥、设备提供的名称和配对时间。接收器密钥和应用设置也保存在本地。

为排查连接失败，ClearCast 在本地保留少量连接日志，记录时间、局域网地址和接收端口、请求类型、响应状态码，以及配对阶段、验证码请求计数和结果，不包含镜像影音、验证码、密钥、验证证明或请求正文。日志自动轮转；点击“复制详情”时会附带连接日志。

你可以在 **设置 → 设备配对** 中查看设备，删除单个设备或清除全部配对记录。删除后，该设备下次连接需要重新配对。ClearCast 还会更新接收端身份，因此其他已保存设备也可能需要重新验证。你可以在任一设备上停止屏幕镜像。ClearCast 不向开发者或云服务共享配对记录。你主动打开 GitHub 链接时，适用 GitHub 的隐私政策。

如需咨询或处理 ClearCast 中的数据，请通过 [ClearCast GitHub 仓库](https://github.com/NCFXZ/ClearCast) 联系开发者。
