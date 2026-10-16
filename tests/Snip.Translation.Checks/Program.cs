using Snip.Translation;

if (args.Length == 1 && args[0] == "--dictionary") return await BasicDictionaryChecks.RunAsync();
if (args.Length == 2 && args[1] == "--gpu-startup-logging") return await NativeGpuLoggingChecks.RunAsync(args[0]);

if (args.Length == 1 && args[0] == "--online")
{
    int compute = await ComputeBackendChecks.RunAsync();
    if(compute!=0)return compute;
    int basic=await BasicDictionaryChecks.RunAsync();
    if (basic != 0) return basic;
    int translation = await OnlineTranslationChecks.RunAsync();
    if (translation != 0) return translation;
    int ocr = await OnlineOcrChecks.RunAsync();
    return ocr == 0 ? await OnlineSpeechChecks.RunAsync() : ocr;
}
if (args.Length == 2 && args[1] == "--performance") return await TranslationPerformanceChecks.RunAsync(args[0]);
if (args.Length == 2 && args[1] is "--gpu-runtime" or "--gpu-inference") return await GpuRuntimeChecks.RunAsync(args[0], args[1] == "--gpu-inference");
if (args.Length < 1 || args.Length > 2) { Console.Error.WriteLine("Pass a model directory and optional --install to download missing pinned files."); return 2; }
var store = new ModelStore(args[0]); var translator = new OfflineTranslator(store);
var metrics = new List<OfflineInferenceMetrics>();
translator.InferenceCompleted += value =>
{
    metrics.Add(value);
    Console.WriteLine($"METRIC Hy-MT2: native {value.PeakWorkingSetBytes / 1048576.0:F1} MiB; parent+worker {value.PeakCombinedWorkingSetBytes / 1048576.0:F1} MiB; {value.ElapsedSeconds:F2}s; exited={value.ProcessExited}");
    if (!value.ProcessExited) throw new InvalidOperationException("Native worker remained alive.");
};
if (args.Length == 2 && args[1] == "--install") await store.InstallAsync(null, CancellationToken.None);
foreach (var (direction, input) in new[] { ("en-zh", "Hello world."), ("zh-en", "你好，世界。") })
{
    if (!await store.IsInstalledAsync(direction, CancellationToken.None)) throw new InvalidOperationException("Model verification failed: " + direction);
    var result = await translator.TranslateAsync(input, direction, CancellationToken.None);
    Console.WriteLine($"{direction}: {input} => {result}");
    if (string.IsNullOrWhiteSpace(result) || result == input) throw new InvalidOperationException("Translation did not execute correctly.");
    if (direction == "en-zh" && !(result.Contains("世界") && (result.Contains("你好") || result.Contains("您好")))) throw new InvalidOperationException("Expected greeting and world meaning missing from Chinese output.");
    if (direction == "zh-en" && !(result.Contains("hello", StringComparison.OrdinalIgnoreCase) && result.Contains("world", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Expected greeting and world meaning missing from English output.");
}
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { await translator.TranslateAsync("test", "en-zh", cancellation.Token); throw new Exception("Cancellation was ignored."); }
    catch (OperationCanceledException) { Console.WriteLine("Cancellation check passed."); }
}
using (var cancellation = new CancellationTokenSource())
{
    bool started = false;
    var progress = new InlineProgress(p =>
    {
        if (!started && p.Stage == "正在翻译") { started = true; cancellation.CancelAfter(TimeSpan.FromMilliseconds(250)); }
    });
    try
    {
        await translator.TranslateAsync(string.Join(" ", Enumerable.Repeat("This is a longer sentence used to verify cancellation during local inference.", 12)), "en-zh", progress, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30));
        throw new Exception("Active translation cancellation was ignored.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested && started) { Console.WriteLine("Active inference cancellation check passed."); }
}
// Same-size executable corruption must be rejected, not just a missing-model check.
string temporary = Path.Combine(Path.GetTempPath(), "Snip.Model.Corruption-" + Guid.NewGuid());
var isolated = new ModelStore(temporary);
try
{
    Directory.CreateDirectory(isolated.Folder("en-zh")); Directory.CreateDirectory(isolated.RuntimeDirectory);
    File.Copy(store.ModelPath, isolated.ModelPath);
    foreach (var file in store.Runtime.Files) File.Copy(Path.Combine(store.RuntimeDirectory, file.Path), Path.Combine(isolated.RuntimeDirectory, file.Path));
    byte[] corrupted = await File.ReadAllBytesAsync(isolated.ExecutablePath); corrupted[0] ^= 1;
    await File.WriteAllBytesAsync(isolated.ExecutablePath, corrupted);
    if (await isolated.IsInstalledAsync("en-zh", CancellationToken.None)) throw new Exception("Corrupted native runtime was accepted.");
    bool rejected = false;
    try { await new OfflineTranslator(isolated).TranslateAsync("Public test", "en-zh", CancellationToken.None); }
    catch (InvalidOperationException) { rejected = true; }
    if (!rejected) throw new Exception("Translation started an unverified executable.");
    Console.WriteLine("Same-size native runtime corruption rejected before execution.");
}
finally { Directory.Delete(temporary, recursive: true); }
foreach (var (direction, input, expected) in new[]
{
    ("en-zh", "Do not save the screenshot unless the user confirms.", "确认"),
    ("en-zh", "The invoice must be paid within 30 days. Early payment qualifies for a 2% discount.", "2%"),
    ("en-zh", "Snip.App is the application name. Keep Snip.App unchanged.", "Snip.App"),
    ("zh-en", "请关闭当前窗口，但不要退出程序。", "not")
})
{
    var result = await translator.TranslateAsync(input, direction, CancellationToken.None);
    if (!result.Contains(expected, StringComparison.OrdinalIgnoreCase)) throw new Exception("Translation lost a key meaning: " + result);
    if (input.StartsWith("Do not") && !result.Contains("不") && !result.Contains("除非")) throw new Exception("Negation lost in translation.");
    if (input.StartsWith("The invoice") && !result.Contains("30")) throw new Exception("Payment period lost in translation.");
    Console.WriteLine($"PASS Hy-MT2 semantic example {direction}: {result}");
}
var multiline = await translator.TranslateAsync("Hello world.\n\nHello world.", "en-zh", CancellationToken.None);
if (multiline.Split(Environment.NewLine).Length != 3 || !multiline.Contains("世界")) throw new Exception("Paragraph spacing was lost.");
Console.WriteLine("PASS blank paragraph preservation.");
if (metrics.Count < 8 || metrics.Any(m => !m.ProcessExited || m.PeakCombinedWorkingSetBytes <= 0 || m.PeakCombinedWorkingSetBytes > 2L * 1024 * 1024 * 1024))
    throw new Exception("Native worker cleanup or 2 GiB combined working set budget failed.");
foreach (var metric in metrics)
{
    try { using var process = System.Diagnostics.Process.GetProcessById(metric.ProcessId); if (!process.HasExited) throw new Exception("Orphan inference process."); }
    catch (ArgumentException) { }
}
Console.WriteLine("PASS every native worker exited, including active cancellation; combined parent+worker samples stayed below 2 GiB.");
Console.WriteLine("Hy-MT2 bilingual, meaning, paragraphs, corruption and cancellation integration checks passed.");
return 0;

sealed class InlineProgress(Action<Snip.Core.TranslationProgress> action) : IProgress<Snip.Core.TranslationProgress>
{
    public void Report(Snip.Core.TranslationProgress value) => action(value);
}
