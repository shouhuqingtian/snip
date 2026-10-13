using Snip.Translation;

if (args.Length < 1 || args.Length > 2) { Console.Error.WriteLine("Pass a model directory and optional --install to download missing pinned files."); return 2; }
var store = new ModelStore(args[0]); var translator = new OfflineTranslator(store);
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
var temporary = Path.Combine(Path.GetTempPath(), "Snip.Model.Check-" + Guid.NewGuid());
using (var cancellation = new CancellationTokenSource())
{
    bool started = false;
    var progress = new InlineProgress(p =>
    {
        if (!started && p.Stage == "正在翻译") { started = true; cancellation.CancelAfter(TimeSpan.FromMilliseconds(10)); }
    });
    try
    {
        await translator.TranslateAsync(string.Join(" ", Enumerable.Repeat("This is a longer sentence used to verify cancellation during local inference.", 12)), "en-zh", progress, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));
        throw new Exception("Active translation cancellation was ignored.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested && started) { Console.WriteLine("Active inference cancellation check passed."); }
}
try
{
    Directory.CreateDirectory(Path.Combine(temporary, "en-zh"));
    await File.WriteAllTextAsync(Path.Combine(temporary, "en-zh/config.json"), "corrupted");
    if (await new ModelStore(temporary).IsInstalledAsync("en-zh", CancellationToken.None)) throw new Exception("Corrupted model was accepted.");
    Console.WriteLine("Corrupt model rejection passed.");
}
finally { Directory.Delete(temporary, true); }
Console.WriteLine("2 semantic offline translation checks and 3 model/cancellation checks passed.");
return 0;

sealed class InlineProgress(Action<Snip.Core.TranslationProgress> action) : IProgress<Snip.Core.TranslationProgress>
{
    public void Report(Snip.Core.TranslationProgress value) => action(value);
}
