# Third-party notices

ClearCast is licensed under `GPL-3.0-or-later`. See the bundled `LICENSE` file or the [project license](https://github.com/NCFXZ/ClearCast/blob/main/LICENSE). The components below retain their own notices and license terms.

## UxPlay receiver

ClearCast builds its in-process AirPlay receiver from [UxPlay](https://github.com/FDH2/UxPlay), pinned to [commit `d4b7373`](https://github.com/FDH2/UxPlay/tree/d4b7373cb6107b2d8d6c5838991c19d39e2b96ae). UxPlay's source notices grant GNU GPL version 3 or later. See its [license text](https://github.com/FDH2/UxPlay/blob/d4b7373cb6107b2d8d6c5838991c19d39e2b96ae/LICENSE) and the original notices in the upstream files.

The ClearCast version dated 2026-11-11 modifies UxPlay for Windows embedding, in-process receiver lifecycle, pairing, video and decoder control, renderer statistics, mDNS, bounded network/media input, verified-session media authorization, adapter-scoped listeners, authentication and parser safety, and disabling the unused legacy playlist bridge. The modifications and build instructions are in [src/native/](https://github.com/NCFXZ/ClearCast/tree/main/src/native) and the [receiver build instructions](https://github.com/NCFXZ/ClearCast#准备-windows-接收运行时).

## GitHub mark

The GitHub mark on the About page comes from [Primer Octicons](https://github.com/primer/octicons/blob/main/icons/mark-github-16.svg). Its MIT license and copyright notice are bundled in `Assets/GitHubMark-LICENSE.txt` and available in the [source asset notice](https://github.com/NCFXZ/ClearCast/blob/main/src/ClearCast/Assets/GitHubMark-LICENSE.txt).

## Microsoft Fluent System Icons

The video player's Skip Back 10 and Skip Forward 10 icons use the original 24-pixel regular vector paths from Microsoft's [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons/tree/4bf6b786b16f120595efcee8911c74392fa29c14), pinned to commit `4bf6b786b16f120595efcee8911c74392fa29c14`:

- [Skip Back 10](https://github.com/microsoft/fluentui-system-icons/blob/4bf6b786b16f120595efcee8911c74392fa29c14/assets/Skip%20Back%2010/SVG/ic_fluent_skip_back_10_24_regular.svg), SVG SHA-256 `CCB06ABB5FB8BDC937FF9CE4BFFDEEB0D11590F560532B50C56D54BFEC2E30AA`.
- [Skip Forward 10](https://github.com/microsoft/fluentui-system-icons/blob/4bf6b786b16f120595efcee8911c74392fa29c14/assets/Skip%20Forward%2010/SVG/ic_fluent_skip_forward_10_24_regular.svg), SVG SHA-256 `3CCD7A128921DC352D90A3E62FBEBB98437DA74D31039C8EC6E4BAC622F0CBA2`.

Only the SVG container and fixed fill color are replaced with WinUI `PathIcon` controls; the path geometry, including each icon's number 10, is unchanged. The [upstream license](https://github.com/microsoft/fluentui-system-icons/blob/4bf6b786b16f120595efcee8911c74392fa29c14/LICENSE) follows:

```text
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Build dependencies

- [GStreamer](https://gstreamer.freedesktop.org/documentation/frequently-asked-questions/licensing.html), its bundled plugin scanner, and selected plugins, including `gst-libav`, are obtained from [MSYS2](https://www.msys2.org/). Individual packages and linked libraries, including FFmpeg, retain their own terms.
- The `codecparsers` library and `videoparsersbad` plugin are built separately from the official GStreamer gst-plugins-bad 1.28.9 source using [scripts/native/Build-GStreamerSecurity.ps1](https://github.com/NCFXZ/ClearCast/blob/main/scripts/native/Build-GStreamerSecurity.ps1). Their LGPL-2.1-or-later license, exact source archive, build configuration and hashes accompany release packages.
- The `libgstsoup` HTTP source is built from the official [GStreamer gst-plugins-good 1.28.9 source](https://gstreamer.freedesktop.org/src/gst-plugins-good/gst-plugins-good-1.28.9.tar.xz), SHA-256 `E518CD97483706A252E95E60312A22D23DEC6F5E7673AF6F5BDE719C3A9CA7F4`, using [Build-GStreamerHttpSecurity.ps1](https://github.com/NCFXZ/ClearCast/blob/main/scripts/native/Build-GStreamerHttpSecurity.ps1). ClearCast adds a `remote-connectable` property to pass checked socket destinations without rewriting HTTP host names or TLS identities. This LGPL-2.1-or-later component retains its upstream COPYING; the exact source, controlled `gstreamer-soup-destination.patch`, license, configuration and binary/patch hashes accompany release notices. The receiver carries `gstreamer-http-security-manifest.json` and `LICENSE-GStreamerHttpSecurity.txt`.
- The `libplist-2.0.dll` library is built separately from the official [libplist 2.8.0 release](https://github.com/libimobiledevice/libplist/releases/tag/2.8.0) using [scripts/native/Build-LibplistSecurity.ps1](https://github.com/NCFXZ/ClearCast/blob/main/scripts/native/Build-LibplistSecurity.ps1). The LGPL-2.1-or-later component also includes MIT-licensed jsmn and time64 code. Its LGPL and embedded MIT notices, source archive, and build metadata accompany the release.
- The `libcrypto-3-x64.dll` library is built separately from the official [OpenSSL 3.5.9 LTS release](https://github.com/openssl/openssl/releases/tag/openssl-3.5.9) using [scripts/native/Build-OpenSslSecurity.ps1](https://github.com/NCFXZ/ClearCast/blob/main/scripts/native/Build-OpenSslSecurity.ps1), under the Apache-2.0 license. The exact source, license text, and build metadata accompany the release. Development libraries used for building and compatibility checks are separate from the runtime dependency closure.
- The .NET and Windows App SDK components are obtained during build. The [Microsoft.WindowsAppSDK.WinUI 2.6.5 package](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.6.5) carries [Microsoft Software License Terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.6.5/License).

This repository publishes ClearCast source, patches, and build instructions. Distribution of compiled packages also requires the applicable corresponding source and notices for the exact dependencies included in those packages.
