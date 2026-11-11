namespace ClearCast;

internal sealed partial class UiText
{
    private static readonly Dictionary<string, string> DynamicEnglish = new()
    {
        ["正在启动接收器…"] = "Starting the receiver…",
        ["等待 AirPlay 连接"] = "Waiting for an AirPlay device",
        ["AirPlay 已连接"] = "AirPlay connected",
        ["无法发送播放指令，请在设备上操作。"] = "Couldn't send the playback command. Use your device to control playback.",
        ["正在打开屏幕镜像画面…"] = "Opening the mirrored screen…",
        ["无法旋转屏幕镜像画面，请重试。"] = "Couldn't rotate the mirrored screen. Try again.",
        ["暂时无法接收屏幕镜像"] = "Screen mirroring unavailable",
        ["在设备上输入这四位数字。"] = "Enter these four digits on your device.",
        ["配对请求未完成，请在设备上重试。"] = "The pairing request couldn't finish. Try again on your device.",
        ["配对验证未通过，请核对验证码后重试。"] = "Pairing verification failed. Check the code and try again.",
        ["验证码已失效，请在设备上重新选择 ClearCast 后重试。"] =
            "The pairing code has expired. Select ClearCast again on your device and try again.",
        ["配对尝试较多，请稍后在设备上重试。"] = "Too many pairing attempts. Try again on your device later.",
        ["设备验证未通过，请在设备上重新选择 ClearCast 后重试。"] =
            "Device verification failed. Select ClearCast again on your device and try again.",
        ["设备连接未完成，请在设备上重试。可复制详情查看连接记录。"] =
            "The device couldn't finish connecting. Try again on your device. Copy details to view the connection log.",
        ["正在切换解码方式…"] = "Switching decoding mode…",
        ["画面没有及时更新。请在设备上滑动或切换画面后重试。"] =
            "No new frames arrived in time. Swipe or change what's on your device, then try again.",
        ["暂时无法切换，继续使用当前解码方式。"] =
            "Unable to switch. The current decoding mode is still active.",
        ["当前设备或画面不支持硬件解码，已保留原解码方式。"] =
            "This device or stream doesn't support hardware decoding. The previous mode is still active.",
        ["硬件解码不可用。请在设置中选择自动或软件解码后重试。"] =
            "Hardware decoding is unavailable. Choose Automatic or Software decoding in Settings and try again.",
        ["无法读取已保存的配对记录。"] = "Couldn't read saved pairings.",
        ["已保存的配对记录过大，请在设置中清除配对记录后重试。"] =
            "The saved pairing records are too large. Remove all saved pairings in Settings, then try again.",
        ["已删除全部配对记录。设备下次屏幕镜像时需要重新验证。"] =
            "All pairings removed. Devices will need a pairing code the next time they connect.",
        ["已删除该设备的配对记录。该设备下次屏幕镜像时需要重新验证，其他已保存设备也可能需要重新验证。"] =
            "Pairing removed. This device will need a pairing code next time. Other saved devices may also need to pair again.",
        ["无法删除配对记录，请重试。"] = "Couldn't remove the saved pairings. Try again.",
        ["无法启动接收，请重试。"] = "Couldn't start the receiver. Try again.",
        ["无法停止接收，请重试。"] = "Couldn't stop the receiver. Try again.",
        ["另一个 ClearCast 窗口正在使用接收器。请关闭它后重试。"] =
            "Another ClearCast window is using the receiver. Close it and try again.",
        ["复制详情"] = "Copy details",
        ["已复制"] = "Copied",
        ["重试复制"] = "Try again",
        ["找不到接收组件。请使用完整的 ClearCast 发布文件夹。"] =
            "The receiver component is missing. Use the complete ClearCast release folder.",
        ["无法初始化接收组件的 DLL 搜索路径。"] = "Couldn't initialize the receiver's DLL search path.",
        ["无法注册接收组件目录。"] = "Couldn't register the receiver's component directory.",
        ["无法加载接收组件或其依赖。"] = "Couldn't load the receiver component or one of its dependencies.",
        ["接收组件版本不兼容。请使用完整的 ClearCast 发布文件夹。"] =
            "The receiver version is incompatible. Use the complete ClearCast release folder.",
        ["无法创建屏幕镜像画面区域。"] = "Couldn't create the screen mirroring surface.",
        ["AirPlay 接收端口已被占用。请关闭其他屏幕镜像程序或 ClearCast 窗口后重试。"] =
            "The AirPlay port is in use. Close other mirroring apps or ClearCast windows, then try again.",
        ["当前网络不可用。请检查 Wi-Fi 或网线连接后重试。"] =
            "The network is unavailable. Check your Wi-Fi or Ethernet connection, then try again.",
        ["无法在局域网发布 AirPlay。请检查网络连接和 Windows 防火墙后重试。"] =
            "Couldn't advertise AirPlay on the local network. Check your connection and Windows Firewall, then try again.",
        ["Windows 拒绝监听。请检查当前网络的防火墙和访问策略。"] =
            "Windows blocked the receiver from listening. Check your firewall and network access policies.",
        ["接收已中断，请重试。"] = "The receiver stopped unexpectedly. Try again."
    };

    public string Translate(string chinese) =>
        IsEnglish && DynamicEnglish.TryGetValue(chinese, out var english) ? english : chinese;

    public string RenderedMediaDiagnostics(double frameRate, ulong dropped) =>
        Get("渲染帧率", "Rendered frame rate") + FormattableString.Invariant($": {frameRate:0.##} fps\n") +
        Get("丢帧数", "Dropped frames") + ": " + dropped.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string MediaDiagnosticsText(ReceiverMediaState state)
    {
        if (state.Kind == ReceiverMediaKind.None || !state.Connected) return "";
        var info = state.Diagnostics;
        var lines = new List<string>(9);
        void Add(string chinese, string english, string value) => lines.Add(Get(chinese, english) + ": " + value);
        if (state.Kind == ReceiverMediaKind.Video)
        {
            if (info.Width > 0 && info.Height > 0)
                Add("分辨率", "Resolution", $"{info.Width} × {info.Height}");
            if (double.IsFinite(info.FrameRate) && info.FrameRate > 0)
                Add("源帧率", "Source frame rate", FormattableString.Invariant($"{info.FrameRate:0.##} fps"));
            if (!string.IsNullOrEmpty(info.VideoCodec)) Add("视频编码", "Video codec", info.VideoCodec);
            if (info.VideoBitrate > 0) Add("视频码率", "Video bitrate", FormatBitrate(info.VideoBitrate));
        }
        if (!string.IsNullOrEmpty(info.AudioCodec)) Add("音频编码", "Audio codec", info.AudioCodec);
        if (info.SampleRate > 0) Add("采样率", "Sample rate", FormattableString.Invariant($"{info.SampleRate / 1000d:0.###} kHz"));
        if (info.Channels > 0) Add("声道数", "Channels", info.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (info.AudioBitrate > 0) Add("音频码率", "Audio bitrate", FormatBitrate(info.AudioBitrate));
        return lines.Count > 0 ? string.Join('\n', lines) : Get("等待媒体信息…", "Waiting for media information…");
    }

    private static string FormatBitrate(long bitrate) => bitrate >= 1_000_000_000
        ? FormattableString.Invariant($"{bitrate / 1_000_000_000d:0.##} Gbps")
        : bitrate >= 1_000_000 ? FormattableString.Invariant($"{bitrate / 1_000_000d:0.##} Mbps")
        : bitrate >= 1000 ? FormattableString.Invariant($"{bitrate / 1000d:0.##} kbps")
        : FormattableString.Invariant($"{bitrate} bps");
}
