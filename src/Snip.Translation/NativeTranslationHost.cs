using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Snip.Translation;

/// <summary>Private loopback CPU/GPU worker. Input never appears in command lines, files or logs.
/// A Windows job closes the entire worker tree if the application exits unexpectedly.</summary>
internal sealed class NativeTranslationHost : IAsyncDisposable
{
    private readonly Process process;
    private readonly HttpClient http;
    private readonly SafeFileHandle? job;
    private readonly Task drain;
    private readonly Timer sampler;
    private readonly Process parent = Process.GetCurrentProcess();
    private long peak;
    private long combinedPeak;
    private int offloadedLayers;
    public int OffloadedLayers => Volatile.Read(ref offloadedLayers);
    public TranslationBackend Backend { get; }
    public CpuInferenceProfile CpuProfile { get; }
    private long processorTicks;
    public double ProcessorSeconds => Interlocked.Read(ref processorTicks) / (double)TimeSpan.TicksPerSecond;
    public int ProcessId { get; }
    public long PeakWorkingSetBytes => Interlocked.Read(ref peak);
    public long PeakCombinedWorkingSetBytes => Interlocked.Read(ref combinedPeak);
    public bool Exited { get; private set; }
    public int? ExitCode { get; private set; }
    public NativeTranslationHost(ModelStore models, TranslationBackend backend = TranslationBackend.Cpu, string? device = null, CpuInferenceProfile? cpuProfile = null)
    {
        Backend = backend;
        CpuProfile = cpuProfile ?? (backend == TranslationBackend.Cpu ? CpuInferenceProfile.ForProcessorCount(Environment.ProcessorCount) : CpuInferenceProfile.Legacy);
        if (backend == TranslationBackend.Vulkan && (device is null || !Regex.IsMatch(device, @"^Vulkan\d+$"))) throw new ArgumentException("Invalid GPU device.");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(models.ExecutablePathFor(backend))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = models.RuntimeDirectoryFor(backend),
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        string key = Guid.NewGuid().ToString("N");
        start.Environment["LLAMA_API_KEY"] = key;
        foreach (string value in backend == TranslationBackend.Vulkan
            ? new[] { "--device", device!, "-ngl", "all", "--fit", "off", "-b", "128", "-ub", "128", "--log-verbosity", "3" }
            : new[] { "--device", "none", "-ngl", "0", "--no-op-offload", "--log-disable" }) start.ArgumentList.Add(value);
        if (backend == TranslationBackend.Cpu)
        {
            foreach (string value in new[] { "-b", CpuProfile.BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture), "-ub", CpuProfile.MicroBatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(value);
            start.ArgumentList.Add(CpuProfile.Repack ? "--repack" : "--no-repack");
        }
        foreach (string value in new[] { "-m", models.ModelPath, "-c", "2048", "-t", CpuProfile.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture), "-tb", CpuProfile.BatchThreads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--parallel", "1", "--threads-http", "1", "--cache-ram", "0", "--no-cache-prompt", "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--alias", "self-snip-offline", "--no-webui", "--no-slots", "--offline", "--no-warmup", "--jinja" }) start.ArgumentList.Add(value);
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException();
            ProcessId = process.Id;
            if (OperatingSystem.IsWindows()) job = AttachJob(process);
            if (backend == TranslationBackend.Cpu && OperatingSystem.IsWindows())
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        catch
        {
            if (process is not null) { try { process.Kill(entireProcessTree: true); } catch { } process.Dispose(); }
            parent.Dispose();
            throw new NativeWorkerException("无法启动本地翻译推理器，请下载 / 修复模型；如安全软件拦截，请核对官方下载来源。");
        }
        drain = Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null), backend == TranslationBackend.Vulkan ? ReadGpuLayerSignalAsync(process.StandardError) : process.StandardError.BaseStream.CopyToAsync(Stream.Null));
        sampler = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
    private async Task ReadGpuLayerSignalAsync(StreamReader reader)
    {
        // Never retain raw model/driver output. Only the numeric startup offload signal survives.
        var line = new StringBuilder(512); var buffer = new char[1024]; bool overflow = false;
        for (int count; (count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0;)
            for (int i = 0; i < count; i++)
            {
                char value = buffer[i];
                if (value == '\n')
                {
                    if (!overflow)
                    {
                        var match = Regex.Match(line.ToString(), @"offloaded (\d+)/\d+ layers to GPU");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int layers)) Interlocked.Exchange(ref offloadedLayers, layers);
                    }
                    line.Clear(); overflow = false;
                }
                else if (line.Length < 512 && !overflow) line.Append(value);
                else overflow = true;
            }
    }
    private void Sample()
    {
        try
        {
            process.Refresh(); parent.Refresh();
            UpdateMaximum(ref peak, process.PeakWorkingSet64);
            UpdateMaximum(ref processorTicks, process.TotalProcessorTime.Ticks);
            UpdateMaximum(ref combinedPeak, process.WorkingSet64 + parent.WorkingSet64);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private static void UpdateMaximum(ref long target, long value)
    {
        long previous;
        do { previous = Interlocked.Read(ref target); if (value <= previous) return; }
        while (Interlocked.CompareExchange(ref target, value, previous) != previous);
    }
    public async Task WaitReadyAsync(CancellationToken token)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new NativeWorkerException("本地推理器启动失败，请下载 / 修复模型，或更新微软 Visual C++ x64 运行库并核对安全软件提示。");
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token); attempt.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                using var response = await http.GetAsync("health", HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && !process.HasExited)
                {
                    if (Backend == TranslationBackend.Vulkan)
                    {
                        for (int i = 0; i < 10 && OffloadedLayers == 0; i++) await Task.Delay(50, token).ConfigureAwait(false);
                        if (OffloadedLayers == 0) throw new NativeWorkerException("GPU 未实际加载模型层，已准备回退 CPU。");
                    }
                    return;
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new NativeWorkerException("本地模型加载超过 30 秒，已停止；请重试或导出诊断包。");
    }
    public async Task<JsonDocument> PostAsync(string path, object body, CancellationToken token)
    {
        if (process.HasExited) throw new NativeWorkerException("本地翻译推理器已停止，请重试。");
        using var registration = token.Register(Kill);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new NativeWorkerException("本地模型拒绝请求，请缩短原文或下载 / 修复模型。");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var block = new byte[8192];
        for (int count; (count = await input.ReadAsync(block, token).ConfigureAwait(false)) > 0;)
        {
            if (buffer.Length + count > 1024 * 1024) throw new InvalidOperationException("本地译文超出大小限制，请缩短原文。");
            buffer.Write(block, 0, count);
        }
        Sample(); return JsonDocument.Parse(buffer.ToArray());
    }
    private void Kill()
    { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
    public async ValueTask DisposeAsync()
    {
        await sampler.DisposeAsync().ConfigureAwait(false); Sample(); Kill(); job?.Dispose();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); Exited = true; ExitCode = process.ExitCode; } catch (TimeoutException) { }
        try { await drain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException) { }
        http.Dispose(); process.Dispose(); parent.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long PerProcess, PerJob; public uint Flags; public UIntPtr Minimum, Maximum; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    internal static SafeFileHandle AttachJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(job, process.Handle))
        { job.Dispose(); throw new InvalidOperationException("本地推理器无法建立进程回收保护。"); }
        return job;
    }
}
