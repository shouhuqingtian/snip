using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Snip.Core;
using Snip.Infrastructure;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;

namespace Snip.Ocr.Evaluation;

internal static class Program
{
    // Separate executable: no changes to the app or its dependency graph.
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is not 2 and not 4) return 1;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(await File.ReadAllTextAsync(args[0]), options)!;
        if (args.Length == 4) return await Enhanced(fixtures, args[1], args[2], args[3], options);
        var service = new OfflineOcrService();
        var languages = await Task.Run(() => service.Languages);
        var english = languages.FirstOrDefault(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        var chinese = languages.FirstOrDefault(l => l.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        var rows = new List<object>();
        foreach (var fixture in fixtures)
        {
            bool containsChinese = fixture.Expected.Any(c => c is >= '\u3400' and <= '\u9fff');
            var language = containsChinese ? chinese : english;
            if (language is null)
            {
                rows.Add(new { fixture.Name, Outcome = "Unrun", Reason = containsChinese ? "Chinese component unavailable" : "English component unavailable" });
                continue;
            }
            var payload = new ImagePayload(await File.ReadAllBytesAsync(fixture.Path), fixture.Width, fixture.Height);
            var times = new List<double>();
            string actual = "";
            for (int round = 0; round < 3; round++)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var clock = Stopwatch.StartNew();
                var result = await service.RecognizeAsync(payload, language, deadline.Token).WaitAsync(deadline.Token);
                times.Add(clock.Elapsed.TotalMilliseconds);
                actual = string.Join("\n", result.Regions.Select(region => region.Text));
            }
            var expected = Normalize(fixture.Expected);
            var normalized = Normalize(actual);
            rows.Add(new { fixture.Name, Outcome = "Measured", Language = language, fixture.Expected, Actual = actual,
                Exact = expected == normalized, Errors = Distance(expected, normalized), Characters = expected.Length,
                FirstMs = times[0], WarmMeanMs = times.Skip(1).Average() });
        }
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var report = new { Engine = "Windows OCR, matching installed language", Languages = languages,
            PeakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0, Rows = rows };
        await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(report, options));
        Console.WriteLine($"Native OCR comparison completed: {fixtures.Length} fixtures; English={english is not null}; Chinese={chinese is not null}.");
        return 0;
    }

    private static async Task<int> Enhanced(Fixture[] fixtures, string output, string cache, string candidate, JsonSerializerOptions json)
    {
        bool bounded = candidate.EndsWith("-bounded", StringComparison.Ordinal);
        string modelCandidate = bounded ? candidate[..^8] : candidate;
        var rows = new List<object>();
        using var engine = new RapidOcr();
        using var sessionOptions = RapidOcr.GetDefaultSessionOptions(2);
        sessionOptions.InterOpNumThreads = 1;
        sessionOptions.EnableCpuMemArena = false;
        var preset = modelCandidate.EndsWith("tiny", StringComparison.Ordinal) ? RapidOcrModelSet.PPOCRv6Tiny : RapidOcrModelSet.PPOCRv6Small;
        var model = preset with { DetModelPath = Path.GetFullPath(Path.Combine(cache, modelCandidate + "-det.onnx")),
            RecModelPath = Path.GetFullPath(Path.Combine(cache, modelCandidate + "-rec.onnx")),
            KeysPath = Path.GetFullPath(Path.Combine(cache, modelCandidate + "-rec.txt")),
            ClsModelPath = Path.Combine(AppContext.BaseDirectory, "models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx") };
        var clock = Stopwatch.StartNew();
        engine.InitModels(model, sessionOptions);
        double loadMs = clock.Elapsed.TotalMilliseconds;
        var settings = RapidOcrOptions.PPOCRv6 with { DoAngle = false };
        foreach (var fixture in fixtures)
        {
            var inferenceSettings = settings;
            if (bounded)
            {
                double shortSide = Math.Min(fixture.Width, fixture.Height), longSide = Math.Max(fixture.Width, fixture.Height);
                inferenceSettings = settings with { LimitSideLen = Math.Clamp((int)Math.Round(Math.Min(Math.Min(736, shortSide * 2), shortSide * 1600 / longSide)), 32, 736) };
            }
            var times = new List<double>(); string actual = "";
            for (int round = 0; round < 3; round++)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                clock.Restart();
                var result = engine.Detect(fixture.Path, inferenceSettings, deadline.Token);
                times.Add(clock.Elapsed.TotalMilliseconds);
                actual = result.StrRes;
            }
            var expected = Normalize(fixture.Expected); var normalized = Normalize(actual);
            using var process = Process.GetCurrentProcess(); process.Refresh();
            rows.Add(new { fixture.Name, Outcome = "Measured", fixture.Expected, Actual = actual,
                Exact = expected == normalized, Errors = Distance(expected, normalized), Characters = expected.Length,
                FirstMs = times[0], WarmMeanMs = times.Skip(1).Average(), WorkingSetMiB = process.WorkingSet64 / 1048576.0 });
        }
        using var current = Process.GetCurrentProcess(); current.Refresh();
        var report = new { Engine = "RapidOcrNet 4.2.0 / " + candidate, LoadMs = loadMs,
            PeakWorkingSetMiB = current.PeakWorkingSet64 / 1048576.0, Rows = rows };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, json));
        Console.WriteLine($".NET OCR measured: {candidate}; {fixtures.Length} fixtures; peak {current.PeakWorkingSet64 / 1048576.0:F1} MiB.");
        return 0;
    }

    private static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            var next = new int[b.Length + 1]; next[0] = i;
            for (int j = 1; j <= b.Length; j++) next[j] = Math.Min(Math.Min(row[j] + 1, next[j - 1] + 1), row[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            row = next;
        }
        return row[b.Length];
    }
    private sealed record Fixture(string Name, string Expected, string Path, int Width, int Height);
}
