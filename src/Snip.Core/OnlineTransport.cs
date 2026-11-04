using System.Net;
using System.Runtime.Versioning;

namespace Snip.Core;

/// <summary>Uses the current Windows user's proxy policy, including PAC and local rule-based proxies.</summary>
public static class OnlineTransport
{
    public static HttpClient CreateClient() => new(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpMessageHandler CreateHandler() => OperatingSystem.IsWindows()
        ? CreateWindowsHandler()
        : new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };

    [SupportedOSPlatform("windows")]
    private static HttpMessageHandler CreateWindowsHandler() => new WinHttpHandler
    {
        WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseWinInetProxy,
        CookieUsePolicy = CookieUsePolicy.IgnoreCookies,
        AutomaticRedirection = false
    };
}
