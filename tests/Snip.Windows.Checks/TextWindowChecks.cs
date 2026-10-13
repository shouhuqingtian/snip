using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class TextWindowChecks
{
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Exception? dispatcherError = null;
        DispatcherUnhandledExceptionEventHandler handler = (_, e) => { dispatcherError = e.Exception; e.Handled = true; };
        app.DispatcherUnhandledException += handler;
        string stage = "OCR XAML", folder = Path.Combine(Path.GetTempPath(), "Snip.Text.Workflow-" + Guid.NewGuid());
        using var diagnostics = new DiagnosticJournal(folder);
        try
        {
            var image = Image(800, 600);
            TextResultWindow Show(bool translate, IOcrService? ocr = null, ITranslationService? translator = null)
            {
                var vm = new TextResultViewModel(image, ocr ?? new FakeOcr(), new ModelStore("unused-models"), translator ?? new FakeTranslation(), diagnostics) { AutoTranslate = translate };
                var result = new TextResultWindow(vm); result.Show();
                PumpUntil(() => dispatcherError is not null || !vm.Busy && (vm.Source.Length > 0 || vm.Status.Contains("失败") || vm.Status.Contains("没有识别")), TimeSpan.FromSeconds(8));
                if (dispatcherError is not null) throw dispatcherError;
                Require(result.IsVisible && result.IsLoaded, "Result window disappeared.");
                Require(result.FindName("ScreenshotPreview") is System.Windows.Controls.Image preview && preview.Source is not null && preview.ActualWidth > 0, "Screenshot preview is missing.");
                return result;
            }
            var window = Show(false);
            Require(window.FindName("RecognizedText") is TextBox source && source.Text == "Hello world", "Recognized text did not reach the visible text box.");
            window.Close(); Console.WriteLine("PASS actual OCR XAML displays screenshot and editable recognized text.");

            stage = "automatic translation XAML";
            window = Show(true);
            var text = (TextResultViewModel)window.DataContext;
            Require(text.ShowTranslation && text.Translated == "你好，世界", "Automatic translation did not complete.");
            Require(window.FindName("TranslatedText") is TextBox translated && translated.Text == text.Translated && translated.IsVisible, "Translation is not visible.");
            window.Close(); Console.WriteLine("PASS actual translation XAML keeps original and translated results visible.");

            stage = "missing model result retention";
            window = Show(true, translator: new FakeTranslation(fail: true));
            text = (TextResultViewModel)window.DataContext;
            Require(text.Source == "Hello world" && text.Status.Contains("失败") && window.IsVisible, "Missing model discarded the original or closed the result.");
            window.Close(); Console.WriteLine("PASS missing translation model retains window, screenshot and original text.");

            stage = "OCR failure result retention";
            window = Show(false, new FakeOcr(fail: true));
            text = (TextResultViewModel)window.DataContext;
            Require(text.Status.Contains("失败") && window.IsVisible, "OCR failure closed the result window.");
            window.Close(); Console.WriteLine("PASS OCR failure remains visible with an error.");

            stage = "empty OCR result feedback";
            window = Show(false, new FakeOcr(empty: true));
            Require(((TextResultViewModel)window.DataContext).Status.Contains("没有识别") && window.IsVisible, "Empty OCR silently closed or gave no feedback.");
            window.Close(); Console.WriteLine("PASS empty OCR keeps preview and gives visible feedback.");

            var capture = new FakeCapture(image); var renderer = new AnnotationRenderer(); var export = new FakeExport();
            using var pins = new PinManager(export);
            var features = new FeatureHub(renderer, export, capture, pins, diagnostics, new FakeOcr(), new FakeTranslation(), new ModelStore("unused-models"));
            foreach (var (action, tip) in new[] { (SelectionAction.Ocr, "离线文字识别"), (SelectionAction.Translate, "离线中英互译") })
            {
                stage = "actual selection toolbar " + action;
                using var selection = new SelectionSession(new(image, new(0, 0, 800, 600), capture.GetDisplays()), capture, renderer, export, []);
                selection.Show(); selection.ViewModel.SetSelection(new(40, 40, 320, 160)); selection.BeginEditing();
                var overlay = app.Windows.OfType<SelectionWindow>().Single(); overlay.UpdateLayout();
                var button = Descendants<Button>(overlay).Single(b => Equals(b.ToolTip, tip));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(selection.Completion.IsCompleted, "Toolbar did not finish the selection handoff.");
                var result = selection.Completion.GetAwaiter().GetResult();
                Require(result?.Action == action && !app.Windows.OfType<SelectionWindow>().Any(), "Screenshot overlays were not removed before showing results.");
                features.HandleAsync(result!).GetAwaiter().GetResult();
                window = app.Windows.OfType<TextResultWindow>().Single(); text = (TextResultViewModel)window.DataContext;
                PumpUntil(() => dispatcherError is not null || !text.Busy && text.Source == "Hello world", TimeSpan.FromSeconds(8));
                if (dispatcherError is not null) throw dispatcherError;
                Require(window.IsVisible && window.Owner is null && (!text.AutoTranslate || text.Translated == "你好，世界"), "Result depends on a closed screenshot owner or is empty.");
                selection.Dispose(); Require(window.IsVisible, "Disposing the screenshot session closed the result window.");
                window.Close(); Console.WriteLine("PASS real selection toolbar handoff: " + action);
            }
            stage = "independent editor toolbar";
            var editor = features.OpenEditor(new EditorViewModel(image, renderer, export, new EditorDialogs(() => null)));
            editor.UpdateLayout(); Descendants<Button>(editor).Single(b => Equals(b.ToolTip, "离线文字识别")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window = app.Windows.OfType<TextResultWindow>().Single(); text = (TextResultViewModel)window.DataContext;
            PumpUntil(() => !text.Busy && text.Source == "Hello world", TimeSpan.FromSeconds(8));
            editor.Close(); Require(window.IsVisible && window.Owner is null, "Closing editor lost OCR results.");
            window.Close(); Console.WriteLine("PASS editor toolbar result survives closing the editor.");

            diagnostics.FlushAsync().GetAwaiter().GetResult();
            string log = File.ReadAllText(Path.Combine(folder, "events.jsonl"));
            Require(log.Contains("window-created") && log.Contains("loaded") && log.Contains("ocr-recognized") && log.Contains("closed"), "Window/OCR lifecycle was not written to runtime logs.");
            Require(!log.Contains("Hello world") && !log.Contains("你好，世界"), "Runtime log leaked recognized/translated text.");
            Console.WriteLine("PASS latest runtime logs include window lifecycle and OCR stages without text.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("::error title=Text result UI::" + stage + ": " + error.ToString().Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A"));
            return 1;
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            app.DispatcherUnhandledException -= handler; SynchronizationContext.SetSynchronizationContext(previousContext);
            diagnostics.Dispose(); if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
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
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static BitmapSource Image(int width, int height)
    { var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, width * height * 4).ToArray(), width * 4); image.Freeze(); return image; }
    internal sealed class FakeOcr(bool fail = false, bool empty = false) : IOcrService
    {
        public IReadOnlyList<string> Languages => ["en-US"];
        public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
        public Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (fail) throw new InvalidOperationException("OCR component unavailable (test).");
            return Task.FromResult(new OcrResult(empty ? [] : [new("Hello world", new(0, 0, 180, 40), null)]));
        }
    }
    internal sealed class FakeTranslation(bool fail = false) : ITranslationService
    {
        public Task<string> TranslateAsync(string text, string direction, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (fail) throw new InvalidOperationException("Translation model not installed (test).");
            return Task.FromResult("你好，世界");
        }
    }
    private sealed class FakeExport : IImageExportService
    { public void SavePng(BitmapSource image, string path) { } public Task CopyAsync(BitmapSource image) => Task.CompletedTask; }
    private sealed class FakeCapture(BitmapSource image) : IDesktopCaptureService
    {
        private readonly DisplayInfo display = new("MockDisplay", new(0, 0, 800, 600), true);
        public IReadOnlyList<DisplayInfo> GetDisplays() => [display];
        public DesktopFrame Capture() => new(image, display.Bounds, GetDisplays());
        public PixelPoint CursorPosition => new(80, 80);
        public void PlaceOverlay(Window window, PixelRect bounds) { }
    }
}
