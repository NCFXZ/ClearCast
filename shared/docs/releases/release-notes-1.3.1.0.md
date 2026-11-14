# ClearCast 1.3.1.0

新的音乐和视频投屏默认自动全屏，可在设置中关闭。新增默认开启的「自动显示音乐歌词」开关，与播放器歌词按钮同步；已有保存的关闭选择会保留。

全屏控制条改为底部居中的圆角浮条，沿用进入全屏前的窗口模式宽度，并保留边距。背景从实际视频局部取样、模糊并调色，随画面变化；系统关闭透明效果或高对比度模式下使用普通材质。

修复超长畸形曲名可能导致歌词匹配耗时过长的问题，限制单行大量歌词时间标签的临时存储，并加强窗口关闭后迟到回调的保护。移除无消费者的界面字段，补充英文辅助说明检查，修正第三方依赖说明与实际 .NET 运行时的版本绑定。

歌词由 LRCLIB 提供，内容与时间轴可能不准确。兼容性仍取决于发送设备、播放器和内容。自动回归与本地合成媒体测试不能代替真实设备、实际账号内容和长期弱网验收。

## English

New music and video sessions enter full screen by default; this can be turned off in Settings. A new enabled-by-default setting shows available music lyrics and stays synchronized with the player lyrics button. Existing saved choices to turn these features off are preserved.

Full-screen controls now float at the bottom with rounded corners and spacing, using their windowed width. The background uses locally sampled, blurred and tinted video colors that follow the picture. A standard material is used when system transparency effects are disabled or high contrast is enabled.

Fixes excessive lyric-matching work on very long malformed track names, bounds temporary storage for unusually many lyric timestamps on one line, and protects against late callbacks after window closure. Removes unused UI fields, extends English accessibility checks, and binds third-party notices to the actual bundled .NET runtime version.

Lyrics are provided by LRCLIB and may have inaccurate content or timing. Compatibility still depends on the sending device, player and content. Automated regression and local synthetic-media checks do not replace physical-device, account-content or prolonged weak-network acceptance.
