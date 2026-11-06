using System.Diagnostics;
using System.Net;
using System.Windows;
using LanDevLink.Services;

namespace LanDevLink;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
#if DEBUG
        SelfCheck.Run();
#endif
        base.OnStartup(e);
    }

    private static class SelfCheck
    {
        public static void Run()
        {
            Debug.Assert(TargetUrlValidator.Parse("localhost:5173").Port == 5173);
            Debug.Assert(NetworkDiagnosticsService.IsInSameSubnet(
                IPAddress.Parse("192.168.10.5"),
                IPAddress.Parse("192.168.10.80"),
                IPAddress.Parse("255.255.255.0")));
            Debug.Assert(!NetworkDiagnosticsService.IsInSameSubnet(
                IPAddress.Parse("192.168.10.5"),
                IPAddress.Parse("192.168.11.80"),
                IPAddress.Parse("255.255.255.0")));
            Debug.Assert(ShareServer.PasswordMatches("correct horse", "correct horse"));
            Debug.Assert(!ShareServer.PasswordMatches("correct horse", "wrong battery"));
            var now = DateTimeOffset.UtcNow;
            Debug.Assert(!ShareServer.IsExpired(null, now));
            Debug.Assert(ShareServer.IsExpired(now, now));

            try
            {
                TargetUrlValidator.Parse("https://example.com");
                Debug.Fail("Public targets must be rejected.");
            }
            catch (ArgumentException)
            {
            }
        }
    }
}

