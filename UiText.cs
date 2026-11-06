using System.ComponentModel;

namespace ClearCast;

internal sealed partial class UiText : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnglish { get; private set; }

    public void SetEnglish(bool english)
    {
        if (IsEnglish == english)
            return;

        IsEnglish = english;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string Get(string chinese, string english) => IsEnglish ? english : chinese;

    public string Mirroring => Get("投屏", "Screen mirroring");
    public string Settings => Get("设置", "Settings");
    public string About => Get("关于", "About");
    public string AboutVersion { get; } = typeof(UiText).Assembly.GetName().Version?.ToString() ?? "—";
    public string AboutRepository => Get("GitHub 仓库", "GitHub repository");
    public string AboutLicense => Get("开源许可证", "Open-source license");
    public string AboutReportIssue => Get("问题反馈", "Report an issue");

    public string PairingCode => Get("投屏验证码", "Mirroring code");
    public string ReceiverError => Get("接收遇到问题", "Couldn't receive the stream");
    public string LandingHeading => Get("将屏幕投到这台电脑", "Mirror your screen to this PC");
    public string LandingDescription => Get("在同一局域网的 iPhone、iPad 或 Mac 上打开「屏幕镜像」，然后选择 ClearCast。",
        "On an iPhone, iPad, or Mac on the same network, open Screen Mirroring and choose ClearCast.");
    public string RetryReceiver => Get("重试接收", "Try again");

    public string Disconnect => Get("断开投屏", "Disconnect");
    public string FullScreen => Get("全屏", "Full screen");
    public string ExitFullScreen => Get("退出全屏", "Exit full screen");
    public string RotateLeft => Get("向左旋转 90°", "Rotate left 90°");
    public string RotateRight => Get("向右旋转 90°", "Rotate right 90°");
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
    public string PreferencesSaveError => Get("无法保存设置，这次更改仍可使用。", "Couldn't save your settings. Changes still apply for this session.");

    public string PairingHeading => Get("设备配对", "Device pairing");
    public string SavedPairings => Get("已保存的配对", "Saved pairings");
    public string SavedPairingsDescription => Get("删除配对后，设备下次投屏时需要输入验证码。",
        "After removal, the device will need a code the next time it connects.");
    public string NoSavedPairings => Get("暂无已配对设备", "No paired devices");
    public string AllSavedPairings => Get("全部配对记录", "All saved pairings");
    public string UnnamedPairing => Get("未命名设备", "Unnamed device");
    public string RemovePairing => Get("删除", "Remove");
    public string RemoveDevicePairing => Get("删除设备配对", "Remove device pairing");
    public string ClearPairings => Get("删除全部", "Remove all");
}
