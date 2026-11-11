# ClearCast 1.3.0.0

## 中文

- 新增音乐全屏和歌词界面，使用 MusicInfo 图标；同步歌词逐行高亮，支持手动浏览和自动恢复跟随。
- 优化歌词滚动，复用动画与布局信息并减少不可见行更新，保留原有自动层叠滚动、模糊及展开/收起效果。
- 放宽过严的歌词匹配规则，支持简繁体曲名和歌手变体；纯文本歌词使用一致字体，底部分两行显示「此歌词不支持滚动」及来源/准确性提示。
- 修复频繁切歌时封面动画资源过早回收导致的闪退，等待 Composition 提交后释放，关闭窗口前排空图像任务。
- 加强歌词请求取消、解析与响应上限，封面格式/尺寸预检、原生输入背压、DACP 网卡约束及异常播放请求的资源回收。
- 修正直播总时长未知或播放位置短暂超过终点时的负缓冲区间。
- 清理已无用途的代码和测试副本；安装包拒绝测试程序集及私有状态，GitHub 附件验证复核精确集合、大小和散列。

自动歌词查询会将曲名、歌手、专辑及可用时长发送至 LRCLIB，详见[隐私政策](https://github.com/NCFXZ/ClearCast/blob/v1.3.0.0/shared/docs/legal/PRIVACY.md)。歌词内容及时间轴可能不准确，部分曲目可能没有歌词。播放控制、歌词时序及音画兼容性取决于发送设备、播放器和内容。

## English

- Adds a full-screen music view and a MusicInfo lyrics button. Timed lyrics highlight line by line, with manual browsing and automatic return to playback following.
- Improves lyric scrolling by reusing animations and layout data and reducing updates to offscreen lines. Preserves the automatic cascade, blur, and expand/collapse transitions.
- Relaxes overly strict matching and supports Simplified/Traditional Chinese titles and artist variants. Plain lyrics use consistent typography and show the scrolling notice separately from source and accuracy information.
- Fixes crashes during rapid track changes by retiring artwork-animation resources after the Composition commit and draining image work before window closure.
- Strengthens lyric cancellation, parsing, and response limits; artwork format/dimension checks; native input backpressure; DACP interface restrictions; and cleanup of malformed playback requests.
- Corrects negative buffer ranges for live streams with an unknown duration and position samples beyond the known endpoint.
- Removes unused code and disposable build copies. Package validation rejects test assemblies and private state; GitHub asset verification checks the exact set, sizes, and hashes.

Automatic lyric lookup sends the title, artist, album, and available duration to LRCLIB; see the [privacy policy](https://github.com/NCFXZ/ClearCast/blob/v1.3.0.0/shared/docs/legal/PRIVACY.md). Lyrics and timing may be inaccurate, and some tracks may have no lyrics. Playback controls, lyric timing, and audiovisual compatibility depend on the sending device, player, and content.
