using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace LanDevLink.Services;

public sealed record ShareStartResult(Uri ShareUrl, DateTimeOffset? ExpiresAt, int RelatedServiceCount, bool HasCompatibilityWarning);

public sealed class ShareServer : IAsyncDisposable
{
    private const int MaxRelatedServices = 4;
    private const int MaxTextBytes = 1024 * 1024;
    private const int MaxDiscoveryAssets = 8;
    private const string InternalPrefix = "/__landevlink/s/";
    private static readonly Regex LocalOrigin = new(@"(?<scheme>https?|wss?)://(?<host>localhost|127\.0\.0\.1|\[::1\])(?::(?<port>\d{1,5}))?(?=[/?#\s""'\x60<>),;}]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly bool AutoCompatibilityEnabled = GetAutoCompatibilityEnabled();
    private static readonly Regex AssetReference = new("(?:src|href)\\s*=\\s*['\\\"](?<url>[^'\\\"]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, byte> _visitors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _originToService = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _serviceToOrigin = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private readonly SocketsHttpHandler _httpHandler = CreateLoopbackHandler();
    private readonly object _stopLock = new();
    private WebApplication? _application;
    private Task? _stopTask;
    private int _compatibilityWarning;
    private bool _discovering;

    public Uri? ShareUrl { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public int VisitorCount => _visitors.Count;
    public int RelatedServiceCount => _serviceToOrigin.Count;
    public bool HasCompatibilityWarning => Volatile.Read(ref _compatibilityWarning) != 0;

    public async Task<ShareStartResult> StartAsync(
        Uri target,
        string? password = null,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        if (_application is not null)
        {
            throw new InvalidOperationException("已有分享正在运行。");
        }
        if (password?.Length > 128)
        {
            throw new ArgumentException("访问密码不能超过 128 个字符。", nameof(password));
        }
        if (duration is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "分享时长必须大于零。");
        }

        TargetUrlValidator.Parse(target.ToString());
        await EnsureTargetIsReachableAsync(target, cancellationToken);

        var lanAddress = LanAddressService.GetPrimaryIpv4Address();
        var port = FindAvailablePort();
        var token = CreateToken(12);
        var pathPrefix = $"/s/{token}";
        var cookieValue = CreateToken(24);
        var requiredPassword = password ?? string.Empty;
        DateTimeOffset? expiresAt = duration is null ? null : DateTimeOffset.UtcNow.Add(duration.Value);
        ShareUrl = new Uri($"http://{lanAddress}:{port}{pathPrefix}/");
        ExpiresAt = expiresAt;
        _visitors.Clear();
        _originToService.Clear();
        _serviceToOrigin.Clear();
        Volatile.Write(ref _compatibilityWarning, 0);
        if (AutoCompatibilityEnabled)
        {
            _discovering = true;
            try { await DiscoverInitialServicesAsync(target, cancellationToken); }
            finally { _discovering = false; }
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ShareServer).Assembly.FullName
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port));
        builder.Services.AddHttpForwarder();

        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            if (IsExpired(expiresAt, DateTimeOffset.UtcNow))
            {
                context.Response.StatusCode = context.Request.Path.StartsWithSegments("/__landevlink")
                    ? StatusCodes.Status404NotFound : StatusCodes.Status410Gone;
                if (context.Response.StatusCode == StatusCodes.Status404NotFound) return;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("此分享已过期。", context.RequestAborted);
                return;
            }

            var isShareEntry = context.Request.Path.StartsWithSegments(pathPrefix);
            var hasAccess = context.Request.Cookies.TryGetValue("landevlink_access", out var cookie) && cookie == cookieValue;

            if (!isShareEntry && !hasAccess)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (isShareEntry && !hasAccess)
            {
                if (string.IsNullOrEmpty(requiredPassword))
                {
                    AppendAccessCookie(context, cookieValue, expiresAt);
                    context.Response.Redirect("/");
                    return;
                }

                if (HttpMethods.IsPost(context.Request.Method) && context.Request.HasFormContentType)
                {
                    var form = await context.Request.ReadFormAsync(context.RequestAborted);
                    var providedPassword = form["password"].ToString();
                    if (providedPassword.Length <= 128 && PasswordMatches(requiredPassword, providedPassword))
                    {
                        AppendAccessCookie(context, cookieValue, expiresAt);
                        context.Response.Redirect("/");
                        return;
                    }

                    await WritePasswordPageAsync(context, true);
                    return;
                }

                await WritePasswordPageAsync(context, false);
                return;
            }

            if (isShareEntry)
            {
                context.Response.Redirect("/");
                return;
            }

            if (context.Request.Path == PathString.Empty || context.Request.Path == "/")
            {
                var visitor = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                _visitors.TryAdd(visitor, 0);
            }

            await next(context);
        });
        var invoker = new HttpMessageInvoker(_httpHandler, disposeHandler: false);
        var transformer = new ShareTransformer(this, target);
        application.Map("/{**path}", async context =>
        {
            var destination = target;
            if (context.Request.Path.StartsWithSegments("/__landevlink"))
            {
                var path = context.Request.Path.Value ?? string.Empty;
                var idEnd = path.IndexOf('/', InternalPrefix.Length);
                if (!path.StartsWith(InternalPrefix, StringComparison.Ordinal) || idEnd < 0 ||
                    !_serviceToOrigin.TryGetValue(path[InternalPrefix.Length..idEnd], out destination))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                context.Items["relatedPath"] = path[..idEnd];
                context.Request.Path = new PathString(path[idEnd..]);
            }

            var forwarder = context.RequestServices.GetRequiredService<IHttpForwarder>();
            await forwarder.SendAsync(context, destination.GetLeftPart(UriPartial.Authority) + "/", invoker,
                ForwarderRequestConfig.Empty, transformer, application.Lifetime.ApplicationStopping);
        });

        try
        {
            await application.StartAsync(cancellationToken);
            _application = application;
            return new ShareStartResult(ShareUrl, expiresAt, RelatedServiceCount, HasCompatibilityWarning);
        }
        catch
        {
            await application.DisposeAsync();
            ResetState();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stopLock)
        {
            if (_stopTask is { IsCompleted: false }) return _stopTask;
            if (_application is null) return Task.CompletedTask;
            return _stopTask = StopCoreAsync(_application, cancellationToken);
        }
    }

    private async Task StopCoreAsync(WebApplication application, CancellationToken cancellationToken)
    {
        await application.StopAsync(cancellationToken);
        await application.DisposeAsync();
        ResetState();
        _application = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _httpHandler.Dispose();
        _discoveryLock.Dispose();
    }

    private static async Task EnsureTargetIsReachableAsync(Uri target, CancellationToken cancellationToken)
    {
        using var handler = CreateLoopbackHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException("无法连接本机服务，请先确认项目已经启动且地址正确。", exception);
        }
    }

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateToken(int byteCount)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static bool PasswordMatches(string expected, string actual)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    internal static bool IsExpired(DateTimeOffset? expiresAt, DateTimeOffset now) =>
        expiresAt is not null && now >= expiresAt;

    private static async Task WritePasswordPageAsync(HttpContext context, bool invalid)
    {
        context.Response.StatusCode = invalid ? StatusCodes.Status401Unauthorized : StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        var error = invalid ? "<p class=\"error\">密码不正确，请重试。</p>" : string.Empty;
        var html = $$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>LanDevLink 访问验证</title>
              <style>
                body{margin:0;min-height:100vh;display:grid;place-items:center;background:#f5f5f7;color:#1d1d1f;font:15px "Segoe UI",sans-serif}
                main{width:min(360px,calc(100% - 48px));padding:32px;background:#fff;border:1px solid #e2e2e7;border-radius:14px;box-shadow:0 18px 50px #00000012}
                h1{margin:0 0 8px;font-size:24px}p{color:#6e6e73;line-height:1.6}.error{color:#d70015}
                input,button{box-sizing:border-box;width:100%;min-height:44px;border-radius:8px;font:inherit}
                input{margin:10px 0 14px;padding:10px 12px;border:1px solid #d2d2d7}button{border:0;background:#0071e3;color:#fff;font-weight:600;cursor:pointer}
              </style>
            </head>
            <body><main><h1>请输入访问密码</h1><p>此局域网分享已启用密码保护。</p>{{error}}<form method="post"><input type="password" name="password" maxlength="128" required autofocus autocomplete="current-password"><button type="submit">进入分享</button></form></main></body>
            </html>
            """;
        await context.Response.WriteAsync(html, context.RequestAborted);
    }

    private static void AppendAccessCookie(HttpContext context, string cookieValue, DateTimeOffset? expiresAt)
    {
        context.Response.Cookies.Append("landevlink_access", cookieValue, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
            Expires = expiresAt
        });
    }

    private void ResetState()
    {
        ShareUrl = null;
        ExpiresAt = null;
        _visitors.Clear();
        _originToService.Clear();
        _serviceToOrigin.Clear();
        Volatile.Write(ref _compatibilityWarning, 0);
    }

    private static bool GetAutoCompatibilityEnabled()
    {
        var setting = Environment.GetEnvironmentVariable("LANDEVLINK_AUTO_SHARE");
#if DEBUG
        return setting != "0";
#else
        return setting == "1";
#endif
    }

    private static SocketsHttpHandler CreateLoopbackHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(1),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = context.DnsEndPoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
                : new[] { IPAddress.Parse(context.DnsEndPoint.Host) };
            foreach (var address in addresses)
            {
                if (!IPAddress.IsLoopback(address)) throw new HttpRequestException("仅允许连接本机服务。");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) when (address == IPAddress.Loopback && addresses.Length > 1)
                {
                    socket.Dispose();
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            throw new HttpRequestException("无法连接本机服务。");
        }
    };

    private async Task DiscoverInitialServicesAsync(Uri target, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var client = new HttpClient(_httpHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var rootText = await InspectAsync(target);
            if (rootText is null) return;
            var assets = AssetReference.Matches(rootText).Select(match => match.Groups["url"].Value)
                .Select(value => Uri.TryCreate(target, value, out var uri) ? uri : null)
                .Where(uri => uri is not null && uri.Scheme is "http" or "https" &&
                              uri.GetLeftPart(UriPartial.Authority).Equals(target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                .DistinctBy(uri => uri!.AbsoluteUri).Take(MaxDiscoveryAssets).ToArray();
            foreach (var asset in assets) await InspectAsync(asset!);

            async Task<string?> InspectAsync(Uri uri)
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!IsTextResponse(response) || response.Content.Headers.ContentLength > MaxTextBytes)
                {
                    if (response.Content.Headers.ContentLength > MaxTextBytes) MarkWarning();
                    return null;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                var bytes = await ReadPrefixAsync(stream, deadline.Token);
                if (bytes.Length > MaxTextBytes)
                {
                    MarkWarning();
                    return null;
                }
                var text = Encoding.UTF8.GetString(bytes);
                await RewriteAsync(text, target, deadline.Token);
                return text;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            MarkWarning();
        }
    }

    private async Task<string> RewriteAsync(string text, Uri mainTarget, CancellationToken cancellationToken)
    {
        if (ShareUrl is null || !AutoCompatibilityEnabled) return text;
        if (text.Contains("serviceWorker.register", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("new Worker(", StringComparison.Ordinal) ||
            text.Contains("new SharedWorker(", StringComparison.Ordinal) ||
            text.Contains("<iframe", StringComparison.OrdinalIgnoreCase)) MarkWarning();
        if (!LocalOrigin.IsMatch(text))
        {
            if (text.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("fetch(", StringComparison.Ordinal) || text.Contains("WebSocket(", StringComparison.Ordinal))) MarkWarning();
            return text;
        }
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in LocalOrigin.Matches(text))
        {
            if (replacements.ContainsKey(match.Value)) continue;
            var scheme = match.Groups["scheme"].Value.ToLowerInvariant();
            var httpScheme = scheme is "ws" or "wss" ? (scheme == "ws" ? "http" : "https") : scheme;
            if (!Uri.TryCreate(httpScheme + match.Value[scheme.Length..], UriKind.Absolute, out var origin) ||
                !TargetUrlValidator.IsAllowedLoopbackHost(origin))
            {
                MarkWarning();
                continue;
            }
            var shareScheme = scheme is "ws" or "wss" ? "ws" : "http";
            var shareOrigin = $"{shareScheme}://{ShareUrl.Authority}";
            if (origin.GetLeftPart(UriPartial.Authority).Equals(mainTarget.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                replacements[match.Value] = shareOrigin;
                continue;
            }
            var id = await GetOrDiscoverServiceAsync(origin, cancellationToken);
            replacements[match.Value] = id is null
                ? $"{shareOrigin}/__landevlink/unavailable"
                : $"{shareOrigin}{InternalPrefix}{id}";
        }
        return LocalOrigin.Replace(text, match => replacements.GetValueOrDefault(match.Value) ?? match.Value);
    }

    private async Task<string?> GetOrDiscoverServiceAsync(Uri origin, CancellationToken cancellationToken)
    {
        var key = origin.GetLeftPart(UriPartial.Authority);
        if (_originToService.TryGetValue(key, out var existing)) return existing;
        if (!_discovering)
        {
            MarkWarning();
            return null;
        }
        await _discoveryLock.WaitAsync(cancellationToken);
        try
        {
            if (_originToService.TryGetValue(key, out existing)) return existing;
            if (_originToService.Count >= MaxRelatedServices)
            {
                MarkWarning();
                return null;
            }
            using var client = new HttpClient(_httpHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(1) };
            using var request = new HttpRequestMessage(HttpMethod.Head, key + "/");
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                MarkWarning();
                return null;
            }
            var id = CreateToken(12);
            _originToService[key] = id;
            _serviceToOrigin[id] = origin;
            return id;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    private static bool IsTextResponse(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return mediaType is not null &&
            (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
             mediaType.Equals("text/css", StringComparison.OrdinalIgnoreCase) ||
             mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
             mediaType.Equals("application/manifest+json", StringComparison.OrdinalIgnoreCase) ||
             mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<byte[]> ReadPrefixAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length <= MaxTextBytes)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaxTextBytes + 1 - buffer.Length)), cancellationToken);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private void MarkWarning() => Volatile.Write(ref _compatibilityWarning, 1);

    private sealed class ShareTransformer(ShareServer server, Uri mainTarget) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(context, request, destinationPrefix, cancellationToken);
            request.Headers.Remove("Accept-Encoding");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            request.Headers.Remove("Cookie");
            var cookies = context.Request.Cookies.Where(cookie => cookie.Key != "landevlink_access")
                .Select(cookie => cookie.Key + "=" + cookie.Value);
            var cookieHeader = string.Join("; ", cookies);
            if (cookieHeader.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? response, CancellationToken cancellationToken)
        {
            if (!await base.TransformResponseAsync(context, response, cancellationToken) || response is null) return false;
            if (!AutoCompatibilityEnabled) return true;

            if (response.Headers.Location is { IsAbsoluteUri: true } location &&
                TargetUrlValidator.IsAllowedLoopbackHost(location))
            {
                var key = location.GetLeftPart(UriPartial.Authority);
                var shareBase = server.ShareUrl!.GetLeftPart(UriPartial.Authority);
                if (location.Scheme is not ("http" or "https") ||
                    (!key.Equals(mainTarget.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) &&
                     !server._originToService.TryGetValue(key, out _)))
                {
                    server.MarkWarning();
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    context.Response.Headers.Remove("Location");
                    context.Response.ContentLength = null;
                    await context.Response.WriteAsync("无法连接项目关联服务。", cancellationToken);
                    return false;
                }
                var prefix = server._originToService.TryGetValue(key, out var id)
                    ? $"{shareBase}{InternalPrefix}{id}" : shareBase;
                context.Response.Headers.Location = prefix + location.PathAndQuery + location.Fragment;
            }
            if (context.Response.Headers.TryGetValue("Set-Cookie", out var cookies))
            {
                var relatedPath = context.Items["relatedPath"] as string;
                context.Response.Headers["Set-Cookie"] = cookies.Select(cookie => RewriteCookie(cookie!, relatedPath))
                    .Where(cookie => cookie.Length > 0).ToArray();
            }
            if (!IsTextResponse(response) || HttpMethods.IsHead(context.Request.Method) || response.Content.Headers.ContentEncoding.Count > 0)
            {
                if (response.Content.Headers.ContentEncoding.Count > 0 && IsTextResponse(response)) server.MarkWarning();
                return true;
            }
            if (response.Content.Headers.ContentLength > MaxTextBytes)
            {
                server.MarkWarning();
                return true;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await ReadPrefixAsync(stream, cancellationToken);
            if (bytes.Length > MaxTextBytes)
            {
                server.MarkWarning();
                await context.Response.Body.WriteAsync(bytes, cancellationToken);
                await stream.CopyToAsync(context.Response.Body, cancellationToken);
                return false;
            }
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "utf-8";
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException)
            {
                server.MarkWarning();
                await context.Response.Body.WriteAsync(bytes, cancellationToken);
                return false;
            }
            string originalText;
            string rewrittenText;
            try
            {
                originalText = encoding.GetString(bytes);
                rewrittenText = await server.RewriteAsync(originalText, mainTarget, cancellationToken);
            }
            catch (DecoderFallbackException)
            {
                server.MarkWarning();
                await context.Response.Body.WriteAsync(bytes, cancellationToken);
                return false;
            }
            if (rewrittenText != originalText &&
                (context.Response.Headers.ContainsKey("Content-Security-Policy") ||
                 originalText.Contains("integrity=", StringComparison.OrdinalIgnoreCase)))
                server.MarkWarning();
            var output = encoding.GetBytes(rewrittenText);
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Digest");
            context.Response.Headers.Remove("Content-MD5");
            context.Response.Headers.Remove("Content-Encoding");
            context.Response.ContentLength = output.Length;
            await context.Response.Body.WriteAsync(output, cancellationToken);
            return false;
        }

        private static string RewriteCookie(string cookie, string? relatedPath)
        {
            var parts = cookie.Split(';');
            if (parts[0].TrimStart().StartsWith("landevlink_access=", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            var originalPath = parts.Skip(1).Select(part => part.Trim())
                .FirstOrDefault(part => part.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))?[5..] ?? "/";
            if (!originalPath.StartsWith('/')) originalPath = "/";
            var attributes = parts.Skip(1).Select(part => part.Trim())
                .Where(part => !part.StartsWith("Path=", StringComparison.OrdinalIgnoreCase) &&
                               !(part.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase) &&
                                 part[7..].Trim().TrimStart('.') is "localhost" or "127.0.0.1" or "::1"));
            var path = relatedPath + originalPath;
            return string.Join("; ", new[] { parts[0].Trim(), "Path=" + path }.Concat(attributes));
        }
    }
}
