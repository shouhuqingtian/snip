using System.Diagnostics;
using System.Text.Json;
using Snip.Translation;

internal static class TranslationPerformanceChecks
{
    public static async Task<int> RunAsync(string folder)
    {
        var models = new ModelStore(folder);
        var results = new List<Measurement>();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            if (!await models.IsInstalledAsync("en-zh", deadline.Token)) throw new InvalidOperationException("Performance fixture requires verified model/runtime.");
            var optimized = CpuInferenceProfile.ForProcessorCount(Environment.ProcessorCount);
            // Alternate old/new in the same run: all requests use cold worker processes,
            // fixed public input, the same model, and the same token/quality limits.
            for (int round = 0; round < 3; round++)
                foreach (bool legacy in round % 2 == 0 ? new[] { true, false } : new[] { false, true })
                {
                    var profile = legacy ? CpuInferenceProfile.Legacy : optimized;
                    var clock = Stopwatch.StartNew();
                    var host = new NativeTranslationHost(models, cpuProfile: profile);
                    Measurement? measurement = null;
                    try
                    {
                        await host.WaitReadyAsync(deadline.Token);
                        var loaded = clock.Elapsed.TotalSeconds;
                        const string prompt = "Translate the following English text into Simplified Chinese. This is ordinary prose, not code. Translate greetings and common words; preserve only explicit URLs and code identifiers. Output only the translation:\n\nThe invoice must be paid within 30 days. Early payment qualifies for a 2% discount.";
                        using var answer = await host.PostAsync("v1/chat/completions", new
                        {
                            model = "self-snip-offline", messages = new[] { new { role = "user", content = prompt } },
                            temperature = 0, max_tokens = 768, stream = false, cache_prompt = false
                        }, deadline.Token);
                        string text = answer.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
                        if (!text.Contains("30") || !text.Contains("2%") || answer.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString() != "stop")
                            throw new InvalidOperationException("Performance tuning lost fixture meaning or truncated output.");
                        measurement = new Measurement(legacy ? "legacy" : "optimized", round, profile.Threads, profile.BatchThreads, loaded,
                            clock.Elapsed.TotalSeconds, host.PeakWorkingSetBytes, host.ProcessorSeconds);
                    }
                    finally { await host.DisposeAsync(); }
                    if (!host.Exited) throw new InvalidOperationException("Performance worker remained resident.");
                    measurement = measurement! with { PeakBytes = host.PeakWorkingSetBytes, ProcessorSeconds = host.ProcessorSeconds };
                    results.Add(measurement);
                    Console.WriteLine($"MEASURE {measurement.Profile}: load={measurement.LoadSeconds:F2}s, total={measurement.TotalSeconds:F2}s, peak={measurement.PeakBytes / 1048576.0:F1} MiB, CPU={measurement.ProcessorSeconds:F2}s; worker exited.");
                }
            double Median(IEnumerable<double> values) => values.Order().ElementAt(1);
            double oldMemory = Median(results.Where(value => value.Profile == "legacy").Select(value => (double)value.PeakBytes));
            double newMemory = Median(results.Where(value => value.Profile == "optimized").Select(value => (double)value.PeakBytes));
            double oldTime = Median(results.Where(value => value.Profile == "legacy").Select(value => value.TotalSeconds));
            double newTime = Median(results.Where(value => value.Profile == "optimized").Select(value => value.TotalSeconds));
            if (newMemory >= oldMemory * .9) throw new InvalidOperationException("CPU optimization did not reduce median peak native memory by at least 10%.");
            if (newTime > oldTime * 1.3) throw new InvalidOperationException("CPU optimization caused a substantial same-run timing regression.");
            Console.WriteLine($"PASS same-run CPU comparison: median native peak {oldMemory / 1048576:F1} → {newMemory / 1048576:F1} MiB; total {oldTime:F2} → {newTime:F2}s. No user-machine speed guarantee.");
            string output = Path.GetFullPath("artifacts/validation"); Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "translation-performance.json"), JsonSerializer.Serialize(results));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private sealed record Measurement(string Profile, int Round, int Threads, int BatchThreads, double LoadSeconds, double TotalSeconds, long PeakBytes, double ProcessorSeconds);
}
