# Third-party notices

ClearCast is licensed under `GPL-3.0-or-later`. See [LICENSE](LICENSE). The components below retain their own notices and license terms.

## UxPlay receiver

ClearCast builds its in-process AirPlay receiver from [UxPlay](https://github.com/FDH2/UxPlay), pinned to [commit `d4b7373`](https://github.com/FDH2/UxPlay/tree/d4b7373cb6107b2d8d6c5838991c19d39e2b96ae). UxPlay's source notices grant GNU GPL version 3 or later. See its [license text](https://github.com/FDH2/UxPlay/blob/d4b7373cb6107b2d8d6c5838991c19d39e2b96ae/LICENSE) and the original notices in the upstream files.

The ClearCast version dated 2026-11-06 modifies UxPlay for Windows embedding, in-process receiver lifecycle, pairing, video and decoder control, renderer statistics, and mDNS. The modifications and build instructions are in [native/](native/) and [README.md](README.md#准备-windows-接收运行时).

## GitHub mark

The GitHub mark on the About page comes from [Primer Octicons](https://github.com/primer/octicons/blob/main/icons/mark-github-16.svg). Its MIT license and copyright notice are in [Assets/GitHubMark-LICENSE.txt](Assets/GitHubMark-LICENSE.txt).

## Build dependencies

- [GStreamer](https://gstreamer.freedesktop.org/documentation/frequently-asked-questions/licensing.html) and selected plugins, including `gst-libav`, are obtained from [MSYS2](https://www.msys2.org/). Individual packages and linked libraries, including FFmpeg, OpenSSL, and libplist, retain their own terms.
- The .NET and Windows App SDK components are obtained during build. The [Microsoft.WindowsAppSDK.WinUI 2.6.5 package](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.6.5) carries [Microsoft Software License Terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.6.5/License).

This repository publishes ClearCast source, patches, and build instructions. Distribution of compiled packages also requires the applicable corresponding source and notices for the exact dependencies included in those packages.
