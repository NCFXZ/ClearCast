using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ClearCast;

internal sealed record ReceiverAdapter(string Name, IPAddress Address, bool HasGateway, long LinkSpeed,
    bool IsWireless, bool IsEthernet)
{
    // Preserve the IPv4 byte sequence in the little-endian uint32_t passed to Winsock.
    public uint NativeAddress => BinaryPrimitives.ReadUInt32LittleEndian(Address.GetAddressBytes());
}

internal static class ReceiverNetwork
{
    private static readonly string[] VirtualAdapterMarkers = ["Virtual", "Hyper-V", "WSL", "vEthernet", "VPN", "TAP-", "Wintun", "WireGuard"];

    public static ReceiverAdapter? FindPreferredAdapter()
    {
        try
        {
            return ReadAdapters()
                .OrderByDescending(adapter => adapter.HasGateway)
                .ThenByDescending(adapter => adapter.IsWireless || adapter.IsEthernet)
                .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static IEnumerable<ReceiverAdapter> ReadAdapters()
    {
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.SupportsMulticast ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            IPInterfaceProperties properties = adapter.GetIPProperties();
            bool hasGateway = properties.GatewayAddresses.Any(gateway => IsLanAddress(gateway.Address));
            // Virtual adapters can report speeds unrelated to the underlying network.
            bool isVirtual = VirtualAdapterMarkers.Any(marker =>
                adapter.Description.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                adapter.Name.Contains(marker, StringComparison.OrdinalIgnoreCase));
            NetworkInterfaceType type = adapter.NetworkInterfaceType;
            bool isWireless = !isVirtual && type == NetworkInterfaceType.Wireless80211;
            bool isEthernet = !isVirtual && type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT;
            long linkSpeed = Math.Max(0, adapter.Speed);
            foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
            {
                if (IsLanAddress(unicast.Address) &&
                    unicast.DuplicateAddressDetectionState is DuplicateAddressDetectionState.Preferred or DuplicateAddressDetectionState.Deprecated)
                {
                    yield return new ReceiverAdapter(adapter.Name, unicast.Address, hasGateway, linkSpeed, isWireless, isEthernet);
                }
            }
        }
    }

    private static bool IsLanAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) &&
        address.GetAddressBytes()[0] is > 0 and < 224;

    public static string GetFailureMessage(int errorCode, DecoderMode decoder) => errorCode switch
    {
        50 when decoder == DecoderMode.Hardware => "硬件解码不可用。请在设置中选择自动或软件解码后重试。",
        10048 => "AirPlay 接收端口已被占用。请关闭其他屏幕镜像程序或 ClearCast 窗口后重试。",
        10049 or 10051 or 1231 => "当前网络不可用。请检查 Wi-Fi 或网线连接后重试。",
        1062 => "无法在局域网发布 AirPlay。请检查网络连接和 Windows 防火墙后重试。",
        10013 => "Windows 拒绝监听。请检查当前网络的防火墙和访问策略。",
        _ => "接收已中断，请重试。"
    };
}
