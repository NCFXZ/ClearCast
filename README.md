# ClearCast

ClearCast 是 Windows 上的局域网屏幕镜像工具。iPhone、iPad 和 Mac 使用系统 AirPlay 投到 ClearCast 窗口。

[下载 Windows x64 安装包](https://github.com/NCFXZ/ClearCast/releases/latest)

## 架构

- **桌面界面**由 C#、.NET 10 和 WinUI 3 原生 XAML 实现。标题栏、侧栏和正文共用 Mica 底色，页面以 Windows 设置为参考，使用居中的内容列和原生主题卡片。收到来流后，投屏页展开播放器。 [微软 Mica 分层指南](https://learn.microsoft.com/en-us/windows/apps/design/style/mica#app-layering-with-mica) · [微软设置页指南](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings#layout)
- **接收服务**在 ClearCast 进程内运行。随软件发布的 `receiver\clearcast_receiver.dll` 使用 UxPlay 处理 AirPlay 发现、配对和收流。
- **播放**由 GStreamer 直接解码来流，优先尝试 D3D11 硬件解码。投屏画面通过原生子窗口在应用内等比显示。发送端停止镜像后，接收端返回等待状态。

## 构建界面

ClearCast 支持 Windows x64，采用免安装的文件夹发布，输出内包含 .NET、Windows App SDK 和接收组件。已有 `.runtime\app\ClearCast.exe` 时可直接使用。需要从源码构建时，先按下文准备接收组件，再在项目目录用 .NET 10 SDK 执行以下命令。 [Windows App SDK 自包含发布](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)

```powershell
dotnet publish .\ClearCast.csproj -c Release -r win-x64 -o .\.runtime\app
```

如果 SDK 已放在项目内的 `.runtime\dotnet`，将命令中的 `dotnet` 换为 `.\.runtime\dotnet\dotnet.exe`。发布目录内的文件需要保留在一起。

## 使用 AirPlay

1. 将苹果设备和 Windows 电脑接入同一个允许设备互访的局域网。Windows 可通过网线接入同一路由器。
2. 双击 `Launch-ClearCast.cmd`，应用打开后自动等待投屏。ClearCast 根据当前网卡的链路速率和解码方式选择初始画质。Windows 防火墙询问时，可为可信的专用网络允许 `ClearCast.exe`。
3. 在苹果设备的「屏幕镜像」列表中选择投屏页显示的名称，例如 `ClearCast DESKTOP-1234`。新设备请求配对时，电脑会显示四位验证码。在苹果设备上输入后，画面会在「投屏」页播放。
4. 在画面上移动鼠标会显示右下角的控件，可向左或向右旋转 90°，也可切换全屏。双击画面可切换全屏，按 Esc 可退出全屏。可在「设置 → 播放显示」选择默认显示大小。点「断开投屏」后，应用会继续等待下一次连接。
5. 投屏时可以在「设置 → 接收」中切换解码方式。ClearCast 会保持 AirPlay 连接，画面可能在切换时短暂停顿。

新设备发起配对时，ClearCast 会显示新的四位验证码，有效期为一分钟。验证多次失败后，新配对会短暂暂停，已配对设备仍可连接。接收器密钥、配对记录和显示偏好保存在 `.clearcast-state` 中。

侧栏底部的「设置」按接收、播放显示和设备配对分组。在「设备配对」中展开「已保存的配对」，即可逐台删除设备的配对记录，也可一次删除全部。配对记录被删除的设备下次连接时需要重新验证。发生接收错误时，可在投屏页复制错误详情并重试。

## 准备 Windows 接收运行时

接收组件采用带内置 mDNS 的 UxPlay 1.74 实验版源码。构建脚本生成接收 DLL，并将所需的 GStreamer 插件和依赖收集到 `.runtime\receiver`。发布时保留 `receiver` 目录结构。 [上游提交](https://github.com/FDH2/UxPlay/commit/d4b7373cb6107b2d8d6c5838991c19d39e2b96ae)

从 [MSYS2 官网](https://www.msys2.org/) 安装 MSYS2，在 **MSYS2 UCRT64** 终端更新。若更新要求关闭终端，重新打开 UCRT64 后再执行一次 `pacman -Syu`。然后在 UCRT64 中运行以下命令。 [MSYS2 更新文档](https://www.msys2.org/docs/updating/)

```bash
pacman -Syu
```

```bash
pacman -S --needed git \
  mingw-w64-ucrt-x86_64-{gcc,cmake,ninja,pkgconf,openssl,libplist,gstreamer} \
  mingw-w64-ucrt-x86_64-gst-{plugins-base,plugins-good,plugins-bad,libav}
```

使用 PowerShell 7 构建，并确保 `git` 在 PATH 中。在项目目录下载固定版本的源码。若使用项目内的 `.runtime\msys64\ucrt64`，可省略 `-Ucrt64Directory`。

```powershell
New-Item -ItemType Directory -Force .runtime
Invoke-WebRequest 'https://codeload.github.com/FDH2/UxPlay/tar.gz/d4b7373cb6107b2d8d6c5838991c19d39e2b96ae' -OutFile .runtime\uxplay-d4b7373.tar.gz
pwsh -File .\native\Build-Receiver.ps1 -Ucrt64Directory C:\msys64\ucrt64
```

构建完成后执行上面的发布命令。若遇到解码兼容问题，可在「设置 → 接收」中切换到软件解码。

## 清晰度与真机验收

接收端直接解码并显示视频。开始接收时，根据 Windows 报告的网卡链路速率选择初始画质。自动解码时，有线速率达到 100 Mb/s 或 Wi-Fi 协商速率达到 400 Mb/s 会请求 4K HEVC，低于 50 Mb/s 请求 720p，其余情况请求 1080p。三档都请求每秒 30 帧。软件解码最高请求 1080p。链路速率是本机到网络的连接速率，实际画质由发送设备和传输条件决定。 [UxPlay 模式与参数](https://github.com/FDH2/UxPlay#usage)

播放状态采用 `1920 × 888 · 30 帧` 的格式，优先显示解码尺寸，帧率根据播放器实际显示帧数采样。悬停可查看播放器跳过的帧数。

在 iPhone、iPad 和 Mac 上分别验证默认画质、横竖屏、全屏和断开重连。比较 Wi-Fi 和有线网络时，可在投屏页查看解码尺寸和播放帧率。

1. 镜像有小字号文字和细线的网页或 PDF，调整窗口大小和最大化，检查可读性、比例和裁切。iPhone、iPad 各做一次横竖屏切换，再切到「设置」并返回「投屏」，确认画面继续播放。
2. 播放一段持续有运动的本地非 DRM 视频，观察卡顿、花屏和音画同步。投屏页会显示画面尺寸和播放帧率。
3. 从苹果端停止镜像，确认应用回到等待投屏并可重新连接。也可点 ClearCast 的「断开投屏」，再从苹果端重新连接。已配对设备使用保存的配对记录。

## 排查

- **AirPlay 列表里没有 ClearCast。** 检查两台设备是否位于同一局域网，访客网络的设备隔离是否挡住发现，Windows 防火墙是否允许 `ClearCast.exe`。AirPlay 发现使用 UDP 5353，接收端使用 TCP 和 UDP 47000–47002。
- **有声音但画面黑屏。** 先镜像发送设备的设置界面或本地照片。若只有受保护的视频黑屏，换用普通本地视频验证。若一般内容也黑屏，在「设置 → 接收」中试用自动或软件解码。
- **画面尺寸偏低或卡顿。** 查看实际画面尺寸和播放帧率，检查 AirPlay 设备是否支持 HEVC，以及两台设备的网络连接。改善连接后重新开始接收可更新画质请求。

## 开源许可证

ClearCast 的原创源码和项目素材采用 GNU 通用公共许可证第 3 版或更高版本（`GPL-3.0-or-later`）。接收组件基于修改后的 UxPlay，第三方组件和图标保留各自的许可。详见 [LICENSE](LICENSE) 和 [第三方声明](THIRD-PARTY-NOTICES.md)。
