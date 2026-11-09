# Third-party notices

ClearCast is licensed under `GPL-3.0-or-later`. See the bundled `LICENSE` file or the [project license](https://github.com/NCFXZ/ClearCast/blob/main/LICENSE). The components below retain their own notices and license terms.

## UxPlay receiver

ClearCast builds its in-process AirPlay receiver from [UxPlay](https://github.com/FDH2/UxPlay), pinned to [commit `d4b7373`](https://github.com/FDH2/UxPlay/tree/d4b7373cb6107b2d8d6c5838991c19d39e2b96ae). UxPlay's source notices grant GNU GPL version 3 or later. See its [license text](https://github.com/FDH2/UxPlay/blob/d4b7373cb6107b2d8d6c5838991c19d39e2b96ae/LICENSE) and the original notices in the upstream files.

The ClearCast version dated 2026-11-07 modifies UxPlay for Windows embedding, in-process receiver lifecycle, pairing, video and decoder control, renderer statistics, mDNS, and bounded network request handling. The modifications and build instructions are in [src/native/](https://github.com/NCFXZ/ClearCast/tree/main/src/native) and the [receiver build instructions](https://github.com/NCFXZ/ClearCast#准备-windows-接收运行时).

## GitHub mark

The GitHub mark on the About page comes from [Primer Octicons](https://github.com/primer/octicons/blob/main/icons/mark-github-16.svg). Its MIT license and copyright notice are bundled in `Assets/GitHubMark-LICENSE.txt` and available in the [source asset notice](https://github.com/NCFXZ/ClearCast/blob/main/src/ClearCast/Assets/GitHubMark-LICENSE.txt).

## Build dependencies

- [GStreamer](https://gstreamer.freedesktop.org/documentation/frequently-asked-questions/licensing.html), its bundled plugin scanner, and selected plugins, including `gst-libav`, are obtained from [MSYS2](https://www.msys2.org/). Individual packages and linked libraries, including FFmpeg, retain their own terms.
- The `codecparsers` library and `videoparsersbad` plugin are built separately from the official GStreamer gst-plugins-bad 1.28.9 source using [scripts/native/Build-GStreamerSecurity.ps1](https://github.com/NCFXZ/ClearCast/blob/main/scripts/native/Build-GStreamerSecurity.ps1). Their LGPL-2.1-or-later license, exact source archive, build configuration and hashes accompany release packages.
- The `libplist-2.0.dll` library is built separately from the official [libplist 2.8.0 release](https://github.com/libimobiledevice/libplist/releases/tag/2.8.0) using [scripts/native/Build-LibplistSecurity.ps1](https://github.com/NCFXZ/ClearCast/blob/main/scripts/native/Build-LibplistSecurity.ps1). The LGPL-2.1-or-later component also includes MIT-licensed jsmn and time64 code. Its LGPL and embedded MIT notices, source archive, and build metadata accompany the release.
- The `libcrypto-3-x64.dll` library is built separately from the official [OpenSSL 3.5.9 LTS release](https://github.com/openssl/openssl/releases/tag/openssl-3.5.9) using [scripts/native/Build-OpenSslSecurity.ps1](https://github.com/NCFXZ/ClearCast/blob/main/scripts/native/Build-OpenSslSecurity.ps1), under the Apache-2.0 license. The exact source, license text, and build metadata accompany the release. Development libraries used for building and compatibility checks are separate from the runtime dependency closure.
- The .NET and Windows App SDK components are obtained during build. The [Microsoft.WindowsAppSDK.WinUI 2.6.5 package](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.6.5) carries [Microsoft Software License Terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.6.5/License).

This repository publishes ClearCast source, patches, and build instructions. Distribution of compiled packages also requires the applicable corresponding source and notices for the exact dependencies included in those packages.
