using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;

namespace LanDevLink.Services;

public sealed record NetworkConnection(
    string AdapterName,
    string ConnectionType,
    IPAddress Address,
    IPAddress SubnetMask,
    IPAddress? Gateway)
{
    public string Summary => $"{AdapterName} · {ConnectionType}";
    public string AddressText => $"IPv4 {Address}/{NetworkDiagnosticsService.GetPrefixLength(SubnetMask)} · 网关 {Gateway?.ToString() ?? "未检测到"}";
}

public sealed record LinkDiagnosticResult(string Title, string Details);

public static class NetworkDiagnosticsService
{
    public static IReadOnlyList<NetworkConnection> GetActiveIpv4Connections()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .Where(network => network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(network =>
            {
                var properties = network.GetIPProperties();
                var gateway = properties.GatewayAddresses
                    .Select(item => item.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any));

                return properties.UnicastAddresses
                    .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Where(unicast => !IPAddress.IsLoopback(unicast.Address) && unicast.IPv4Mask is not null)
                    .Select(unicast => new NetworkConnection(
                        network.Name,
                        GetConnectionType(network.NetworkInterfaceType),
                        unicast.Address,
                        unicast.IPv4Mask,
                        gateway));
            })
            .OrderByDescending(connection => connection.Gateway is not null)
            .ThenBy(connection => connection.AdapterName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static async Task<LinkDiagnosticResult> DiagnoseAsync(
        string value,
        IReadOnlyList<NetworkConnection> localConnections,
        CancellationToken cancellationToken = default)
    {
        var uri = ParseSharedLink(value);
        var targetAddresses = await ResolveIpv4AddressesAsync(uri, cancellationToken);
        if (targetAddresses.Count == 0)
        {
            return new LinkDiagnosticResult(
                "分享地址没有可用的 IPv4",
                "当前版本按 IPv4 子网检测。请让分享者提供包含 IPv4 地址的局域网链接。");
        }

        var targetAddress = targetAddresses.FirstOrDefault(address =>
                localConnections.Any(connection => IsInSameSubnet(connection.Address, address, connection.SubnetMask)))
            ?? targetAddresses[0];

        if (IPAddress.IsLoopback(targetAddress))
        {
            return new LinkDiagnosticResult(
                "链接指向了你自己的电脑",
                $"目标地址：{targetAddress}:{uri.Port}\nlocalhost 或 127.0.0.1 只能在分享者电脑上访问，请让对方复制 LanDevLink 生成的局域网分享链接。");
        }

        if (localConnections.Count == 0)
        {
            return new LinkDiagnosticResult(
                "未检测到活动的 IPv4 网络连接",
                $"目标地址：{targetAddress}:{uri.Port}\n请先连接 Wi-Fi 或网线，关闭不必要的 VPN 后刷新网络信息。");
        }

        var sameSubnet = localConnections.Any(connection =>
            IsInSameSubnet(connection.Address, targetAddress, connection.SubnetMask));
        var portReachable = await CanConnectAsync(targetAddress, uri.Port, cancellationToken);
        var subnetText = sameSubnet ? "与本机某个活动连接处于同一 IPv4 子网" : "与本机活动连接不在同一 IPv4 子网";
        var portText = portReachable ? "目标端口可以建立连接" : "目标端口无法建立连接";

        if (!sameSubnet)
        {
            var advice = portReachable
                ? "网络允许跨子网访问；若浏览器仍打不开，请确认链接完整且没有启用会改写局域网流量的代理。"
                : "这是最可能的问题。请与分享者连接同一 Wi-Fi 或有线网络，并检查 VPN、访客网络和路由器客户端隔离。";
            return new LinkDiagnosticResult(
                "本机与分享地址不在同一局域网子网",
                $"分享地址：{targetAddress}:{uri.Port}\n子网判断：{subnetText}\n端口检测：{portText}\n建议：{advice}");
        }

        if (!portReachable)
        {
            return new LinkDiagnosticResult(
                "同一局域网，但分享端口无法连接",
                $"分享地址：{targetAddress}:{uri.Port}\n子网判断：{subnetText}\n端口检测：{portText}\n建议：让分享者确认分享仍在运行，并允许 LanDevLink 通过 Windows 专用网络防火墙。");
        }

        return new LinkDiagnosticResult(
            "网络与分享端口均可连接",
            $"分享地址：{targetAddress}:{uri.Port}\n子网判断：{subnetText}\n端口检测：{portText}\n若页面仍打不开，请重新复制完整链接，或让分享者停止后重新创建分享。");
    }

    public static bool IsInSameSubnet(IPAddress first, IPAddress second, IPAddress subnetMask)
    {
        if (first.AddressFamily != AddressFamily.InterNetwork ||
            second.AddressFamily != AddressFamily.InterNetwork ||
            subnetMask.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var firstBytes = first.GetAddressBytes();
        var secondBytes = second.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        return firstBytes.Select((value, index) => (byte)(value & maskBytes[index]))
            .SequenceEqual(secondBytes.Select((value, index) => (byte)(value & maskBytes[index])));
    }

    public static int GetPrefixLength(IPAddress subnetMask)
    {
        return subnetMask.GetAddressBytes().Sum(value => BitOperations.PopCount(value));
    }

    private static Uri ParseSharedLink(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("请输入别人分享给你的链接。", nameof(value));
        }

        var normalized = value.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"http://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("分享链接格式无效，请粘贴完整的 http:// 或 https:// 链接。", nameof(value));
        }

        return uri;
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveIpv4AddressesAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(uri.Host, out var address))
        {
            return address.AddressFamily == AddressFamily.InterNetwork ? [address] : [];
        }

        try
        {
            return (await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken))
                .Where(item => item.AddressFamily == AddressFamily.InterNetwork)
                .ToArray();
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException("无法解析分享链接中的主机名，请检查链接或当前网络。", exception);
        }
    }

    private static async Task<bool> CanConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string GetConnectionType(NetworkInterfaceType type)
    {
        return type switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT => "以太网",
            NetworkInterfaceType.Ppp => "拨号或 VPN",
            _ => type.ToString()
        };
    }
}
