using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Snip.Translation;

internal static class NativeGpuLoggingChecks
{
    // Exercise the pinned native callback/verbosity mapping even on a machine without a physical GPU.
    // CPU-only execution must report 0 layers; this is never claimed as GPU inference validation.
    public static async Task<int> RunAsync(string folder)
    {
        try
        {
            var models = new ModelStore(folder);
            var backend = OperatingSystem.IsWindows() ? TranslationBackend.Vulkan : TranslationBackend.Cpu;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            if (!await models.IsInstalledAsync("en-zh", deadline.Token) || !await models.IsRuntimeInstalledAsync(backend, deadline.Token))
                throw new InvalidOperationException("Native logging check requires the pinned model and runtime.");
            var results = new List<Measurement>();
            foreach (string verbosity in new[] { "3", GpuLayerSignal.LogVerbosity })
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                string key = Guid.NewGuid().ToString("N");
                var start = new ProcessStartInfo(models.ExecutablePathFor(backend))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = models.RuntimeDirectoryFor(backend),
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.Environment["LLAMA_API_KEY"] = key;
                foreach (string value in new[] { "-m", models.ModelPath, "--device", "none", "-ngl", "0", "-c", "128", "-b", "32", "-ub", "32", "-t", "2",
                    "--parallel", "1", "--no-warmup", "--offline", "--no-webui", "--host", "127.0.0.1", "--port", port.ToString(), "--log-verbosity", verbosity })
                    start.ArgumentList.Add(value);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Native logging worker did not start.");
                var signal = new GpuLayerSignal();
                var reads = Task.WhenAll(signal.ReadAsync(process.StandardOutput), signal.ReadAsync(process.StandardError));
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(1) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
                bool ready = false;
                SafeFileHandle? job = null;
                try
                {
                    if (OperatingSystem.IsWindows()) job = NativeTranslationHost.AttachJob(process);
                    var clock = Stopwatch.StartNew();
                    while (clock.Elapsed < TimeSpan.FromSeconds(30) && !process.HasExited)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        try
                        {
                            using var response = await http.GetAsync($"http://127.0.0.1:{port}/health", deadline.Token);
                            if (response.IsSuccessStatusCode) { ready = true; break; }
                        }
                        catch (HttpRequestException) { }
                        catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                        await Task.Delay(50, deadline.Token);
                    }
                }
                finally
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        await reads.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    finally { job?.Dispose(); }
                }
                if (!ready || !process.HasExited || signal.Layers != 0 || signal.Observed != (verbosity == GpuLayerSignal.LogVerbosity))
                    throw new InvalidOperationException("Pinned native startup did not reproduce old suppressed confirmation and new visible zero-layer signal.");
                results.Add(new(verbosity, ready, signal.Observed, signal.Layers, process.HasExited));
            }
            string output = Path.GetFullPath("artifacts/validation"); Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "gpu-startup-logging.json"), JsonSerializer.Serialize(new { Runtime = models.RuntimeFor(backend).Version, PhysicalGpuInference = false, Results = results }));
            Console.WriteLine("PASS real pinned native startup: verbosity 3 hides layer confirmation; production verbosity 4 exposes explicit 0-layer signal; both workers healthy and reaped. CPU-only fixture, no physical GPU inference claim.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private sealed record Measurement(string Verbosity, bool Healthy, bool SignalObserved, int OffloadedLayers, bool ProcessExited);
}
