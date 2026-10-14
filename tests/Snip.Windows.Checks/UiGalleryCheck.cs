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

/// <summary>Renders the real WPF windows using public fixtures; never touches a user's screenshot or selects a live sharing target.</summary>
internal static class UiGalleryCheck
{
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Ui.EnsureTheme();
        string stage = "initialization";
        using var watchdog = new System.Threading.Timer(_ => { Console.WriteLine("::error title=UI gallery deadline::" + Volatile.Read(ref stage)); Environment.Exit(1); }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        Exception? dispatcherError = null;
        DispatcherUnhandledExceptionEventHandler handler = (_, e) => { dispatcherError = e.Exception; e.Handled = true; };
        app.DispatcherUnhandledException += handler;
        string output = Path.GetFullPath("artifacts/validation/ui-previews"); Directory.CreateDirectory(output);
        var image = Sample(); var export = new FakeExport();
        var renderer = new AnnotationRenderer();
        try
        {
            void Capture(Window window, string name, Action? ready = null)
            {
                stage = name; Console.WriteLine("::notice title=UI gallery::" + name);
                window.Show(); window.UpdateLayout();
                ready?.Invoke();
                var paint = Task.Delay(80); TextWindowChecks.PumpUntil(() => paint.IsCompleted, TimeSpan.FromSeconds(3));
                if (dispatcherError is not null) throw dispatcherError;
                Require(window.IsLoaded && window.IsVisible && window.ActualWidth > 0, "Window did not render: " + name);
                var content = (FrameworkElement)window.Content;
                foreach (var button in TextWindowChecks.Descendants<Button>(content).Where(b => b.IsVisible && b.ActualWidth > 0))
                {
                    var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1, "Button clipped outside the window in " + name + ": " + button.Content + "; bounds=" + bounds + "; content=" + content.ActualWidth + "x" + content.ActualHeight);
                }
                if (window is TextResultWindow)
                    foreach (var field in TextWindowChecks.Descendants<TextBox>(content).Where(b => b.IsVisible && b.Name is "RecognizedText" or "TranslatedText"))
                        Require(field.ActualHeight >= 70, "Text result is too short to read in " + name + ": " + field.ActualHeight + " DIP");
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                {
                    dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth + 48, content.ActualHeight + 48));
                    var offset = VisualTreeHelper.GetOffset(content);
                    var brush = new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(offset.X, offset.Y, content.ActualWidth, content.ActualHeight), Stretch = Stretch.Fill };
                    dc.DrawRectangle(brush, null, new Rect(24, 24, content.ActualWidth, content.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + 48), (int)Math.Ceiling(content.ActualHeight + 48), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(file);
                if (window is MainWindow main) main.AllowClose = true;
                window.Close();
                Console.WriteLine("PASS real themed WPF window: " + name);
            }
            var main = new MainWindow { DataContext = new MainViewModel(() => Task.CompletedTask, _ => { }, () => Task.CompletedTask) };
            Capture(main, "01-main");
            var editor = new EditorWindow { DataContext = new EditorViewModel(image, renderer, export, new FakeDialogs()) };
            Capture(editor, "02-editor", () =>
            {
                var button = TextWindowChecks.Descendants<RadioButton>(editor).Single(b => Equals(b.ToolTip, "椭圆"));
                button.IsChecked = true;
                Require(((EditorViewModel)editor.DataContext).Tool == AnnotationTool.Ellipse, "Themed tool radio no longer selects the tool.");
                var choice = TextWindowChecks.Descendants<ComboBox>(editor).First(); choice.IsDropDownOpen = true; choice.UpdateLayout(); choice.SelectedIndex = 3; choice.IsDropDownOpen = false;
                Require(((EditorViewModel)editor.DataContext).StrokeWidth == 8, "Themed width dropdown no longer updates the editor.");
                var color = TextWindowChecks.Descendants<Button>(editor).Single(b => Equals(b.ToolTip, "#FF9500"));
                color.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(((EditorViewModel)editor.DataContext).Color == "#FF9500", "Color swatches no longer update the editor.");
            });
            TextResultWindow Result(bool translation) => new(new TextResultViewModel(image, new GalleryOcr(), new ModelStore("unused-models"), new GalleryTranslation()) { AutoTranslate = translation });
            var result = Result(false);
            Capture(result, "03-ocr", () => TextWindowChecks.PumpUntil(() => !((TextResultViewModel)result.DataContext).Busy && ((TextResultViewModel)result.DataContext).Source.Length > 0, TimeSpan.FromSeconds(5)));
            result = Result(true);
            Capture(result, "04-translation", () => TextWindowChecks.PumpUntil(() => !((TextResultViewModel)result.DataContext).Busy && ((TextResultViewModel)result.DataContext).Translated.Length > 0, TimeSpan.FromSeconds(5)));
            result = Result(true); result.Width = result.MinWidth; result.Height = result.MinHeight;
            Capture(result, "05-translation-compact", () => TextWindowChecks.PumpUntil(() => !((TextResultViewModel)result.DataContext).Busy && ((TextResultViewModel)result.DataContext).Translated.Length > 0, TimeSpan.FromSeconds(5)));
            Capture(new TextDialog("为画面添加一点说明。\n截图、标注，然后分享。", "#079B70", 24), "06-text-dialog");
            Capture(new StickerPicker(), "07-stickers");
            Capture(new WindowsShareWindow(image, export, autoOpen: false), "08-windows-share");
            Capture(new PinManagerWindow([]), "10-pins-empty");
            var pin = new PinWindow(image, export); pin.Show();
            Capture(new PinManagerWindow([pin]), "11-pins-manager");
            Capture(pin, "12-pin");
            var capture = new FakeCapture(image);
            using var selection = new SelectionSession(new(image, new(0, 0, 1280, 800), capture.GetDisplays()), capture, renderer, export, []);
            selection.Show(); selection.ViewModel.SetSelection(new(90, 110, 1050, 460)); selection.BeginEditing();
            Capture(app.Windows.OfType<SelectionWindow>().Single(), "13-selection-toolbar");
            Console.WriteLine("PASS 12 actual UI previews; no share target invoked or user screenshot used."); return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("::error title=UI gallery::" + stage + ": " + error.ToString().Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A")); return 1;
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) { if (window is MainWindow main) main.AllowClose = true; window.Close(); }
            app.DispatcherUnhandledException -= handler; SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static BitmapSource Sample()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(243, 248, 247)), null, new Rect(0, 0, 1280, 800));
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(70, 70, 1140, 660), 22, 22);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(7, 155, 112)), null, new Rect(110, 110, 72, 72), 18, 18);
            void Text(string text, double size, Brush brush, double x, double y) => dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1), new Point(x, y));
            Text("A little clarity, every day.", 40, Brushes.DarkSlateGray, 110, 225);
            Text("Capture an idea. Make it yours.", 24, Brushes.SlateGray, 110, 295);
            Text("Keep the details that matter.", 24, Brushes.SlateGray, 110, 345);
            Text("SCREENSHOT  /  ANNOTATE  /  SHARE", 17, Brushes.SeaGreen, 110, 625);
            for (int i = 0; i < 3; i++) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb((byte)(225 + i * 5), (byte)(242 - i * 5), (byte)(235 + i * 4))), null, new Rect(760 + i * 90, 170 + i * 105, 170, 170), 22, 22);
        }
        var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    private sealed class GalleryOcr : IOcrService
    {
        public IReadOnlyList<string> Languages => ["en-US", "zh-CN"];
        public Task<OcrResult> RecognizeAsync(ImagePayload image, CancellationToken token) => RecognizeAsync(image, null, token);
        public Task<OcrResult> RecognizeAsync(ImagePayload image, string? language, CancellationToken token) => Task.FromResult(new OcrResult([new("A little clarity, every day.\r\n\r\nCapture an idea. Make it yours.\r\nKeep the details that matter.", new(0, 0, 300, 120), null)]));
    }
    private sealed class GalleryTranslation : ITranslationService
    { public Task<string> TranslateAsync(string text, string direction, CancellationToken token) => Task.FromResult("每天，让思路更清晰一点。\r\n\r\n捕捉一个灵感，让它成为你的作品。\r\n留下那些重要的细节。"); }
    private sealed class FakeExport : IImageExportService
    { public void SavePng(BitmapSource image, string path) { } public Task CopyAsync(BitmapSource image) => Task.CompletedTask; }
    private sealed class FakeDialogs : IEditorDialogs
    { public string? ChoosePngPath() => null; public TextInput? EditText(string text, string color, double fontSize) => null; public CloseChoice ConfirmClose() => CloseChoice.Discard; public void ShowError(string message) => throw new InvalidOperationException(message); }
    private sealed class FakeCapture(BitmapSource image) : IDesktopCaptureService
    {
        private readonly DisplayInfo display = new("Gallery", new(0, 0, 1280, 800), true);
        public IReadOnlyList<DisplayInfo> GetDisplays() => [display]; public DesktopFrame Capture() => new(image, display.Bounds, GetDisplays()); public PixelPoint CursorPosition => new(140, 150);
        public void PlaceOverlay(Window window, PixelRect bounds) { window.Left = window.Top = 0; }
    }
}
