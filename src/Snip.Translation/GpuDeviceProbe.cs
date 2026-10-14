using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Snip.Translation;

public sealed record GpuDevice(string Id, long FreeMemoryMiB);

internal sealed class NativeWorkerException(string message) : InvalidOperationException(message);

/// <summary>Queries the verified Vulkan executable without opening a model. Raw driver output is discarded.</summary>
public static class GpuDeviceProbe
{
    public static GpuDevice? Parse(string output)
    {
        // Keep only a machine device ID and free memory; never put vendor output into runtime logs.
        return Regex.Matches(output, @"^\s*(Vulkan\d+):[^\r\n]*\(\d+ MiB, (\d+) MiB free\)", RegexOptions.Multiline)
            .Select(match => long.TryParse(match.Groups[2].Value, out long free) ? new GpuDevice(match.Groups[1].Value, free) : null)
            .Where(device => device is not null && device.FreeMemoryMiB >= 1536)
            .OrderByDescending(device => device!.FreeMemoryMiB).FirstOrDefault();
    }
    public static async Task<GpuDevice?> DetectAsync(ModelStore models, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !models.HasRuntimeFiles(TranslationBackend.Vulkan)) return null;
        if (!await models.IsRuntimeInstalledAsync(TranslationBackend.Vulkan, token).ConfigureAwait(false)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = new Process { StartInfo = new(models.ExecutablePathFor(TranslationBackend.Vulkan))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = models.RuntimeDirectoryFor(TranslationBackend.Vulkan),
            RedirectStandardOutput = true, RedirectStandardError = true
        }};
        process.StartInfo.ArgumentList.Add("--list-devices");
        Task<string>? output = null; Task? errors = null;
        try
        {
            if (!process.Start()) return null;
            using var job = NativeTranslationHost.AttachJob(process);
            output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            string devices = await output.ConfigureAwait(false);
            return process.ExitCode == 0 ? Parse(devices) : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception error) when (error is InvalidOperationException or TimeoutException) { }
            deadline.Cancel();
            try { await Task.WhenAll(output ?? Task.FromResult(""), errors ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or TimeoutException) { }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[1024];
        for (int count; (count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0;)
        {
            if (result.Length + count > 65536) throw new InvalidOperationException("GPU 设备检测输出过大。");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
