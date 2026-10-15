using System.Text.Json;
using Snip.Core;
using Snip.Translation;

internal static class GpuRuntimeChecks
{
    public static async Task<int> RunAsync(string folder, bool requirePhysicalGpu)
    {
        var models = new ModelStore(folder);
        var previous = models.ComputeSettings.Read();
        var metrics = new List<OfflineInferenceMetrics>();
        var stages = new List<string>();
        var translator = new OfflineTranslator(models);
        translator.InferenceCompleted += value => metrics.Add(value);
        var progress = new InlineProgress(value => stages.Add(value.Stage));
        try
        {
            var modified = File.GetLastWriteTimeUtc(models.ModelPath);
            if (!requirePhysicalGpu)
            {
                await models.InstallGpuRuntimeAsync(null, CancellationToken.None);
                if (!await models.IsRuntimeInstalledAsync(TranslationBackend.Vulkan, CancellationToken.None)) throw new InvalidOperationException("GPU runtime checksum verification failed.");
                if (File.GetLastWriteTimeUtc(models.ModelPath) != modified) throw new InvalidOperationException("Installing GPU support rewrote the existing model.");
                string isolatedFolder = Path.Combine(Path.GetTempPath(), "Snip.Gpu.Corruption-" + Guid.NewGuid());
                var isolated = new ModelStore(isolatedFolder);
                try
                {
                    Directory.CreateDirectory(isolated.RuntimeDirectoryFor(TranslationBackend.Vulkan));
                    foreach (var file in models.RuntimeFor(TranslationBackend.Vulkan).Files)
                        File.Copy(Path.Combine(models.RuntimeDirectoryFor(TranslationBackend.Vulkan), file.Path), Path.Combine(isolated.RuntimeDirectoryFor(TranslationBackend.Vulkan), file.Path));
                    byte[] damaged = await File.ReadAllBytesAsync(isolated.ExecutablePathFor(TranslationBackend.Vulkan)); damaged[0] ^= 1;
                    await File.WriteAllBytesAsync(isolated.ExecutablePathFor(TranslationBackend.Vulkan), damaged);
                    if (!isolated.HasRuntimeFiles(TranslationBackend.Vulkan) || await isolated.IsRuntimeInstalledAsync(TranslationBackend.Vulkan, CancellationToken.None) ||
                        await GpuDeviceProbe.DetectAsync(isolated, CancellationToken.None) is not null)
                        throw new InvalidOperationException("Same-size GPU runtime corruption was not rejected before device probing.");
                    Console.WriteLine("PASS same-size GPU executable corruption in isolated directory rejected before launch.");
                }
                finally { Directory.Delete(isolatedFolder, true); }
                Console.WriteLine("PASS downloaded official Vulkan archive; verified all required native files; shared model unchanged.");
            }
            if (!requirePhysicalGpu && await NativeGpuLoggingChecks.RunAsync(folder) != 0)
                throw new InvalidOperationException("Real native GPU startup logging regression failed.");
            var device = await GpuDeviceProbe.DetectAsync(models, CancellationToken.None);
            if (requirePhysicalGpu && device is null)
            {
                Console.WriteLine("UNRUN physical GPU inference: no compatible Vulkan device with at least 1536 MiB free; runtime/CPU fallback has a separate passed check.");
                return 2;
            }
            models.ComputeSettings.Save(TranslationComputeMode.Gpu);
            string translated = await translator.TranslateAsync("Hello world.", "en-zh", progress, CancellationToken.None);
            if (!translated.Contains("世界") || metrics.Count == 0 || metrics.Any(value => !value.ProcessExited)) throw new InvalidOperationException("Preferred GPU or fallback translation failed or left a worker alive.");
            var actual = metrics.Last();
            if (device is null && (actual.Backend != TranslationBackend.Cpu || !stages.Any(stage => stage.Contains("回退 CPU")))) throw new InvalidOperationException("No-device path did not explicitly fall back to CPU.");
            if (requirePhysicalGpu && (actual.Backend != TranslationBackend.Vulkan || actual.OffloadedLayers <= 0)) throw new InvalidOperationException("Physical GPU was present but inference did not actually offload model layers.");
            Console.WriteLine($"PASS preferred GPU translation: actual={actual.Backend}, offloadedLayers={actual.OffloadedLayers}, exited={actual.ProcessExited}.");

            // Forced CPU remains functional even after GPU DLLs have been installed.
            models.ComputeSettings.Save(TranslationComputeMode.Cpu);
            translated = await translator.TranslateAsync("Hello world.", "en-zh", CancellationToken.None);
            if (!translated.Contains("世界") || metrics.Last().Backend != TranslationBackend.Cpu || !metrics.Last().ProcessExited) throw new InvalidOperationException("CPU-only mode loaded GPU or failed to release the worker.");
            Console.WriteLine("PASS forced CPU translation after installing GPU support; worker released.");

            if (requirePhysicalGpu)
            {
                models.ComputeSettings.Save(TranslationComputeMode.Gpu);
                using var cancellation = new CancellationTokenSource(); bool started = false;
                var cancelProgress = new InlineProgress(value => { if (!started && value.Stage == "正在翻译") { started = true; cancellation.CancelAfter(TimeSpan.FromMilliseconds(250)); } });
                bool cancelled = false;
                try { await translator.TranslateAsync(string.Join(" ", Enumerable.Repeat("This longer text verifies cancellation during GPU translation.", 12)), "en-zh", cancelProgress, cancellation.Token); }
                catch (OperationCanceledException) when (started && cancellation.IsCancellationRequested) { cancelled = true; }
                if (!cancelled || !metrics.Last().ProcessExited || metrics.Last().Backend != TranslationBackend.Vulkan) throw new InvalidOperationException("GPU generation cancellation did not stop and release the worker.");
                Console.WriteLine("PASS physical GPU generation cancellation releases the native worker.");
            }
            foreach (var metric in metrics)
            {
                try { using var process = System.Diagnostics.Process.GetProcessById(metric.ProcessId); if (!process.HasExited) throw new InvalidOperationException("Orphan compute worker."); }
                catch (ArgumentException) { }
            }
            string output = Path.GetFullPath("artifacts/validation"); Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, requirePhysicalGpu ? "gpu-inference-metrics.json" : "gpu-runtime-metrics.json"), JsonSerializer.Serialize(metrics));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { models.ComputeSettings.Save(previous); }
    }
    private sealed class InlineProgress(Action<TranslationProgress> action) : IProgress<TranslationProgress>
    { public void Report(TranslationProgress value) => action(value); }
}
