using System.ComponentModel;

namespace ClearCast;

internal sealed partial class UiText : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnglish { get; private set; }
    private string _receiverName = "ClearCast";

    public void SetEnglish(bool english)
    {
        if (IsEnglish == english)
            return;

        IsEnglish = english;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string Get(string chinese, string english) => IsEnglish ? english : chinese;

    public void SetReceiverName(string name)
    {
        if (_receiverName == name)
            return;

        _receiverName = name;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LandingDescription)));
    }

    public string Mirroring => "AirPlay";
    public string Settings => Get("设置", "Settings");
    public string About => Get("关于", "About");
    public string AboutVersion { get; } = typeof(UiText).Assembly.GetName().Version?.ToString() ?? "—";
    public string AboutLicense => Get("GNU GPL v3 或更高版本", "GNU GPL v3 or later");
    public string AboutRepositoryLinkText => Get("查看 GitHub 仓库", "View GitHub repository");
    public string AboutReportIssue => Get("问题反馈", "Report an issue");

    public string PairingCode => Get("AirPlay 验证码", "AirPlay pairing code");
    public string DismissPairingCode => Get("关闭验证码提示", "Dismiss pairing code");
    public string DismissReceiverError => Get("关闭错误提示", "Dismiss error");
    public string ScrollUp => Get("向上滚动", "Scroll up");
    public string ScrollDown => Get("向下滚动", "Scroll down");
    public string PageUp => Get("向上翻页", "Page up");
    public string PageDown => Get("向下翻页", "Page down");
    public string VerticalScrollThumb => Get("垂直滚动条滑块", "Vertical scroll thumb");
    public string ScrollLeft => Get("向左滚动", "Scroll left");
    public string ScrollRight => Get("向右滚动", "Scroll right");
    public string PageLeft => Get("向左翻页", "Page left");
    public string PageRight => Get("向右翻页", "Page right");
    public string HorizontalScrollThumb => Get("水平滚动条滑块", "Horizontal scroll thumb");
    public string ReceiverError => Get("接收遇到问题", "Receiver error");
    public string LandingHeading => Get("将屏幕、音乐或视频投到此电脑", "Mirror or play media on this PC");
    public string LandingDescription => Get($"请将设备与此电脑连接到同一网络，在设备上打开「屏幕镜像」或播放器中的 AirPlay，选择「{_receiverName}」。",
        $"Connect your device and this PC to the same network. Open Screen Mirroring or AirPlay in your player, then select “{_receiverName}”.");
    public string RetryReceiver => Get("重试接收", "Try again");

    public string Disconnect => Get("断开连接", "Disconnect");
    public string FullScreen => Get("全屏", "Full screen");
    public string ExitFullScreen => Get("退出全屏", "Exit full screen");
    public string RotateLeft => Get("向左旋转 90°", "Rotate left 90°");
    public string RotateRight => Get("向右旋转 90°", "Rotate right 90°");
    public string SkipBackward10 => Get("后退 10 秒", "Back 10 seconds");
    public string SkipForward10 => Get("前进 10 秒", "Forward 10 seconds");
    public string OriginalSize => Get("原始大小", "Actual size");

    public string GeneralHeading => Get("常规", "General");
    public string ThemeLabel => Get("应用主题", "App theme");
    public string ThemeDescription => Get("选择 ClearCast 的外观。", "Choose how ClearCast looks.");
    public string SystemDefault => Get("跟随系统", "Use system setting");
    public string LightTheme => Get("浅色", "Light");
    public string DarkTheme => Get("深色", "Dark");
    public string LanguageLabel => Get("应用语言", "App language");
    public string LanguageDescription => Get("选择 ClearCast 界面使用的语言。", "Choose the language used in ClearCast.");
    public string ChineseLanguage => "简体中文";
    public string EnglishLanguage => "English";

    public string ReceivingHeading => Get("接收", "Receiving");
    public string DecoderLabel => Get("解码方式", "Decoding mode");
    public string DecoderDescription => Get("自动模式优先使用显卡解码。", "Automatic mode uses hardware decoding when available.");
    public string AutomaticDecoder => Get("自动", "Automatic");
    public string SoftwareDecoder => Get("软件解码", "Software decoding");
    public string HardwareDecoder => Get("硬件解码", "Hardware decoding");

    public string PlaybackHeading => Get("播放显示", "Playback");
    public string DisplaySizeLabel => Get("默认显示大小", "Default display size");
    public string DisplaySizeDescription => Get("适应窗口会保持比例。原始大小按画面像素显示，超出窗口时会裁切。",
        "Fit to window keeps the aspect ratio. Actual size shows the original pixels and crops the picture if needed.");
    public string FitWindow => Get("适应窗口", "Fit to window");
    public string AutoFullScreenLabel => Get("投屏时自动全屏", "Automatically enter full screen");
    public string AutoFullScreenDescription => Get("新的音乐和视频投屏开始时自动全屏显示。", "Show new music and video sessions in full screen.");
    public string AutoShowLyricsLabel => Get("自动显示音乐歌词", "Automatically show music lyrics");
    public string AutoShowLyricsDescription => Get("找到音乐歌词后自动显示。", "Show lyrics automatically when they are found.");
    public string MusicUnknownTitle => Get("正在接收音乐", "Receiving music");
    public string MusicUnknownArtist => Get("来自 AirPlay 设备", "From your AirPlay device");
    public string MusicPlaying => Get("正在播放音乐", "Playing music");
    public string MusicPaused => Get("音乐已暂停", "Music paused");
    public string MusicPlay => Get("播放", "Play");
    public string MusicPause => Get("暂停", "Pause");
    public string MusicPrevious => Get("上一首", "Previous track");
    public string MusicNext => Get("下一首", "Next track");
    public string MusicArtwork => Get("专辑封面", "Album artwork");
    public string MusicProgress => Get("播放进度", "Playback progress");
    public string MusicShowLyrics => Get("显示歌词", "Show lyrics");
    public string MusicHideLyrics => Get("隐藏歌词", "Hide lyrics");
    public string MusicLyrics => Get("歌词", "Lyrics");
    public string LyricsSearching => Get("正在查找歌词…", "Looking for lyrics…");
    public string LyricsNotFound => Get("未找到这首歌的歌词", "No lyrics found for this song");
    public string LyricsSource => Get("歌词由 LRCLIB 提供，内容及时间轴可能不准确。", "Lyrics provided by LRCLIB. Lyrics and timing may be inaccurate.");
    public string LyricsNotScrollable => Get("此歌词不支持滚动", "Scrolling is not supported for these lyrics.");
    public string MusicControlFailed => Get("无法发送播放指令，请在设备上操作。", "Couldn't send the playback command. Use your device to control playback.");
    public string PreferencesSaveError => Get("无法保存设置，这次更改仍可使用。", "Couldn't save your settings. Changes still apply for this session.");

    public string PairingHeading => Get("设备配对", "Device pairing");
    public string SavedPairings => Get("已保存的配对", "Saved pairings");
    public string SavedPairingsDescription => Get("删除配对后，该设备下次需输入验证码，其他已保存设备也可能需要重新验证。",
        "A removed device will need a pairing code next time. Other saved devices may also need to pair again.");
    public string NoSavedPairings => Get("暂无已配对设备", "No paired devices");
    public string AllSavedPairings => Get("全部配对记录", "All saved pairings");
    public string UnnamedPairing => Get("未命名设备", "Unnamed device");
    public string RemovePairing => Get("删除", "Remove");
    public string RemoveDevicePairing => Get("删除设备配对", "Remove device pairing");
    public string ClearPairings => Get("删除全部", "Remove all");
}
