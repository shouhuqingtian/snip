using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Translation;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class NativeOcrCheck
{
    public static int Run(bool withResultUi = false)
    {
        try
        {
            var service = new OfflineOcrService();
            var languages = Task.Run(() => service.Languages).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var english = languages.FirstOrDefault(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (english is null) { Console.WriteLine("UNRUN native OCR: English Windows OCR language component is not installed."); return 2; }
            var bytes = Enumerable.Repeat((byte)255, 600 * 150 * 4).ToArray();
            var background = BitmapSource.Create(600, 150, 96, 96, PixelFormats.Bgra32, null, bytes, 600 * 4); background.Freeze();
            var annotation = new Annotation(Guid.NewGuid(), AnnotationTool.Text, new(20, 25), new(550, 120), "#000000", 2, 48, 8, "Hello world", []);
            var image = new AnnotationRenderer().Render(background, [annotation]);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream(); encoder.Save(stream);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = service.RecognizeAsync(new ImagePayload(stream.ToArray(), 600, 150), english, cancellation.Token).WaitAsync(cancellation.Token).GetAwaiter().GetResult();
            var text = string.Join(" ", result.Regions.Select(r => r.Text));
            if (!text.Contains("Hello", StringComparison.OrdinalIgnoreCase) || !text.Contains("world", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Native OCR semantic assertion failed: " + text);
            Console.WriteLine("PASS native Windows OCR on background worker: " + text);
            if (withResultUi)
            {
                _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                TextResultWindow? window = null;
                try
                {
                    var models = new ModelStore(Path.GetFullPath(".model-cache/translation"));
                    using var vm = new TextResultViewModel(image, service, models, new OfflineTranslator(models)) { AutoTranslate = true, Language = english };
                    window = new TextResultWindow(vm); window.Show();
                    TextWindowChecks.PumpUntil(() => !vm.Busy && (vm.Translated.Length > 0 || vm.Status.Contains("失败")), TimeSpan.FromSeconds(40));
                    if (!window.IsVisible || !vm.Source.Contains("Hello", StringComparison.OrdinalIgnoreCase) || !vm.Translated.Contains("世界"))
                        throw new InvalidOperationException("Native OCR-to-translation UI pipeline failed: " + vm.Status);
                    if (window.FindName("RecognizedText") is not TextBox sourceBox || sourceBox.Text != vm.Source ||
                        window.FindName("TranslatedText") is not TextBox translatedBox || translatedBox.Text != vm.Translated || !translatedBox.IsVisible)
                        throw new InvalidOperationException("Native OCR/translation text was not visible in the result window.");
                    Console.WriteLine("PASS actual result window: native English OCR → real offline translation → both visible results; window stays open.");
                }
                finally { window?.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL native OCR: " + e); return 1; }
    }
}
