using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using LanDevLink.Services;
using LanDevLink.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

static async Task<(WebApplication App, Uri Url)> StartServiceAsync(Action<WebApplication> configure)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    var app = builder.Build();
    configure(app);
    await app.StartAsync();
    return (app, new Uri(app.Urls.Single()));
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static Task RunOnDispatcherAsync(Func<Task> check)
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.InvokeAsync(async () =>
        {
            try { await check(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
            finally { dispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
}

Uri? backendUrlForRoutes = null;
Environment.SetEnvironmentVariable("LANDEVLINK_AUTO_SHARE", "1");
var (backend, backendUrl) = await StartServiceAsync(app =>
{
    app.UseWebSockets();
    app.MapGet("/users", () => Results.Json(new { name = "Alice" }));
    app.MapPost("/echo", async (HttpRequest request) =>
    {
        using var reader = new StreamReader(request.Body);
        return Results.Text(await reader.ReadToEndAsync() + request.QueryString);
    });
    app.MapGet("/redirect", (HttpResponse response) =>
    {
        return Results.Redirect(backendUrlForRoutes + "users");
    });
    app.MapGet("/cookie", (HttpContext context) =>
    {
        context.Response.Headers.Append("Set-Cookie", "session=abc; Domain=localhost; Path=/; HttpOnly");
        return context.Request.Cookies.ContainsKey("landevlink_access") ? "leaked" : "ok";
    });
    app.Map("/socket", async context =>
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var data = new byte[32];
        var received = await socket.ReceiveAsync(data, context.RequestAborted);
        await socket.SendAsync(data.AsMemory(0, received.Count), received.MessageType, true, context.RequestAborted);
    });
    app.Map("/hold", async context =>
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
        catch (OperationCanceledException) { }
    });
});
backendUrlForRoutes = backendUrl;
var (unrelated, unrelatedUrl) = await StartServiceAsync(app => app.MapGet("/secret", () => "private"));
var (frontend, frontendUrl) = await StartServiceAsync(app =>
{
    app.MapGet("/", () => Results.Content("<script src='/app.js'></script>", "text/html"));
    app.MapGet("/page", () => Results.Content($"<a href='{backendUrl}users'>Users</a>", "text/html"));
    app.MapGet("/app.js", () => Results.Text($"const api = '{backendUrl}users';", "application/javascript"));
    app.MapGet("/reflect", (HttpRequest request) => Results.Text(
        $"const api = 'http://127.0.0.1:{request.Query["port"]}/secret';", "application/javascript"));
    app.MapGet("/style.css", () => Results.Text($"/* {backendUrl}users */", "text/css"));
    app.MapGet("/config.json", () => Results.Json(new { api = backendUrl + "users" }));
    app.MapGet("/compressed", async context =>
    {
        context.Response.ContentType = "application/javascript";
        context.Response.Headers.ContentEncoding = "gzip";
        await using var gzip = new GZipStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
        await gzip.WriteAsync(Encoding.UTF8.GetBytes($"const api = '{backendUrl}users';"));
    });
    app.MapGet("/large", () => Results.Text(new string('x', 1024 * 1024 + 1) + backendUrl + "users", "text/html"));
    app.MapGet("/download", () => Results.Bytes([0, 1, 2, 255], "application/octet-stream"));
    app.MapGet("/events", async context =>
    {
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync("data: ready\n\n");
        await context.Response.Body.FlushAsync();
        try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
        catch (OperationCanceledException) { }
    });
});

try
{
    await using var share = new ShareServer();
    var result = await share.StartAsync(frontendUrl, "secret", TimeSpan.FromMinutes(2));
    Check(result.RelatedServiceCount == 1, "Backend was not discovered at startup.");
    using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
    using var client = new HttpClient(handler);
    var entry = await client.GetAsync(result.ShareUrl);
    Check(entry.StatusCode == HttpStatusCode.OK, "Password entry page was not shown.");
    var denied = await client.GetAsync(new Uri(result.ShareUrl, "/"));
    Check(denied.StatusCode == HttpStatusCode.NotFound, "Root was accessible without password.");
    var login = await client.PostAsync(result.ShareUrl, new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = "secret" }));
    Check(login.StatusCode == HttpStatusCode.Redirect, "Password login failed.");
    var html = await client.GetStringAsync(new Uri(result.ShareUrl, "/page"));
    Check(!html.Contains(backendUrl.ToString(), StringComparison.Ordinal), "Backend URL was left in HTML.");
    var path = Regex.Match(html, @"/__landevlink/s/[^/]+/users").Value;
    Check(path.Length > 0, "Backend path from a linked script was not discovered.");
    var users = await client.GetStringAsync(new Uri(result.ShareUrl, path));
    Check(users.Contains("Alice", StringComparison.Ordinal), "Backend response was not forwarded.");
    foreach (var route in new[] { "/app.js", "/style.css", "/config.json" })
    {
        var text = await client.GetStringAsync(new Uri(result.ShareUrl, route));
        Check(text.Contains(path, StringComparison.Ordinal), route + " was not rewritten.");
    }
    var compressed = await client.GetStringAsync(new Uri(result.ShareUrl, "/compressed"));
    Check(compressed.Contains(path, StringComparison.Ordinal), "Compressed text was not rewritten.");
    var download = await client.GetByteArrayAsync(new Uri(result.ShareUrl, "/download"));
    Check(download.SequenceEqual(new byte[] { 0, 1, 2, 255 }), "Binary download changed.");
    var large = await client.GetStringAsync(new Uri(result.ShareUrl, "/large"));
    Check(large.Contains(backendUrl.ToString(), StringComparison.Ordinal) && share.HasCompatibilityWarning,
        "Oversized text was modified or its warning was lost.");
    var posted = await client.PostAsync(new Uri(result.ShareUrl, path.Replace("/users", "/echo") + "?q=1"), new StringContent("body"));
    Check(await posted.Content.ReadAsStringAsync() == "body?q=1", "Method, body, or query changed.");
    var forged = await client.GetAsync(new Uri(result.ShareUrl, "/__landevlink/s/forged/users"));
    Check(forged.StatusCode == HttpStatusCode.NotFound, "Forged service ID was accepted.");
    var reflected = await client.GetStringAsync(new Uri(result.ShareUrl, "/reflect?port=" + unrelatedUrl.Port));
    Check(reflected.Contains("/__landevlink/unavailable", StringComparison.Ordinal) &&
          share.RelatedServiceCount == 1, "Visitor-controlled content expanded the service allowlist.");
    var redirect = await client.GetAsync(new Uri(result.ShareUrl, path.Replace("/users", "/redirect")));
    Check(redirect.Headers.Location?.AbsolutePath == path &&
          redirect.Headers.Location.Authority == result.ShareUrl.Authority, "Backend redirect was not rewritten.");
    var cookieResponse = await client.GetAsync(new Uri(result.ShareUrl, path.Replace("/users", "/cookie")));
    Check(await cookieResponse.Content.ReadAsStringAsync() == "ok", "Access cookie leaked upstream.");
    var backendCookie = cookieResponse.Headers.GetValues("Set-Cookie").Single();
    Check(!backendCookie.Contains("Domain=", StringComparison.OrdinalIgnoreCase) &&
          backendCookie.Contains("Path=" + path[..path.LastIndexOf('/')], StringComparison.Ordinal),
        "Backend cookie was not restricted to its service path.");
    using (var socket = new ClientWebSocket())
    {
        socket.Options.SetRequestHeader("Cookie", handler.CookieContainer.GetCookieHeader(new Uri(result.ShareUrl, "/")));
        var socketUrl = new UriBuilder(new Uri(result.ShareUrl, path.Replace("/users", "/socket"))) { Scheme = "ws" }.Uri;
        await socket.ConnectAsync(socketUrl, CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, CancellationToken.None);
        var data = new byte[16];
        var received = await socket.ReceiveAsync(data, CancellationToken.None);
        Check(Encoding.UTF8.GetString(data, 0, received.Count) == "ping", "WebSocket forwarding failed.");
    }
    using (var heldSocket = new ClientWebSocket())
    {
        heldSocket.Options.SetRequestHeader("Cookie", handler.CookieContainer.GetCookieHeader(new Uri(result.ShareUrl, "/")));
        var heldUrl = new UriBuilder(new Uri(result.ShareUrl, path.Replace("/users", "/hold"))) { Scheme = "ws" }.Uri;
        await heldSocket.ConnectAsync(heldUrl, CancellationToken.None);
        using var events = await client.GetAsync(new Uri(result.ShareUrl, "/events"), HttpCompletionOption.ResponseHeadersRead);
        Check(events.IsSuccessStatusCode, "Streaming request was not started.");
        var firstStop = share.StopAsync();
        var repeatedStop = share.StopAsync();
        if (!firstStop.IsCompleted && repeatedStop.IsCompleted)
        {
            heldSocket.Dispose();
            await firstStop.WaitAsync(TimeSpan.FromSeconds(10));
            throw new Exception("Repeated stop did not await the active shutdown.");
        }
        await firstStop.WaitAsync(TimeSpan.FromSeconds(10));
        await repeatedStop;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var closed = await heldSocket.ReceiveAsync(new byte[16], deadline.Token);
            Check(closed.MessageType == WebSocketMessageType.Close, "WebSocket still received data after shutdown.");
        }
        catch (WebSocketException) { }
    }
    Check(share.ShareUrl is null && share.RelatedServiceCount == 0 && share.VisitorCount == 0,
        "Share state was not cleared after shutdown.");
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var response = await client.GetAsync(result.ShareUrl, deadline.Token);
        throw new Exception("Old share URL was still reachable after shutdown.");
    }
    catch (HttpRequestException) { }
    var restarted = await share.StartAsync(frontendUrl);
    using var freshClient = new HttpClient(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer()
    });
    await freshClient.GetAsync(restarted.ShareUrl);
    var oldId = await freshClient.GetAsync(new Uri(restarted.ShareUrl, path));
    Check(oldId.StatusCode == HttpStatusCode.NotFound, "Previous service ID survived restart.");
    await share.StopAsync();
    await RunOnDispatcherAsync(async () =>
    {
        await using var viewModel = new MainViewModel { TargetUrl = frontendUrl.ToString() };
        await viewModel.StartAsync();
        Check(viewModel.IsActive && viewModel.CanStop, "View model did not start sharing: " + viewModel.ErrorMessage);
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        await browser.GetStringAsync(viewModel.ShareUrl);
        using var events = await browser.GetAsync(new Uri(new Uri(viewModel.ShareUrl), "/events"), HttpCompletionOption.ResponseHeadersRead);
        var sawStopping = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.IsStopping) && viewModel.IsStopping)
                sawStopping = !viewModel.CanStop && !viewModel.CanStart && viewModel.StopButtonText == "正在停止...";
        };
        var stop = viewModel.StopAsync();
        if (!stop.IsCompleted)
            Check(viewModel.IsActive && viewModel.IsStopping && !viewModel.CanStop, "UI allowed repeated stops during shutdown.");
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Check(sawStopping && !viewModel.IsActive && !viewModel.IsStopping && !viewModel.CanStop && viewModel.CanStart &&
              viewModel.ShareUrl.Length == 0 && viewModel.QrCode is null && viewModel.StatusMessage == "分享已停止",
            "One stop did not finish and reset the UI.");
        await viewModel.StartAsync();
        Check(viewModel.IsActive && viewModel.CanStop, "UI could not restart sharing after shutdown.");
        await viewModel.StopAsync();
    }).WaitAsync(TimeSpan.FromSeconds(20));
    Console.WriteLine("Integration checks passed.");
}
finally
{
    await frontend.StopAsync();
    await backend.StopAsync();
    await unrelated.StopAsync();
    await frontend.DisposeAsync();
    await backend.DisposeAsync();
    await unrelated.DisposeAsync();
}
