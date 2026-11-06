using System.Net;
using System.Net.Sockets;

namespace LanDevLink.Services;

public sealed class LocalServiceScanner
{
    private static readonly int[] CommonDevelopmentPorts = [3000, 4200, 5000, 5173, 8000, 8080];

    public async Task<IReadOnlyList<string>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var scans = CommonDevelopmentPorts.Select(port => IsOpenAsync(port, cancellationToken));
        var results = await Task.WhenAll(scans);

        return results
            .Where(result => result is not null)
            .Select(result => result!)
            .ToArray();
    }

    private static async Task<string?> IsOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(350), cancellationToken);
            return $"http://localhost:{port}";
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return null;
        }
    }
}

