namespace ClearCast;

internal sealed partial class UiText
{
    private static readonly Dictionary<string, string> DynamicEnglish = new()
    {
        ["正在启动接收器…"] = "Starting the receiver…",
        ["等待屏幕镜像连接"] = "Waiting for a device",
        ["正在打开屏幕镜像画面…"] = "Opening the mirrored screen…",
        ["已连接"] = "Connected",
        ["无法旋转屏幕镜像画面，请重试。"] = "Couldn't rotate the mirrored screen. Try again.",
        ["暂时无法接收屏幕镜像"] = "Screen mirroring unavailable",
        ["在设备上输入这四位数字。"] = "Enter these four digits on your device.",
        ["配对请求未完成，请在设备上重试。"] = "The pairing request couldn't finish. Try again on your device.",
        ["配对验证未通过，请在设备上重试。"] = "Pairing verification failed. Try again on your device.",
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
}
