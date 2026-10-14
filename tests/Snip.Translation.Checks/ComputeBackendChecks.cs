using Snip.Translation;

internal static class ComputeBackendChecks
{
    public static async Task<int> RunAsync()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Snip.Compute-" + Guid.NewGuid());
        int count = 0;
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); count++; }
        try
        {
            var store = new ModelStore(folder);
            Require(store.ComputeSettings.Read() == TranslationComputeMode.Auto && !Directory.Exists(folder), "Fresh preference load created files or did not default to automatic.");
            foreach (var mode in Enum.GetValues<TranslationComputeMode>())
            {
                store.ComputeSettings.Save(mode);
                Require(new ModelStore(folder).ComputeSettings.Read() == mode, "Restart lost the selected compute mode.");
            }
            File.WriteAllText(Path.Combine(folder, "compute-settings.json"), "999");
            bool rejected = false;
            try { store.ComputeSettings.Read(); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Unknown compute mode accepted.");
            store.ComputeSettings.Save(TranslationComputeMode.Cpu);
            Require(store.ComputeSettings.Read() == TranslationComputeMode.Cpu && !Directory.EnumerateFiles(folder, "*.tmp").Any(), "Invalid configuration could not be repaired atomically.");
            string fixture = "Available devices:\n  Vulkan0: PUBLIC Intel adapter (4096 MiB, 1024 MiB free)\n  Vulkan1: PUBLIC AMD adapter (8192 MiB, 6144 MiB free)\n  Vulkan2: PUBLIC NVIDIA adapter (8192 MiB, 4096 MiB free)\n";
            Require(GpuDeviceProbe.Parse(fixture) is { Id: "Vulkan1", FreeMemoryMiB: 6144 }, "Did not choose a Vulkan device with sufficient free memory.");
            Require(GpuDeviceProbe.Parse("Available devices:\n  CUDA0: PUBLIC GPU (8192 MiB, 6000 MiB free)\n") is null, "Accepted a device ID incompatible with this runtime.");
            Require(GpuDeviceProbe.Parse("Available devices:\n  Vulkan0: PUBLIC GPU (1024 MiB, 512 MiB free)\n") is null, "Insufficient GPU memory accepted.");
            Require(await GpuDeviceProbe.DetectAsync(store, CancellationToken.None) is null && !File.Exists(store.ModelPath), "Fresh GPU detection downloaded a model or found an uninstalled runtime.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            rejected = false;
            try { await GpuDeviceProbe.DetectAsync(store, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            Require(rejected, "GPU detection ignored cancellation.");
            var cpu = ModelStore.Model.Runtimes.Single(runtime => runtime.Platform == "win-x64" && runtime.Backend == "cpu");
            var gpu = ModelStore.Model.Runtimes.Single(runtime => runtime.Platform == "win-x64" && runtime.Backend == "vulkan");
            Require(cpu.Archive.Sha256 != gpu.Archive.Sha256 && gpu.Files.Any(file => file.Path == "ggml-vulkan.dll") && !cpu.Files.Any(file => file.Path == "ggml-vulkan.dll"), "CPU/GPU manifests or checksums were confused.");
            Require(ModelStore.Model.Weight.Size == 951022560 && gpu.Archive.Size < 33_000_000, "GPU support replaced the shared model or exceeded its runtime download budget.");
            var calls = new List<TranslationBackend>(); bool released = false, reported = false;
            string result = await BackendExecution.TryGpuThenCpuAsync(backend =>
            {
                calls.Add(backend);
                if (backend == TranslationBackend.Vulkan) { released = true; throw new NativeWorkerException("Public fixture: GPU startup failed."); }
                if (!released) throw new InvalidOperationException("CPU started before GPU cleanup.");
                return Task.FromResult("PUBLIC CPU result");
            }, () => reported = true, CancellationToken.None);
            Require(result == "PUBLIC CPU result" && reported && calls.SequenceEqual(new[] { TranslationBackend.Vulkan, TranslationBackend.Cpu }), "GPU startup failure did not release and fall back exactly once.");
            calls.Clear();
            using (var activeCancellation = new CancellationTokenSource())
            {
                rejected = false;
                try
                {
                    await BackendExecution.TryGpuThenCpuAsync(backend =>
                    {
                        calls.Add(backend); activeCancellation.Cancel(); throw new NativeWorkerException("Public fixture: cancellation races GPU exit.");
                    }, () => throw new InvalidOperationException("Cancellation must not run fallback."), activeCancellation.Token);
                }
                catch (NativeWorkerException) when (activeCancellation.IsCancellationRequested) { rejected = true; }
                Require(rejected && calls.SequenceEqual(new[] { TranslationBackend.Vulkan }), "Cancellation started a CPU retry.");
            }
            calls.Clear(); rejected = false;
            try
            {
                await BackendExecution.TryGpuThenCpuAsync(backend => { calls.Add(backend); throw new InvalidOperationException("Public fixture: input exceeds model context."); },
                    () => throw new InvalidOperationException("Invalid input must not be retried."), CancellationToken.None);
            }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && calls.SequenceEqual(new[] { TranslationBackend.Vulkan }), "Input error started a CPU retry.");
            foreach (int cores in new[] { 1, 2, 4, 8, 16, 64 })
            {
                var profile = CpuInferenceProfile.ForProcessorCount(cores);
                Require(profile.Threads >= 1 && profile.Threads <= Math.Min(4, Math.Max(1, cores / 2)) && profile.BatchThreads <= Math.Min(4, Math.Max(1, cores - 1)) &&
                    !profile.Repack && profile.MicroBatchSize == 128 && profile.BatchSize >= profile.MicroBatchSize, "CPU profile oversubscribed cores or retained extra weight storage.");
            }
            Console.WriteLine($"PASS {count} compute settings, device selection, cancellation and pinned-runtime checks; no network or physical GPU used.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
