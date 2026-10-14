using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Snip.Translation;

/// <summary>Private loopback CPU worker. Input never appears in command lines, files or logs.
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
    public int ProcessId { get; }
    public long PeakWorkingSetBytes => Interlocked.Read(ref peak);
    public long PeakCombinedWorkingSetBytes => Interlocked.Read(ref combinedPeak);
    public bool Exited { get; private set; }
    public int? ExitCode { get; private set; }
    public NativeTranslationHost(ModelStore models)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(models.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = models.RuntimeDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        string key = Guid.NewGuid().ToString("N");
        start.Environment["LLAMA_API_KEY"] = key;
        foreach (string value in new[] { "-m", models.ModelPath, "-c", "2048", "-t", "2", "-tb", "2", "-ngl", "0", "--no-op-offload",
            "--parallel", "1", "--threads-http", "1", "--cache-ram", "0", "--no-cache-prompt", "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--alias", "self-snip-offline", "--no-webui", "--no-slots", "--log-disable", "--offline", "--no-warmup", "--jinja" }) start.ArgumentList.Add(value);
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException();
            ProcessId = process.Id;
            if (OperatingSystem.IsWindows()) job = AttachJob(process);
        }
        catch
        {
            if (process is not null) { try { process.Kill(entireProcessTree: true); } catch { } process.Dispose(); }
            throw new InvalidOperationException("无法启动本地翻译推理器，请下载 / 修复模型；如安全软件拦截，请核对官方下载来源。");
        }
        drain = Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null), process.StandardError.BaseStream.CopyToAsync(Stream.Null));
        sampler = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
    private void Sample()
    {
        try
        {
            process.Refresh(); parent.Refresh();
            UpdateMaximum(ref peak, process.PeakWorkingSet64);
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
            if (process.HasExited) throw new InvalidOperationException("本地推理器启动失败，请下载 / 修复模型并检查安全软件提示。");
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token); attempt.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                using var response = await http.GetAsync("health", HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && !process.HasExited) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new TimeoutException("本地模型加载超过 30 秒，已停止；请重试或导出诊断包。");
    }
    public async Task<JsonDocument> PostAsync(string path, object body, CancellationToken token)
    {
        if (process.HasExited) throw new InvalidOperationException("本地翻译推理器已停止，请重试。");
        using var registration = token.Register(Kill);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("本地模型拒绝请求，请缩短原文或下载 / 修复模型。");
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
    private static SafeFileHandle AttachJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(job, process.Handle))
        { job.Dispose(); throw new InvalidOperationException("本地推理器无法建立进程回收保护。"); }
        return job;
    }
}
