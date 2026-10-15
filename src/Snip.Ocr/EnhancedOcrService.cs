using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Snip.Core;

namespace Snip.Ocr;

public sealed record EnhancedOcrMetrics(int ProcessId, bool ProcessExited, long PeakWorkingSetBytes, double ElapsedSeconds);

/// <summary>Optional bilingual engine with a bounded, isolated lifetime and native-system fallback.</summary>
public sealed class EnhancedOcrService : IOcrService, IOcrEnhancement
{
    private readonly IOcrService system;
    private readonly OcrModelStore models;
    private readonly Func<ProcessStartInfo> workerHost;
    private readonly DiagnosticJournal? diagnostics;
    private readonly SemaphoreSlim recognitionGate = new(1);
    private bool enabled;
    private string description = "系统 OCR（免下载）；可在更多设置下载增强识别";
    public event Action<EnhancedOcrMetrics>? WorkerReleased;
    public event Action<int>? WorkerStarted;
    public EnhancedOcrService(IOcrService system, OcrModelStore models, Func<ProcessStartInfo> workerHost, DiagnosticJournal? diagnostics = null)
    { this.system = system; this.models = models; this.workerHost = workerHost; this.diagnostics = diagnostics; enabled = models.ReadEnabled(); }
    public bool Enabled { get => enabled; set { models.SaveEnabled(value); enabled = value; } }
    public bool IsInstalled => models.HasFiles;
    public string EngineDescription => description;
    public IReadOnlyList<string> Languages => enabled && IsInstalled ? ["zh-Hans-CN", "en-US"] : system.Languages;
    public async Task InstallAsync(IProgress<OcrDownloadProgress>? progress, CancellationToken token)
    { await models.InstallAsync(progress, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); Enabled = true; }
    public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
    public async Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token)
    {
        if (!enabled || !IsInstalled)
        {
            description = "系统 OCR（免下载）";
            return await SystemRecognizeAsync(image, language, token).ConfigureAwait(false);
        }
        await recognitionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            try
            {
                var result = await RunWorkerAsync(image, token).ConfigureAwait(false);
                description = "增强离线 OCR · PP-OCRv6 small（中英自动，识别进程已退出）";
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                diagnostics?.Record("ocr", "enhanced-fallback", error: error);
                token.ThrowIfCancellationRequested();
                description = error is WorkerFailure { ExitCode: 7 }
                    ? "增强识别运行库不可用，已回退系统 OCR；请安装微软 Visual C++ x64 运行库"
                    : "增强识别未完成，已回退系统 OCR；可在更多设置修复模型";
                return await SystemRecognizeAsync(image, language, token).ConfigureAwait(false);
            }
        }
        finally { recognitionGate.Release(); }
    }
    private Task<OcrResult> SystemRecognizeAsync(ImagePayload image, string? language, CancellationToken token)
    {
        var installed = system.Languages;
        string? candidate = language is not null && installed.Contains(language) ? language
            : OcrLanguageSelection.Candidates(installed, OcrLanguageSelection.Automatic).FirstOrDefault();
        return system.RecognizeAsync(image, candidate, token);
    }
    private async Task<OcrResult> RunWorkerAsync(ImagePayload image, CancellationToken token)
    {
        if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > OcrWorker.MaxPixels || image.PngBytes.Length > OcrWorker.MaxPngBytes)
            throw new InvalidOperationException("截图过大，请缩小文字识别区域。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var ct = timeout.Token;
        using var log = diagnostics?.Begin("enhanced-ocr", new { Engine = "PP-OCRv6-small", image.Width, image.Height });
        var info = workerHost(); info.UseShellExecute = false; info.CreateNoWindow = true;
        info.WorkingDirectory = models.DirectoryPath;
        info.RedirectStandardInput = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        info.ArgumentList.Add("--ocr-worker"); info.ArgumentList.Add(models.DirectoryPath);
        using var process = new Process { StartInfo = info };
        bool started = false;
        long peakBytes = 0;
        Task<byte[]>? outputRead = null;
        Task? errorDrain = null;
        var clock = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("无法启动增强识别。");
            started = true;
            WorkerStarted?.Invoke(process.Id);
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
            // Content travels only through inherited pipes, never temporary image files or logs.
            outputRead = ReadBoundedAsync(process.StandardOutput.BaseStream, 8 * 1024 * 1024, ct);
            errorDrain = DrainAsync(process.StandardError.BaseStream, ct);
            byte[] header = new byte[12]; BinaryPrimitives.WriteInt32LittleEndian(header, image.Width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), image.Height); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), image.PngBytes.Length);
            await process.StandardInput.BaseStream.WriteAsync(header, ct).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(image.PngBytes, ct).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            byte[] json = await outputRead.ConfigureAwait(false); await errorDrain.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new WorkerFailure(process.ExitCode);
            var reply = JsonSerializer.Deserialize<OcrWorkerReply>(json) ?? throw new InvalidDataException("增强识别结果为空。");
            var result = reply.Result; peakBytes = reply.PeakWorkingSetBytes;
            if (result.Regions.Count > 100_000 || result.Regions.Any(r => r.Bounds.IsEmpty || !double.IsFinite(r.Bounds.X) || !double.IsFinite(r.Bounds.Y) || r.Bounds.Intersect(new(0, 0, image.Width, image.Height)) != r.Bounds))
                throw new InvalidDataException("增强识别结果坐标无效。");
            log?.Complete(new { RegionCount = result.Regions.Count, ElapsedMilliseconds = clock.ElapsedMilliseconds, ProcessExited = process.HasExited });
            return result;
        }
        catch (OperationCanceledException) { log?.Cancel("cancelled-or-timeout"); throw; }
        catch (Exception error) { log?.Fail(error); throw; }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                var metrics = new EnhancedOcrMetrics(process.Id, process.HasExited, peakBytes, clock.Elapsed.TotalSeconds);
                diagnostics?.Record("ocr", "worker-released", metrics);
                WorkerReleased?.Invoke(metrics);
            }
            if (outputRead is not null) { try { await outputRead.ConfigureAwait(false); } catch { } }
            if (errorDrain is not null) { try { await errorDrain.ConfigureAwait(false); } catch { } }
        }
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream(); byte[] bytes = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) != 0)
        { if (output.Length + count > maximum) throw new InvalidDataException("增强识别结果过大。"); await output.WriteAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false); }
        return output.ToArray();
    }
    private static async Task DrainAsync(Stream stream, CancellationToken token)
    { byte[] bytes = new byte[4096]; while (await stream.ReadAsync(bytes, token).ConfigureAwait(false) != 0) { } }
    private sealed class WorkerFailure(int code) : InvalidOperationException("增强识别进程未完成，可在更多设置修复或切回系统 OCR。")
    { public int ExitCode { get; } = code; }
}
