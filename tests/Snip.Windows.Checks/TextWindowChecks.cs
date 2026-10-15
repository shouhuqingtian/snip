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

            stage = "OCR-only default with no translation resources";
            var emptyModels = new ModelStore(Path.Combine(folder, "no-models"));
            var emptyOnline = new OnlineTranslationSettingsStore(Path.Combine(folder, "no-online.json"));
            using var freshPins = new PinManager(new FakeExport());
            var noResources = new FeatureHub(new AnnotationRenderer(), new FakeExport(), new FakeCapture(image),
                freshPins, diagnostics, new FakeOcr(), models: emptyModels, onlineSettings: emptyOnline);
            window = noResources.ShowText(new EditorViewModel(image, new AnnotationRenderer(), new FakeExport(), new EditorDialogs(() => null)), true)!;
            text = (TextResultViewModel)window.DataContext;
            PumpUntil(() => !text.Busy && text.Source.Length > 0, TimeSpan.FromSeconds(8));
            Require(window.IsVisible && text.Source == "Hello world" && !text.ShowTranslation && !text.Status.Contains("失败"), "Fresh toolbar translation did not fall back to visible OCR.");
            Require(window.FindName("RecognizedText") is TextBox freshSource && freshSource.Text == text.Source && !freshSource.IsReadOnly && freshSource.IsVisible,
                "Fresh OCR text is not visible and editable.");
            Require(window.FindName("CopySourceButton") is Button copy && copy.IsEnabled, "Fresh OCR cannot be copied.");
            var retry = text.TranslateAsync(); PumpUntil(() => retry.IsCompleted, TimeSpan.FromSeconds(8)); retry.GetAwaiter().GetResult();
            Require(!text.ShowTranslation && !text.Status.Contains("失败") && text.Source == "Hello world", "Manual translation without a model discarded OCR or displayed an error.");
            text.UseOnlineTranslation = true;
            retry = text.TranslateAsync(); PumpUntil(() => retry.IsCompleted, TimeSpan.FromSeconds(8)); retry.GetAwaiter().GetResult();
            Require(!text.ShowTranslation && text.Status.Contains("在线翻译尚未配置") && !text.Status.Contains("失败") && text.Source == "Hello world", "Online mode without credentials discarded OCR.");
            Require(!Directory.Exists(Path.Combine(folder, "no-models")) && !File.Exists(Path.Combine(folder, "no-online.json")), "OCR-only fallback installed a model or wrote online settings.");
            window.Close(); Console.WriteLine("PASS real FeatureHub: no model or online settings keeps editable, copyable OCR for automatic and manual translation.");

            stage = "compute settings actual XAML and restart";
            var computeModels = new ModelStore(Path.Combine(folder, "compute-models"));
            var compute = new TextResultViewModel(image, new FakeOcr(), computeModels, new FakeTranslation());
            window = new TextResultWindow(compute); window.Show();
            PumpUntil(() => !compute.Busy && compute.Source.Length > 0, TimeSpan.FromSeconds(8));
            compute.ShowOptions = true; window.UpdateLayout();
            var computeBox = (ComboBox)window.FindName("ComputeModeBox");
            computeBox.SelectedValue = TranslationComputeMode.Gpu;
            Require(compute.ComputeMode == TranslationComputeMode.Gpu && new ModelStore(computeModels.DirectoryPath).ComputeSettings.Read() == TranslationComputeMode.Gpu,
                "GPU preference did not persist through actual XAML.");
            Require(!File.Exists(computeModels.ModelPath) && !computeModels.HasRuntimeFiles(TranslationBackend.Cpu), "Changing a preference downloaded models or a runtime.");
            compute.UseOnlineTranslation = true; window.UpdateLayout();
            Require(!computeBox.IsEnabled && window.FindName("DownloadGpuButton") is Button gpuDownload && !gpuDownload.IsEnabled,
                "Online mode left local GPU controls enabled.");
            Require(compute.DownloadGpuCommand.CanExecute(null) == false, "Online mode can start a GPU download.");
            window.Close();
            using (var restarted = new TextResultViewModel(image, new FakeOcr(), computeModels, new FakeTranslation()))
                Require(restarted.ComputeMode == TranslationComputeMode.Gpu, "Restart lost GPU preference.");
            Console.WriteLine("PASS actual compute mode selector persists; selecting a mode does not download; online mode disables local GPU controls.");

            stage = "missing Windows OCR language component";
            window = Show(true, new NoLanguageOcr());
            text = (TextResultViewModel)window.DataContext;
            Require(window.IsVisible && text.Status.Contains("Windows OCR 语言组件"), "Missing Windows language component did not retain the result and explain the OS dependency.");
            window.Close(); Console.WriteLine("PASS absent Windows OCR language component keeps preview and gives explicit language-install guidance.");

            stage = "translation inference failure result retention";
            window = Show(true, translator: new FakeTranslation(fail: true));
            text = (TextResultViewModel)window.DataContext;
            Require(text.Source == "Hello world" && text.Status.Contains("失败") && window.IsVisible, "Missing model discarded the original or closed the result.");
            window.Close(); Console.WriteLine("PASS translation inference failure retains window, screenshot and original text.");

            stage = "OCR failure result retention";
            window = Show(false, new FakeOcr(fail: true));
            text = (TextResultViewModel)window.DataContext;
            Require(text.Status.Contains("失败") && window.IsVisible, "OCR failure closed the result window.");
            window.Close(); Console.WriteLine("PASS OCR failure remains visible with an error.");

            stage = "empty OCR result feedback";
            window = Show(false, new FakeOcr(empty: true));
            Require(((TextResultViewModel)window.DataContext).Status.Contains("没有识别") && window.IsVisible, "Empty OCR silently closed or gave no feedback.");
            window.Close(); Console.WriteLine("PASS empty OCR keeps preview and gives visible feedback.");

            stage = "automatic language empty-result fallback";
            var multilingual = new MultilingualOcr();
            window = Show(true, multilingual);
            text = (TextResultViewModel)window.DataContext;
            Require(text.Language == OcrLanguageSelection.Automatic && text.Source == "Hello world" && text.Translated == "你好，世界", "Automatic English fallback did not reach visible source and translation.");
            Require(multilingual.Calls.SequenceEqual(new[] { "zh-Hans-CN", "en-US" }), "Automatic OCR did not retry exactly one English recognizer.");
            window.Close(); Console.WriteLine("PASS Chinese-empty automatic mode retries English and then translates visible result.");

            stage = "manual language respects selection";
            multilingual = new MultilingualOcr();
            var manual = new TextResultViewModel(image, multilingual, new ModelStore("unused-models"), new FakeTranslation()) { Language = "zh-Hans-CN" };
            window = new TextResultWindow(manual); window.Show(); PumpUntil(() => !manual.Busy && manual.Status.Contains("没有识别"), TimeSpan.FromSeconds(8));
            Require(multilingual.Calls.SequenceEqual(new[] { "zh-Hans-CN" }) && manual.Status.Contains("自动"), "Manual language was silently changed or lacked actionable empty result.");
            window.Close(); Console.WriteLine("PASS explicit language stays fixed; empty result recommends automatic mode.");

            var capture = new FakeCapture(image); var renderer = new AnnotationRenderer(); var export = new FakeExport();
            using var pins = new PinManager(export);
            var features = new FeatureHub(renderer, export, capture, pins, diagnostics, new FakeOcr(), new FakeTranslation(), new ModelStore("unused-models"));
            foreach (var (action, tip) in new[] { (SelectionAction.Ocr, "离线文字识别"), (SelectionAction.Translate, "文字识别 / 中英翻译") })
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

            stage = "long translation reading and speech";
            TextReadingChecks.Run(folder, image);
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
    private sealed class NoLanguageOcr : IOcrService
    {
        public IReadOnlyList<string> Languages => [];
        public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
        public Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token)
            => throw new InvalidOperationException("Recognition must not run without a language component.");
    }
    private sealed class MultilingualOcr : IOcrService
    {
        public IReadOnlyList<string> Languages => ["zh-Hans-CN", "en-US"];
        public List<string> Calls { get; } = [];
        public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
        public Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(language!);
            return Task.FromResult(new OcrResult(language == "en-US" ? [new("Hello world", new(0, 0, 180, 40), null)] : []));
        }
    }
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
