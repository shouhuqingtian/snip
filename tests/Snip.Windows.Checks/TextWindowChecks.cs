using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class TextWindowChecks
{
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Exception? dispatcherError = null;
        DispatcherUnhandledExceptionEventHandler handler = (_, e) => { dispatcherError = e.Exception; e.Handled = true; };
        app.DispatcherUnhandledException += handler;
        Window? window = null;
        string stage = "construct actual OCR result window";
        try
        {
            var image = BitmapSource.Create(400, 160, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 400 * 160 * 4).ToArray(), 1600); image.Freeze();
            using var vm = new TextResultViewModel(image, new FakeOcr(), new ModelStore("unused-models"), new FakeTranslation());
            window = new TextResultWindow(vm);
            stage = "show actual XAML and load OCR";
            window.Show();
            PumpUntil(() => dispatcherError is not null || vm.Source == "Hello world" && !vm.Busy, TimeSpan.FromSeconds(8));
            if (dispatcherError is not null) throw dispatcherError;
            if (!window.IsVisible || vm.Source != "Hello world" || vm.Busy) throw new InvalidOperationException("OCR result window disappeared or produced no visible result.");
            if (!Descendants<TextBox>(window).Any(box => box.Text == "Hello world")) throw new InvalidOperationException("OCR result did not reach the visible text box.");
            Console.WriteLine("PASS actual OCR result XAML stays open and displays recognized text.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("::error title=Text result UI::" + stage + ": " + error.ToString().Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A"));
            return 1;
        }
        finally { window?.Close(); app.DispatcherUnhandledException -= handler; }
    }
    internal static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { if (done() || clock.Elapsed > timeout) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!done()) throw new TimeoutException("Text UI workflow did not complete within the deadline.");
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    internal sealed class FakeOcr : IOcrService
    {
        public IReadOnlyList<string> Languages => ["en-US"];
        public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
        public Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new OcrResult([new("Hello world", new(0, 0, 180, 40), null)])); }
    }
    internal sealed class FakeTranslation : ITranslationService
    {
        public Task<string> TranslateAsync(string text, string direction, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult("你好，世界"); }
    }
}
