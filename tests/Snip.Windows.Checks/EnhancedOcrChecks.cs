using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Ocr;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class EnhancedOcrChecks
{
    public static int Run(string? executable = null)
    {
        string stage = "initialization", temporary = Path.Combine(Path.GetTempPath(), "Snip.EnhancedOcr-" + Guid.NewGuid().ToString("N"));
        TextResultWindow? window = null;
        try
        {
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var prior = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            try
            {
                executable ??= Path.GetFullPath("src/Snip.App/bin/Release/net8.0-windows10.0.19041.0/Snip.exe");
                Require(File.Exists(executable), "Worker executable missing.");
                var system = new SystemFallback();
                var store = new OcrModelStore(Path.GetFullPath(".model-cache/ocr/PP-OCRv6-small"));
                var fresh = new OcrModelStore(Path.Combine(temporary, "fresh"));
                var noModels = new EnhancedOcrService(system, fresh, () => new ProcessStartInfo(executable));
                var white = Draw("Google", 18);
                var native = noModels.RecognizeAsync(Payload(white), "en-US", default).GetAwaiter().GetResult();
                Require(system.Calls == 1 && native.Regions[0].Text == "System fallback" && !Directory.Exists(fresh.DirectoryPath), "Fresh install loaded/downloaded enhanced models.");
                system.Calls = 0;
                stage = "verified model installation";
                var install = Task.Run(() => store.InstallAsync(null, default));
                TextWindowChecks.PumpUntil(() => install.IsCompleted, TimeSpan.FromMinutes(4)); install.GetAwaiter().GetResult();
                Require(store.HasFiles && store.ValidateAsync(default).GetAwaiter().GetResult(), "Installed models failed SHA256 validation.");
                store.SaveEnabled(true);
                using var journal = new DiagnosticJournal(Path.Combine(temporary, "logs"));
                var service = new EnhancedOcrService(system, store, () => new ProcessStartInfo(executable), journal);
                var metrics = new List<EnhancedOcrMetrics>(); service.WorkerReleased += value => { lock (metrics) metrics.Add(value); };
                stage = "real bilingual worker semantics";
                foreach (int size in new[] { 12, 18, 28 })
                {
                    foreach (var sample in new[] { ("Google", false), ("是否开启提示：\n通知方式：\n生效日期：\n处理状态：", true) })
                    {
                        var image = Draw(sample.Item1, size, bold: sample.Item2);
                        var result = Recognize(service, Payload(image));
                        string actual = string.Concat(result.Regions.Select(r => r.Text));
                        Require(Normalize(actual) == Normalize(sample.Item1), "Bilingual semantic assertion failed for public fixture at " + size + "px; Chinese=" + sample.Item2);
                        Require(result.Regions.All(r => !r.Bounds.IsEmpty && r.Bounds.Intersect(new(0, 0, image.PixelWidth, image.PixelHeight)) == r.Bounds), "Source coordinate mapping failed.");
                        Require(metrics[^1].ProcessExited && !IsAlive(metrics[^1].ProcessId), "Successful worker retained native memory.");
                    }
                }
                Require(system.Calls == 0, "Enhanced samples silently used system OCR.");
                Console.WriteLine("PASS real isolated PP-OCRv6 small: Google and Chinese form labels at 12/18/28 px, source coordinates, each worker exits.");
                stage = "gray labels and red required markers";
                const string publicLabels = "是否开启提示：\n通知方式：\n生效日期：\n处理状态：";
                string marked = Normalize(string.Concat(Recognize(service, Payload(Draw(publicLabels, 18, bold: true, required: true))).Regions.Select(r => r.Text)));
                int position = -1;
                foreach (string label in new[] { "是否开启提示", "通知方式", "生效日期", "处理状态" })
                {
                    int next = marked.IndexOf(label, StringComparison.Ordinal);
                    Require(next > position, "Gray form label was wrong or reordered beside a red marker."); position = next;
                }
                Console.WriteLine("PASS gray bold public Chinese form labels with red required markers; labels correct and ordered (marker punctuation is not scored).");
                stage = "long page overlap";
                var lines = Enumerable.Range(1, 34).Select(i => $"通知记录 {i:000} Status Ready").ToArray();
                var longImage = Draw(string.Join("\n", lines), 24, spacing: 50);
                var longResult = Recognize(service, Payload(longImage));
                Require(Normalize(string.Concat(longResult.Regions.Select(r => r.Text))) == Normalize(string.Concat(lines)), "Long-page tiles lost, duplicated or reordered text at overlaps.");
                Require(longResult.Regions.Count == lines.Length, "Long-page line count mismatch.");
                Console.WriteLine("PASS actual vertical tile overlaps: 34 numbered mixed-language rows, no missing/duplicate lines.");
                stage = "source-layout form rows";
                foreach (int count in new[] { 3, 30 })
                {
                    var form = DrawForm(count);
                    var result = Recognize(service, Payload(form));
                    var formattedRows = result.LayoutText?.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries) ?? [];
                    Console.WriteLine($"Source-layout geometry: fixtureRows={count}, formattedRows={formattedRows.Length}, regions={result.Regions.Count}");
                    foreach (var region in result.Regions.Take(12))
                        Console.WriteLine($"Source-layout box: x={region.Bounds.X:F1}, y={region.Bounds.Y:F1}, w={region.Bounds.Width:F1}, h={region.Bounds.Height:F1}");
                    Require(formattedRows.Length == count, "Source-layout form rows merged or split; expected=" + count + ", actual=" + formattedRows.Length);
                    var rowPositions = result.LayoutText!.Split(Environment.NewLine).Select((text, index) => (text, index)).Where(row => row.text.Length > 0).Select(row => row.index).ToArray();
                    Require(rowPositions.Skip(1).Zip(rowPositions, (next, prior) => next - prior).Distinct().Count() == 1, "Equal source row pitch became unequal text spacing.");
                    string[] labels = ["通知方式：", "处理状态：", "生效日期："], values = ["Email", "Ready", "Today"];
                    for (int i = 0; i < count; i++)
                        Require(Normalize(formattedRows[i]) == Normalize(labels[i % 3] + values[i % 3]), "Source-layout left label/right value order changed at row " + i);
                    string expected = string.Concat(Enumerable.Range(0, count).Select(i => labels[i % 3] + values[i % 3]));
                    string oldOrder = string.Concat(result.Regions.OrderBy(r => r.Bounds.Y).ThenBy(r => r.Bounds.X).Select(r => r.Text));
                    Require(Normalize(oldOrder) != Normalize(expected), "Public fixture did not reproduce the previous exact-Y ordering defect.");
                    Require(system.Calls == 0 && metrics[^1].ProcessExited, "Source-layout sample used fallback or left worker alive.");
                }
                Console.WriteLine("PASS real source-layout forms: previous exact-Y order reproduced; same-row cells preserved left-to-right across 30 tiled rows.");
                stage = "source-layout result and copying";
                var layoutImage = DrawForm(3);
                var copied = new LayoutClipboard();
                using (var layoutVm = new TextResultViewModel(layoutImage, service, new ModelStore(Path.Combine(temporary, "layout-models")), new TextWindowChecks.FakeTranslation(), clipboard: copied))
                {
                    window = new TextResultWindow(layoutVm); window.Show();
                    TextWindowChecks.PumpUntil(() => !layoutVm.Busy && layoutVm.Source.Length > 0, TimeSpan.FromSeconds(20));
                    var sourceBox = (TextBox)window.FindName("RecognizedText"); window.UpdateLayout();
                    Require(layoutVm.HasSourceLayout && sourceBox.Text == layoutVm.Source && sourceBox.TextWrapping == TextWrapping.NoWrap && sourceBox.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto && sourceBox.FontFamily.Source.Contains("Consolas"), "Original-layout XAML changed rows or lacks horizontal navigation.");
                    layoutVm.CopySourceCommand.Execute(null);
                    TextWindowChecks.PumpUntil(() => copied.Value is not null && layoutVm.CopyStatus.Contains("已复制"), TimeSpan.FromSeconds(3));
                    Require(copied.Value == layoutVm.Source && copied.Value.Contains("  "), "Copy discarded source-layout whitespace.");
                    string layoutPreview = Path.GetFullPath("artifacts/validation/ui-previews/28-ocr-source-layout.png"); Directory.CreateDirectory(Path.GetDirectoryName(layoutPreview)!);
                    var layoutVisual = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); layoutVisual.Render(window);
                    var layoutPng = new PngBitmapEncoder(); layoutPng.Frames.Add(BitmapFrame.Create(layoutVisual)); using (var file = File.Create(layoutPreview)) layoutPng.Save(file);
                    window.Close(); window = null;
                }
                Console.WriteLine("PASS actual source-layout WPF: fixed-width view, no automatic wrapping, horizontal scrolling and unchanged whitespace on copy.");
                stage = "real result window and settings";
                using var vm = new TextResultViewModel(white, service, new ModelStore(Path.Combine(temporary, "translation")), new TextWindowChecks.FakeTranslation(), journal);
                int ticks = 0; var heartbeat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                heartbeat.Tick += (_, _) => ticks++; heartbeat.Start();
                window = new TextResultWindow(vm); window.Show();
                TextWindowChecks.PumpUntil(() => !vm.Busy && vm.Source.Length > 0, TimeSpan.FromSeconds(20)); heartbeat.Stop();
                Require(vm.Source == "Google" && vm.Status.Contains("增强离线 OCR") && ticks > 1, "Real UI did not show enhanced result responsively.");
                vm.ShowOptions = true; TextWindowChecks.PumpUntil(() => ((System.Windows.Controls.Primitives.Popup)window.FindName("TextOptionsPopup")).IsOpen, TimeSpan.FromSeconds(3));
                Require(window.FindName("EnhancedOcrBox") is CheckBox box && box.IsChecked == true && box.IsEnabled &&
                    window.FindName("DownloadOcrButton") is Button download && download.IsEnabled, "Enhanced options are not usable.");
                var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("TextOptionsPopup");
                var options = (FrameworkElement)popup.Child; options.UpdateLayout();
                Require(options.ActualWidth > 600 && options.ActualHeight > 100 && options.ActualHeight < 650, "Enhanced options popup has invalid bounds.");
                string preview = Path.GetFullPath("artifacts/validation/ui-previews/26-enhanced-ocr-options.png"); Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
                var visual = new RenderTargetBitmap((int)Math.Ceiling(options.ActualWidth), (int)Math.Ceiling(options.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                visual.Render(options); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(visual)); using (var file = File.Create(preview)) png.Save(file);
                vm.UseEnhancedOcr = false;
                Require(!new OcrModelStore(store.DirectoryPath).ReadEnabled(), "System mode did not persist.");
                vm.UseEnhancedOcr = true;
                Require(new OcrModelStore(store.DirectoryPath).ReadEnabled(), "Enhanced mode did not persist.");
                window.Close(); window = null;
                Console.WriteLine("PASS actual WPF result: responsive, visible enhanced engine, saved mode switches and available repair entry.");
                stage = "cancel closes native worker";
                int before = metrics.Count;
                using (var cancellation = new CancellationTokenSource())
                {
                    void CancelStarted(int processId) { _ = Task.Run(async () => { await Task.Delay(100); cancellation.Cancel(); }); }
                    service.WorkerStarted += CancelStarted;
                    var cancelled = Task.Run(() => service.RecognizeAsync(Payload(longImage), cancellation.Token));
                    TextWindowChecks.PumpUntil(() => cancelled.IsCompleted, TimeSpan.FromSeconds(8));
                    try { cancelled.GetAwaiter().GetResult(); throw new InvalidOperationException("Cancellation ignored."); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    finally { service.WorkerStarted -= CancelStarted; }
                }
                Require(metrics.Count == before + 1 && metrics[^1].ProcessExited && !IsAlive(metrics[^1].ProcessId), "Cancellation left a worker alive.");
                Console.WriteLine("PASS cancellation kills/reaps the actual native worker without falling back or blocking the dispatcher.");
                stage = "parent process exit";
                string lifetimeImage = Path.Combine(temporary, "public-lifetime-fixture.png");
                var extended = Draw(string.Join("\n", Enumerable.Range(1, 220).Select(i => $"通知记录 {i:000} Status Ready")), 24, spacing: 50);
                File.WriteAllBytes(lifetimeImage, Payload(extended).PngBytes.ToArray());
                var launcherInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                launcherInfo.ArgumentList.Add("--ocr-lifetime-launcher"); launcherInfo.ArgumentList.Add(executable);
                launcherInfo.ArgumentList.Add(store.DirectoryPath); launcherInfo.ArgumentList.Add(lifetimeImage);
                using (var launcher = Process.Start(launcherInfo) ?? throw new InvalidOperationException("Lifetime helper failed to start."))
                {
                    var pidLine = launcher.StandardOutput.ReadLineAsync();
                    TextWindowChecks.PumpUntil(() => pidLine.IsCompleted, TimeSpan.FromSeconds(20));
                    Require(int.TryParse(pidLine.GetAwaiter().GetResult(), out int child), "Lifetime helper did not report an input-ready child.");
                    TextWindowChecks.PumpUntil(() => launcher.HasExited, TimeSpan.FromSeconds(5));
                    Require(launcher.ExitCode == 0, "Lifetime helper failed.");
                    TextWindowChecks.PumpUntil(() => !IsAlive(child), TimeSpan.FromSeconds(3));
                }
                Console.WriteLine("PASS Windows job kills the input-ready OCR worker when its parent exits without managed cleanup.");
                stage = "corrupt model fallback";
                var corrupt = new OcrModelStore(Path.Combine(temporary, "corrupt")); Directory.CreateDirectory(corrupt.DirectoryPath);
                foreach (var file in OcrModelStore.Files) File.Copy(Path.Combine(store.DirectoryPath, file.Name), Path.Combine(corrupt.DirectoryPath, file.Name));
                string keys = Path.Combine(corrupt.DirectoryPath, "keys.txt");
                using (var stream = new FileStream(keys, FileMode.Open, FileAccess.Write)) stream.WriteByte(0);
                var fallbackService = new EnhancedOcrService(system, corrupt, () => new ProcessStartInfo(executable), journal);
                int calls = system.Calls;
                var fallback = Recognize(fallbackService, Payload(white));
                Require(system.Calls == calls + 1 && fallback.Regions[0].Text == "System fallback" && fallbackService.EngineDescription.Contains("回退"), "Corrupt model did not visibly fall back.");
                Console.WriteLine("PASS same-size model corruption fails SHA256 validation before inference and visibly falls back to installed system language.");
                stage = "privacy and parent native modules";
                journal.FlushAsync().GetAwaiter().GetResult();
                string logs = string.Join("\n", Directory.GetFiles(Path.Combine(temporary, "logs"), "*.jsonl").Select(File.ReadAllText));
                Require(logs.Contains("worker-released") && !logs.Contains("Google") && !logs.Contains("是否开启提示") && !logs.Contains("System fallback"), "Worker telemetry included OCR content or omitted cleanup.");
                using var parent = Process.GetCurrentProcess();
                Require(!parent.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Contains("libSkiaSharp", StringComparison.OrdinalIgnoreCase)), "Parent loaded native enhanced inference libraries.");
                var output = Path.GetFullPath("artifacts/validation/enhanced-ocr-metrics.json"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, JsonSerializer.Serialize(new { Engine = "PP-OCRv6-small", ModelBytes = OcrModelStore.Files.Sum(f => f.Size), Runs = metrics }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("PASS parent has no ONNX/Skia native modules; all recorded workers exited; telemetry contains technical metadata only.");
            }
            finally { SynchronizationContext.SetSynchronizationContext(prior); }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL enhanced OCR at " + stage + ": " + error); return 1; }
        finally { window?.Close(); if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    internal static int LifetimeLauncher(string[] args)
    {
        try
        {
            var store = new OcrModelStore(args[2]);
            var service = new EnhancedOcrService(new SystemFallback(), store, () => new ProcessStartInfo(args[1]));
            service.WorkerInputSent += pid =>
            {
                Console.WriteLine(pid); Console.Out.Flush();
                _ = Task.Run(async () => { await Task.Delay(200); Environment.Exit(0); });
            };
            byte[] png = File.ReadAllBytes(args[3]);
            using var memory = new MemoryStream(png); var bitmap = BitmapFrame.Create(memory, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            _ = service.RecognizeAsync(new(png, bitmap.PixelWidth, bitmap.PixelHeight), default).GetAwaiter().GetResult();
            return 3; // The deliberately long public image must still be processing at parent exit.
        }
        catch { return 4; }
    }
    private static OcrResult Recognize(EnhancedOcrService service, ImagePayload payload)
    {
        var task = Task.Run(() => service.RecognizeAsync(payload, default));
        TextWindowChecks.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(45)); return task.GetAwaiter().GetResult();
    }
    private static bool IsAlive(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static ImagePayload Payload(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var memory = new MemoryStream(); encoder.Save(memory);
        return new(memory.ToArray(), image.PixelWidth, image.PixelHeight);
    }
    private static BitmapSource DrawForm(int count)
    {
        string[] labels = ["通知方式：", "处理状态：", "生效日期："], values = ["Email", "Ready", "Today"];
        int width = 840, height = count * 72 + 40;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int i = 0; i < count; i++)
            {
                var label = new FormattedText(labels[i % 3], System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 24, Brushes.Black, 1);
                var value = new FormattedText(values[i % 3], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 30, Brushes.Black, 1);
                double baseline = 56 + i * 72;
                drawing.DrawText(label, new Point(24, baseline - label.Baseline));
                drawing.DrawText(value, new Point(460, baseline - value.Baseline));
            }
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    private sealed class LayoutClipboard : ITextClipboardService
    {
        public string? Value;
        public Task CopyAsync(string text, CancellationToken token, IProgress<int>? retries = null)
        { token.ThrowIfCancellationRequested(); Value = text; return Task.CompletedTask; }
    }
    private static BitmapSource Draw(string text, double size, bool bold = false, double spacing = 0, bool required = false)
    {
        var rows = text.Split('\n');
        var formatted = rows.Select(row => new FormattedText(row, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, required ? Brushes.DimGray : Brushes.Black, 1)).ToArray();
        double rowHeight = formatted.Max(row => row.Height) + spacing;
        int margin = required ? 40 : 16;
        int width = (int)Math.Ceiling(formatted.Max(row => row.WidthIncludingTrailingWhitespace)) + margin + 16, height = (int)Math.Ceiling(rowHeight * rows.Length) + 32;
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int i = 0; i < formatted.Length; i++)
            {
                drawing.DrawText(formatted[i], new Point(margin, 16 + i * rowHeight));
                if (required) drawing.DrawText(new FormattedText("*", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), size, Brushes.Red, 1), new Point(16, 16 + i * rowHeight));
            }
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    private sealed class SystemFallback : IOcrService
    {
        public int Calls;
        public IReadOnlyList<string> Languages => ["en-US"];
        public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
        public Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (language != "en-US") throw new InvalidOperationException("Uninstalled fallback language requested."); Calls++; return Task.FromResult(new OcrResult([new("System fallback", new(0, 0, 10, 10), null)])); }
    }
}
