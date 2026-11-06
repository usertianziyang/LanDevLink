using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanDevLink.Services;

public static class LanAddressService
{
    public static IPAddress GetPrimaryIpv4Address()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .Where(network => network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Select(network => network.GetIPProperties())
            .Where(properties => properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(properties => properties.UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .ToArray();

        return candidates.FirstOrDefault(IsPrivateAddress)
            ?? candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("未检测到可用的局域网 IPv4 地址，请确认 Wi-Fi 或网线已连接。");
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }
}

