using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace LanDevLink.Services;

public sealed class ShareServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, byte> _visitors = new(StringComparer.Ordinal);
    private WebApplication? _application;

    public Uri? ShareUrl { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public int VisitorCount => _visitors.Count;

    public async Task StartAsync(
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

        var routes = new[]
        {
            new RouteConfig
            {
                RouteId = "share",
                ClusterId = "local-target",
                Match = new RouteMatch { Path = "/{**catch-all}" }
            }
        };
        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "local-target",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["target"] = new() { Address = target.GetLeftPart(UriPartial.Authority) + "/" }
                }
            }
        };

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ShareServer).Assembly.FullName
        });
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port));
        builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);

        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            if (IsExpired(expiresAt, DateTimeOffset.UtcNow))
            {
                context.Response.StatusCode = StatusCodes.Status410Gone;
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
        application.MapReverseProxy();

        try
        {
            await application.StartAsync(cancellationToken);
            _application = application;
        }
        catch
        {
            await application.DisposeAsync();
            ResetState();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var application = _application;
        if (application is null)
        {
            return;
        }

        _application = null;
        await application.StopAsync(cancellationToken);
        await application.DisposeAsync();
        ResetState();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static async Task EnsureTargetIsReachableAsync(Uri target, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
    }
}
