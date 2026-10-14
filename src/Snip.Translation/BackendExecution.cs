using System.Text.Json;

namespace Snip.Translation;

internal static class BackendExecution
{
    // The GPU attempt must release its worker before its task completes or fails.
    // Caller cancellation never starts a second inference attempt.
    internal static async Task<string> TryGpuThenCpuAsync(Func<TranslationBackend, Task<string>> infer, Action fallback, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await infer(TranslationBackend.Vulkan).ConfigureAwait(false); }
        catch (Exception error) when (!token.IsCancellationRequested && error is NativeWorkerException or HttpRequestException or IOException or JsonException or KeyNotFoundException)
        { fallback(); }
        token.ThrowIfCancellationRequested();
        return await infer(TranslationBackend.Cpu).ConfigureAwait(false);
    }
}
