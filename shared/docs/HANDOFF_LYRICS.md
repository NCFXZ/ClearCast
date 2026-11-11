# 交接：音乐歌词、音乐全屏与按钮布局

本文交接 ClearCast 音乐页的歌词功能开发，供下一位开发者（或 AI 助手）接续。协作规则以仓库根目录 [AGENTS.md](../../AGENTS.md) 为准，当前进度见 [PROGRESS.md](../../PROGRESS.md)，功能行为见 [AirPlay 媒体](AIRPLAY_MEDIA.md)。

## 当前接续状态

本交接保留开发过程和故障定位记录，后文的「未提交」、早期 700/600 字重和商店待办是当时状态。当前 `1.3.0.0` 发行范围、待办和验证入口以 [PROGRESS.md](../../PROGRESS.md) 为准。同步和纯文本歌词现均为 700 字重及一致字号/行距；纯文本底部提示分两行。匹配已加入简繁体兼容并放宽过严条件，查询有界且仅保留最新待处理项；手动浏览直接定位，自动层叠与展开/收起效果保留。歌词解析避免超量短行分配，封面前景和背景复用格式/像素限制。商店双语描述与隐私说明已更新，发行和商店提交的最终状态见本版发行记录。

## 0. 审阅修正（2026-11-11）

以下更新覆盖原交接中的故障推测；原始开发过程保留在后文供追溯。

- 用户随后报告所有歌曲都无法打开歌词。实际 `.clearcast-state/ui-error.log` 指向 `Microsoft.UI.Text.FontWeights.get_Bold()`，异常为 `0x80040111`，经 `ApplyTypography` / `SetLyrics` / 查询续接触发本次运行的歌词停用。代码改为直接构造 `Windows.UI.Text.FontWeight`，同步/纯文本仍分别使用原来的 700/600 字重，避免依赖该静态 WinRT 工厂。此证据不再支持优先怀疑模糊对象；此前未留下堆栈的闪退是否同源仍待真机确认。
- 首次未命中后，时长从未知变为已知、或修正超过 2 秒，会对同一曲目重新查询；覆盖时长在查询中或查询完成后到达的情况。停用歌词后不再为后续歌曲发起无用请求。
- LRCLIB 的 15 秒请求期限改为覆盖响应头和完整响应体读取，避免响应头已到但正文停滞时永远停留在查找中。原有响应大小、TLS、重定向和取消限制保留。
- 未改歌词滚动、展开/收起、模糊或边缘渐隐的动画逻辑和参数。
- 新增 `CLEARCAST_UI_LYRICS_ONLY=1` 测试模式：使用独立临时状态和离线响应，启动后立即经过真实异步查询入口显示同步及纯文本歌词，检查时长晚到重查，再复用既有布局、全屏、模糊、切歌和展开/收起检查；该模式不启动接收器。托管 HTTP 夹具补充完整正文超时、缓存、回退和调用方取消检查。报告与截图保留在 `.runtime/qa-lyrics-review/`；真机歌曲匹配与播放验收仍未完成。

### 后续真机闪退与资源回收修正

- 用户随后报告 ILLIT《jellyous》歌词出现时闪退。LRCLIB 精确返回的曲名、歌手和专辑匹配，该曲的韩英混排歌词属正常内容；未因语言而修改匹配策略。
- 19:59:02 的崩溃为 `0xc0000409 / FAST_FAIL_FATAL_APP_EXIT`，与此前字重工厂失败不同。现有系统转储配合微软匹配符号解析出 `dcompi!purecall → CDevice::CRebuildableObjectList::Rebuild → Commit → DCompTreeHost::CommitMainDevice`。minidump 未保存相关堆页，不能从其直接辨识具体资源类型。
- 用相同歌曲的本地 JSON 夹具复现出相同原生栈，当时尝试调整 `ClearLines` 中歌词行资源的清理。该尝试未能解决后续真机频繁切歌；完整转储随后证明故障对象是封面动画控制器，歌词行原有的三处 `Dispose` 已恢复。动画代码、参数和视觉结构不变。
- `CLEARCAST_UI_LYRICS_FIXTURE` 可附加于歌词专用测试模式，读取本地 JSON。实际歌词保留在忽略的 `.runtime/`，不提交歌曲内容。回归涵盖播放、调整窗口、替换歌词、GC、同一回调内的多次替换与退出；必须同时核对宿主记录的进程退出码，不能只凭 UI 内部 `results.json` 判定没有原生崩溃。
- 转储栈和修复前后运行记录见 `.runtime/qa-lyrics-review/`。修正已在同一实际歌曲输入上验证；真实 AirPlay 再验收仍需用户播放确认。

### 频繁切歌：完整转储确认封面控制器生命周期

- 用户再次反馈频繁切歌闪退；20:19:11 的系统转储仍为相同重建队列栈，说明此前回归不足以确认真机故障已排除。
- 新增歌词、封面、组合三种独立离线压测。关闭歌词显示后，仅封面切换仍可在几秒内复现；由只管理测试子进程的本地调试辅助程序捕获完整内存转储，不修改系统 WER 设置，不上传转储。关闭调试堆填充后，故障节点前 `0xA0` 处明确解析为 `Microsoft::UI::Composition::AnimationController::vftable`，确认是封面控制器已销毁却仍在 DComp 待重建列表。
- `MusicBackdrop` 清理改为先停止动画、移除旧视觉内容，关闭依赖活跃 XAML 核心的 `LoadedImageSurface` 和流，持有原生 Composition 资源并等待 `Compositor.RequestCommitAsync()` 完成，再关闭控制器及其目标 Clock 等对象。正常替换、创建失败、取消及视频旧帧回收均覆盖；播放速率和暂停控制降级中的单个控制器同样延迟关闭。提交失败不能当作已完成：可恢复失败采用有上限间隔的重试，资源仍被持有；关闭的 compositor 不会再提交。
- 使用一个回收任务约束现有图像处理管线，开始下一组背景创建前等待上一组回收；清除仍立即移除画面、撤销待处理图像。保留单个活动请求和最新待处理请求，避免延迟提交时堆积旧 GPU 资源。该修正不改变背景或歌词的视觉动画参数。
- 额外快速关闭回归暴露了另一条空指针栈：`LoadedImageSurface.Close` 在其 XAML 核心句柄已断开后被调用。完整托管堆确认窗口此时已经关闭，且没有重复 Dispose 证据。最终将 XAML 表面的关闭移到提交等待前，并提供可等待的背景清理；窗口关闭会先排空图像处理、控制器回收和父视觉资源，再通过队列完成关闭。程序化 `Window.Close()` 不依赖系统关闭事件，测试在调用关闭前显式等待背景清理，并通过自有 HWND 的 `WM_CLOSE` 覆盖系统关闭路径。
- 压测入口为 `CLEARCAST_UI_LYRICS_ONLY=1` 加 `CLEARCAST_UI_LYRICS_STRESS=lyrics/artwork/both`；另有 `backpressure` 复用既有渲染和封面背压检查。压力运行包括连续切歌、取消查询、歌词类型切换、封面更换、GC，以及封面场景中的最小化/恢复。必须核对外层进程退出码。
- 详细证据与各次运行结果继续放在 `.runtime/qa-lyrics-review/`。先前仅歌词夹具通过的结果不能替代此次封面与组合回归，也不能替代最终真机验收。

## 1. 当前状态（先读）

- 基线：`main` 分支提交 `e62771c`（1.2.2.0 已发布版之后）。**所有改动均未提交**，处于工作区。
- 本地启动版 `.runtime/app/` 已替换为本开发版（用户已自行备份旧版），`scripts/Launch-ClearCast.cmd` 打开的就是它。配对与设置在仓库根目录 `.clearcast-state/`，未改动。
- 原开发阶段的测试结论不能覆盖后续原生闪退；当前以 `.runtime/qa-lyrics-review/QA-summary.md`、对应 JSON 和外层进程退出码为准，不能仅凭界面断言通过认定没有崩溃。
- 真机播放音乐时曾连续闪退（见第 6 节）；后续“所有歌曲歌词不可用”已查到实际字重工厂异常并修正（见第 0 节），两种现象是否同源仍待真机确认。
- 未做：真机验收（歌词匹配率、逐行同步、全屏）；商店描述与商店隐私说明的同步（留到下一版发布）。

## 2. 用户需求（已确认的决定）

1. 歌词来源只用 [LRCLIB](https://lrclib.net)（免费、无需密钥）。不使用网易云/QQ 等非官方接口。
2. **自动查找**：每首歌开始后自动查找歌词；查找中或未找到时歌词按钮**置灰**；找到后可用。
3. 歌词显示有开关（按钮），状态随设置保存；默认不显示。开启状态下找到歌词会自动展开。
4. 布局参考 Apple Music：左侧封面、信息和控制，右侧歌词滚动；窗口较窄时歌词在播放器下方。
5. **只做逐行高亮，不做逐字高亮。**
6. 滚动动效和间距参照 Apple Music：层叠缓出滚动、非当前行**按距离模糊**、歌词区**上下边缘渐隐**。
7. 打开/关闭歌词用非线性（缓出）动画。
8. 按钮布局：
   - 发送端**有**上一首/播放/下一首控制时：`全屏 | 上一首 播放 下一首 | 歌词` 同一行，「断开连接」单独在下一行。
   - **没有**控制时：`全屏 | 断开连接 | 歌词` 同一行。
9. AirPlay 可以通过 DACP 控制播放/暂停/上一首/下一首（已有实现，按发送端能力显示），保留不删。
10. 音乐页新增全屏按钮（复用视频全屏的 presenter）。

## 3. 改动文件一览

新增：

| 文件 | 作用 |
|---|---|
| `src/ClearCast/Services/Lyrics.cs` | LRC 解析（时间标签、`[offset:]`、间奏空行、行数 ≤2000、每行 ≤400 字符、去控制字符），`ActiveIndex` 二分查找当前行，`NextChangeMs` 下一行时间 |
| `src/ClearCast/Services/LyricsClient.cs` | LRCLIB 查询与选择：精确 `get` → 曲名+歌手 `search` → 去括号后缀曲名 `search`（必须时长确认）；优先带时间轴；时长差 >2 s 不采用；仅 `https://lrclib.net/api/`、不跟随重定向、超时 15 s、响应解压后 ≤1 MiB；内存缓存 ≤64 首，网络失败不缓存 |
| `src/ClearCast/Controls/LyricsView.cs` | 歌词视图。隐藏的 XAML `TextBlock` 只负责排版；每行通过 `CompositionVisualSurface` 画到独立 `SpriteVisual`；整块经 `CompositionMaskBrush` + 线性渐变做上下渐隐；逐行偏移/透明度/缩放/模糊全部是 Composition 动画 |
| `src/ClearCast/Controls/LyricsBlurEffect.cs` | **手写原生 COM 对象**，实现 Direct2D 高斯模糊所需的 `IGraphicsEffect`/`IGraphicsEffectSource`/`IGraphicsEffectD2D1Interop`（固定 vtable，`UnmanagedCallersOnly`），不依赖 Win2D |
| `src/ClearCast/Views/MainWindow.Lyrics.cs` | 窗口侧逻辑：自动查找、按钮状态、展开/收起（FLIP 动画）、按下一行时间设置单次计时器、异常兜底 |
| `tests/managed/LyricsTests.cs` | 解析、边界、选择逻辑、JSON 绑定的离线测试 |

修改：

- `MainWindow.xaml`：`MusicLayout` 增加歌词列/行；`MusicActionRow` 为 3 行网格（第 0 行传输控制、第 1 行提示、第 2 行断开连接），全屏与歌词按钮的行号在代码中切换；歌词 `ToggleButton` 用资源覆盖为 Subtle 样式（含禁用态）。
- `MainWindow.Music.cs`：`UpdateMusicLayout` 支持左右分栏（视口宽 ≥680 DIP）/上下堆叠；`UpdateMusicControls` 按有无传输控制设置按钮行号和间距；进度刷新时驱动歌词；音乐结束退出全屏；`UpdateMusicBackgroundState` 设置高对比度下关闭模糊/渐隐。
- `MainWindow.Player.cs` / `MainWindow.xaml.cs` / `MainWindow.Playback.cs`：全屏支持纯音乐；视频开始前先退出音乐全屏；音乐全屏下 Esc 退出；关闭窗口时停止歌词。
- `ReceiverPreferences.cs`：新增 `ShowLyrics`（默认 `false`，向后兼容）。
- `UiText.cs`：歌词相关中英文文本。
- `App.xaml.cs`：抽出 `App.WriteUiError(string)`，歌词兜底复用。
- `ClearCast.csproj`：`AllowUnsafeBlocks=true`（原生 COM 效果对象需要）。
- 文档：`AIRPLAY_MEDIA.md`、`legal/PRIVACY.md`（中英文：播放音乐时自动把曲名/歌手/专辑/时长发给 LRCLIB）、`testing.md`、`PROGRESS.md`。
- 测试：`tests/managed/*`（链接新源文件并注册 `LyricsTests`）；`tests/ui/MainWindow.MediaUiTests.cs`、`MainWindow.UiTests.cs`（离线 LRCLIB 处理器、歌词/布局/全屏/模糊/切歌压力检查）。

## 4. 关键设计与参数

**数据流**：原生接收器 → `ReceiverMediaState`（曲名/歌手/专辑/时长/进度）→ `ApplyMediaState` → `RefreshMusicText` → `RefreshLyrics`。曲目（曲名+歌手+专辑）变化后等待 600 ms（时长常在后续快照到达）再查询；用代次号丢弃旧曲目结果。AirPlay 本身不传歌词。

**展开状态**：`LyricsWanted = ShowLyrics && 有音乐 && 无视频 && (有歌词 || 查找中且当前已展开)`。切歌时已展开的面板保持展开、旧歌词淡出，新歌没有歌词才收起，避免布局来回跳。

**展开/收起动画**（`UpdateLyricsPresentation`）：记录播放器布局前后边界，对 `MusicContent` 的 handout visual 做平移+缩放 FLIP，600 ms，`cubic-bezier(0.2, 0.9, 0.1, 1)`。展开时歌词淡入并从右（或下）滑入，延迟 80 ms；收起时歌词先在原位固定（`_lyricsClosing`，`UpdateMusicLayout` 跳过其布局）淡出 240 ms，播放器同时回到居中，`ScopedBatch` 完成后折叠。关闭系统动画时直接切换。

**逐行滚动**（`LyricsView`）：

- 当前行位于歌词区上方约 30%；换行时各行 700 ms，`cubic-bezier(0.22, 1, 0.36, 1)`；当前行之后每行再延迟 45 ms（最多 8 行），形成层叠。
- 当前行不透明度 1、缩放 1；其他行 0.3、0.96。
- 字号随宽度 22–44 DIP，粗体，行高 1.2 倍，段距约 0.62 倍字号；用户后续要求纯文本歌词采用同样排版，底部显示「此歌词不支持滚动」。纯文本仍可手动浏览，没有时间轴时不自动跟随。
- 模糊：相邻行 0.7 DIP，每远一行 +0.7，上限 4；滚轮/触控浏览时全部清晰，3 秒后恢复跟随。
- 渐隐遮罩：0→16% 由透明到不透明，78%→100% 再到透明。
- 间奏（空时间行）不高亮任何行，锚定下一行。
- 精确计时：`UpdateLyricsPosition` 每次按下一行时间设置单次 `DispatcherQueueTimer`，不增加持续轮询。

**原生模糊对象**（`LyricsBlurEffect`）：CsWinRT 2.2 的 CCW 不会暴露 `[GeneratedComInterface]` 接口（实测 `CreateEffectFactory` 返回 `E_NOINTERFACE`），Win2D 只支持 Windows App SDK 1.x 元包、与本项目的 `Microsoft.WindowsAppSDK.WinUI 2.6.5` 不兼容，因此手写 COM 对象：

- `GetName` 必须返回 `"Blur"`（可动画属性写作 `Blur.BlurAmount`）。
- 效果 CLSID `{1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5}`；属性 0 标准差（float）、1 优化（Balanced=1）、2 边界（Soft=0）。
- 当前加固：属性值和输入在创建时一次性 QI 并持有；聚合 `CoCreateFreeThreadedMarshaler` 提供 `IMarshal`；`LyricsView` 字段持有效果对象；仅在 `CompositionEffectFactory.LoadStatus == Success` 时使用模糊，否则歌词保持清晰。

**隐私**：查找在播放音乐时自动进行（歌词隐藏也查），只发送曲名、歌手、专辑、时长；结果只在内存。`PRIVACY.md` 已更新，商店隐私说明未更新。

## 5. 构建与测试

命令从仓库根目录执行，工具链在 `.runtime/`（PowerShell 写法，Git Bash 下设置相同环境变量即可）：

```powershell
$env:DOTNET_CLI_HOME="$PWD\.runtime"; $env:NUGET_PACKAGES="$PWD\.runtime\nuget\packages"
.\.runtime\dotnet\dotnet.exe run --project tests/managed/ManagedStateTests.csproj -c Release -- --output .runtime/qa-lyrics/managed-results.json
.\.runtime\dotnet\dotnet.exe publish src/ClearCast/ClearCast.csproj -c Release -r win-x64 -o .runtime/app
```

UI 测试（需先停止其他接收器；使用一次性目录，不碰真实 `.clearcast-state`）：

```powershell
.\.runtime\dotnet\dotnet.exe publish src/ClearCast/ClearCast.csproj -c Release -r win-x64 `
  -p:EnableUiTests=true -p:BaseIntermediateOutputPath=../../.runtime/obj-ui/ `
  -p:BaseOutputPath=../../.runtime/bin-ui/ -o .runtime/app-ui
$h = "$PWD\.runtime\qa-lyrics\ui-home"; New-Item -ItemType Directory -Force $h | Out-Null
New-Item -ItemType File -Force "$h\clearcast-ui-test-state.marker" | Out-Null
$env:CLEARCAST_HOME = $h; $env:CLEARCAST_UI_TEST_RUN = "1"; .\.runtime\app-ui\ClearCast.exe
```

结果在 `$h\ui-test-output\results.json`，进度在 `$h\ui-check-progress.log`，截图（含 `PrintWindow` 真实合成帧 `*-music-window-*.png`）在 `ui-test-output\`。

注意事项：

- UI 测试里 LRCLIB 被离线处理器替换（所有查询返回 404），不连网；歌词展示用合成 LRC 注入。
- `RenderTargetBitmap` 截图不包含 Composition 属性（模糊、偏移、透明度），判断视觉效果要看 `PrintWindow` 帧。
- UI 测试偶发不相关失败（等待页 SVG 动画计时超时、真实接收器快速轮询阶段一次原生崩溃），重跑通过。
- 测试成绩与产物放 `.runtime/`，不写入 `PROGRESS.md`。

## 6. 原开发阶段：真机播放音乐闪退（后续结论见第 0 节）

**现象**：用户用 iPhone 投放音乐，连续三次在启动后 9–19 秒、首次显示歌词时闪退（加固前的版本）。

**证据**（Windows 事件日志 Application）：

- `Application Error`：故障模块 `CoreMessagingXP.dll` 12.6.5.0，异常 `0xc000027b`（STOWED_EXCEPTION），偏移 `0xadf5e`。
- `Windows Error Reporting`：P4 `combase.dll` 10.0.26100.9818，P7 `80040111`（CLASS_E_CLASSNOTAVAILABLE），P8 偏移 `0x138146`。
- `.clearcast-state/ui-error.log` 未更新，说明 `Application.UnhandledException` 未触发；这符合异常从 DispatcherQueue 回调（计时器 Tick、`await` 续接、Composition 批次完成）逃出、或原生侧 fail-fast 的特征。
- `.clearcast-state/receiver.log`（UTC）：每次闪退约在一首歌元数据（`SET_PARAMETER` 突发）到达后 0.6–1 s，即查找延迟结束、歌词返回并显示时。第二个会话第一首歌显示正常，切到下一首时闪退。

**已排除/未复现**：在 UI 测试中用真实 LRCLIB、用户播放的歌曲（邓紫棋《HELL》等）、模拟元数据突发顺序、显示中反复换歌并强制 GC，均未复现。

**主要怀疑**：手写模糊对象在应用启动后不久的生命周期或线程处理（测试里首次显示歌词通常在启动一两分钟后，用户是一打开就播放）。已做第 4 节所列加固，并给歌词计时器、查找续接、收起批次回调加了 try/catch：意外异常写入 `.clearcast-state/ui-error.log` 并在本次运行停用歌词，不中断播放。

**接手建议**：

1. 让用户用当前 `.runtime/app/` 再播放几首；若不再闪退，检查 `ui-error.log` 是否有 `Lyrics disabled` 记录（有则说明兜底吞掉了真实异常，需按堆栈修正）。
2. 若仍闪退：查看事件日志；可请用户自行开启 WER LocalDumps 取得 dump（系统设置，需用户本人操作）。
3. 隔离法：临时让 `LyricsView` 跳过模糊（`_blurFactory = null`）或跳过 `VisualSurface`/遮罩，确认是否与 Composition 效果相关。
4. 其他候选：`_viewport.ManipulationMode`（触控惯性，DirectManipulation）、XAML 隐藏宿主 + `VisualSurface` 的组合。

## 7. 其他后续

- 真机验收：Apple Music 等发送端的歌词匹配率、逐行时序、音乐全屏、两种按钮布局。
- 下一版发布时同步 `packaging/store-listing.json` 描述和商店隐私说明（自动向 LRCLIB 发送曲目信息）。
- 可选：间奏期间显示类似 Apple Music 的呼吸点；点击歌词行跳转（需发送端支持定位）。
- 提交前按 AGENTS.md 做最小化检查，并把本文结论合并进 `PROGRESS.md`/`AIRPLAY_MEDIA.md`；一次性交接内容不必长期保留。
