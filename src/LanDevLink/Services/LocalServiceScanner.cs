using System.Net.Http;

namespace LanDevLink.Services;

public sealed class LocalServiceScanner
{
    private static readonly int[] CommonDevelopmentPorts = [3000, 4200, 5000, 5173, 8000, 8080];

    public async Task<IReadOnlyList<string>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var scans = CommonDevelopmentPorts.Select(port => IsHttpServiceAsync(port, cancellationToken));
        var results = await Task.WhenAll(scans);

        return results
            .Where(result => result is not null)
            .Select(result => result!)
            .ToArray();
    }

    private static async Task<string?> IsHttpServiceAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(500) };
            using var request = new HttpRequestMessage(HttpMethod.Head, $"http://127.0.0.1:{port}/");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return $"http://localhost:{port}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}

