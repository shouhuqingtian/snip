using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Snip.Core;

namespace Snip.Windows.Checks;

internal static class NetworkProxyChecks
{
    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);

    public static int Run()
    {
        // This fixture temporarily changes per-user settings only on the disposable CI desktop.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        { Console.Error.WriteLine("Network proxy fixture requires the disposable Windows CI desktop."); return 1; }
        using var registry = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        string[] names = ["ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL", "AutoDetect"];
        var old = names.ToDictionary(n => n, n => registry.GetValue(n));
        var kinds = names.Where(n => old[n] is not null).ToDictionary(n => n, n => registry.GetValueKind(n));
        try { return CheckAsync(registry).GetAwaiter().GetResult(); }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
        finally
        {
            foreach (string name in names)
                if (old[name] is { } value) registry.SetValue(name, value, kinds[name]); else registry.DeleteValue(name, false);
            Notify();
        }
    }
    private static void Notify()
    {
        if (!InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0) || !InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0))
            throw new InvalidOperationException("Could not notify Windows about fixture proxy settings.");
    }
    private static async Task<int> CheckAsync(RegistryKey registry)
    {
        void Configure(int? port)
        {
            registry.DeleteValue("AutoConfigURL", false); registry.SetValue("AutoDetect", 0);
            registry.SetValue("ProxyEnable", port is null ? 0 : 1); registry.SetValue("ProxyOverride", "");
            if (port is not null) registry.SetValue("ProxyServer", "127.0.0.1:" + port);
            Notify();
        }
        using var first = new Server("FIRST"); using var second = new Server("SECOND");
        Configure(null);
        using var client = OnlineTransport.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Require(await client.GetStringAsync(first.Direct, deadline.Token) == "FIRST", "Initial direct request failed.");
        Configure(first.Port);
        var remote = new Uri("http://snip-proxy-fixture.invalid/test");
        Require(await client.GetStringAsync(remote, deadline.Token) == "FIRST", "Enabling the current Windows proxy was not picked up by the existing client.");
        Require(first.Requests.Any(r => r.StartsWith("GET http://snip-proxy-fixture.invalid/")), "Request did not reach the system proxy.");
        Configure(second.Port);
        Require(await client.GetStringAsync(remote, deadline.Token) == "SECOND", "Switching the system proxy port did not affect the existing client.");
        Configure(null);
        Require(await client.GetStringAsync(first.Direct, deadline.Token) == "FIRST", "Disabling the system proxy did not restore direct requests.");
        Console.WriteLine("PASS existing native Windows client follows system proxy enable, port change and disable; proxy receives the original destination for rule-based routing.");
        Directory.CreateDirectory("artifacts/validation");
        File.WriteAllText("artifacts/validation/network-proxy.json", JsonSerializer.Serialize(new { NativeWindowsTransport = true, InitialDirect = true, EnableWithoutRestart = true, PortChangeWithoutRestart = true, DisableWithoutRestart = true, OriginalDestinationPreserved = true }));
        return 0;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Server : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Requests = [];
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Uri Direct => new("http://127.0.0.1:" + Port + "/");
        public Server(string text)
        {
            listener.Start();
            serving = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                        using var stream = socket.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        Requests.Add(await reader.ReadLineAsync(stop.Token) ?? "");
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + text.Length + "\r\nConnection: close\r\n\r\n" + text);
                        await stream.WriteAsync(response, stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (SocketException) when (stop.IsCancellationRequested) { }
            });
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); serving.GetAwaiter().GetResult(); stop.Dispose(); }
    }
}
