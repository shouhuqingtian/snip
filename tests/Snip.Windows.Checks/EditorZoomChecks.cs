using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class EditorZoomChecks
{
    public static int Run()
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string folder = Path.GetFullPath("artifacts/validation"); Directory.CreateDirectory(folder);
        string temporary = Path.Combine(Path.GetTempPath(), "Snip.EditorZoom-" + Guid.NewGuid()); Directory.CreateDirectory(temporary);
        EditorWindow? window = null;
        try
        {
            var image = Article(1200, 8000);
            var vm = new EditorViewModel(image, new AnnotationRenderer(), new ImageExportService(), new Dialogs(Path.Combine(temporary, "capture.png")));
            window = new EditorWindow { DataContext = vm }; window.Show();
            var viewport = (ScrollViewer)window.FindName("Viewport");
            var canvas = (EditorCanvas)viewport.Content;
            Wait(window, () => vm.IsFitWindow && viewport.ViewportHeight > 0 && vm.Zoom * image.PixelHeight <= viewport.ViewportHeight + 1);
            double entireZoom = vm.Zoom;
            Require(vm.Zoom < 0.1 && viewport.ScrollableHeight <= 1, "Fit window does not show the entire tall image.");
            Capture(window, "29-editor-fit-window");

            Click(window, "FitWidthButton"); WaitWidth(window, vm, viewport, canvas);
            double widthZoom = vm.Zoom;
            Require(widthZoom > entireZoom * 3 && viewport.ScrollableHeight > viewport.ViewportHeight * 3, "Fit width still shrinks a long image to its height.");
            Capture(window, "30-editor-fit-width");
            double fullViewport = viewport.ViewportWidth;
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            WaitWidth(window, vm, viewport, canvas);
            Require(viewport.ViewportWidth < fullViewport && vm.Zoom < widthZoom, "Fit width did not follow the compact window.");
            Capture(window, "31-editor-fit-width-compact");

            viewport.ScrollToEnd();
            Wait(window, () => viewport.VerticalOffset > 0 && Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) < 2);
            var presenter = TextWindowChecks.Descendants<ScrollContentPresenter>(viewport).Single();
            var start = new Point(100, image.PixelHeight - 180);
            var end = new Point(260, image.PixelHeight - 120);
            var visible = canvas.TranslatePoint(start, presenter);
            Require(visible.X >= 0 && visible.X < presenter.ActualWidth && visible.Y >= 0 && visible.Y < presenter.ActualHeight, "The bottom of the long image cannot be reached.");
            var mapped = presenter.TranslatePoint(visible, canvas);
            Require((mapped - start).Length < 0.01, "Zoom and scrolling changed image coordinate mapping.");
            vm.BeginPointer(new(mapped.X, mapped.Y), false); vm.EndPointer(new(end.X, end.Y));
            Require(vm.Save(), "Saving after fit-width annotation failed.");
            using (var stream = File.OpenRead(Path.Combine(temporary, "capture.png")))
            {
                var png = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Require(png.PixelWidth == image.PixelWidth && png.PixelHeight == image.PixelHeight, "Display fit resized the exported PNG.");
                byte[] pixel = new byte[4]; png.CopyPixels(new Int32Rect(100, image.PixelHeight - 150, 1, 1), pixel, 4, 0);
                Require(pixel[2] > 240 && pixel[1] < 80 && pixel[0] < 80, "Annotation was exported at a display coordinate instead of its original image pixel.");
            }

            vm.ZoomOutCommand.Execute(null); double manual = vm.Zoom;
            Require(vm.ZoomMode == EditorZoomMode.Manual, "Manual zoom did not leave automatic fit.");
            window.Width = 1050; Wait(window, () => viewport.ViewportWidth > fullViewport * 0.8);
            Require(vm.Zoom == manual, "Resizing overrode manual zoom.");
            Click(window, "FitWindowButton");
            Wait(window, () => vm.IsFitWindow && vm.Zoom * image.PixelHeight <= viewport.ViewportHeight + 1 && viewport.ScrollableHeight <= 1);
            Require(vm.BaseAnnotations.Count == 1, "Switching zoom mode changed annotations.");
            window.Close(); window = null;

            // Small images may upscale; very wide captures must fit below the old 10% floor.
            foreach (var (width, height, dpi) in new[] { (320, 160, 144), (12000, 160, 96), (1200, 650, 96) })
            {
                var fixture = Article(width, height, dpi);
                var small = new EditorViewModel(fixture, new AnnotationRenderer(), new ImageExportService(), new Dialogs("unused.png"));
                window = new EditorWindow { DataContext = small }; window.Show();
                viewport = (ScrollViewer)window.FindName("Viewport"); canvas = (EditorCanvas)viewport.Content;
                Wait(window, () => viewport.ViewportWidth > 0 && window.IsLoaded);
                Click(window, "FitWidthButton"); WaitWidth(window, small, viewport, canvas);
                Require(width != 320 || small.Zoom > 1, "Small-image fit width is incorrectly capped at 100%.");
                Require(width != 12000 || small.Zoom < 0.1, "Very wide image fit is incorrectly capped at 10%.");
                double stable = small.Zoom;
                for (int i = 0; i < 5; i++) window.UpdateLayout();
                Require(small.Zoom == stable, "Scrollbars caused fit-width layout oscillation.");
                window.Close(); window = null;
            }
            File.WriteAllText(Path.Combine(folder, "editor-fit-width.json"), JsonSerializer.Serialize(new { ImageWidth = image.PixelWidth, ImageHeight = image.PixelHeight, FitWindowZoom = entireZoom, FitWidthZoom = widthZoom, ExportWidth = image.PixelWidth, ExportHeight = image.PixelHeight }));
            Console.WriteLine("PASS actual WPF fit width: long-image vertical scroll, no horizontal clipping, compact resize follows width, manual zoom remains fixed, entire-image fit restored, small image upscales, wide image below 10%, bitmap DPI and scrollbar stability; bottom annotation saves at original PNG dimensions/coordinates.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { window?.Close(); Directory.Delete(temporary, true); }
    }
    private static void WaitWidth(EditorWindow window, EditorViewModel vm, ScrollViewer viewport, EditorCanvas canvas)
    {
        Wait(window, () => vm.IsFitWidth && Math.Abs(vm.PixelWidth * vm.Zoom - viewport.ViewportWidth) <= 2 && viewport.ScrollableWidth <= 1);
        var presenter = TextWindowChecks.Descendants<ScrollContentPresenter>(viewport).Single();
        var bounds = canvas.TransformToAncestor(presenter).TransformBounds(new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight));
        Require(bounds.Left >= -1 && bounds.Right <= presenter.ActualWidth + 1 && presenter.ActualWidth - bounds.Width <= 2, "Scaled canvas does not fill the actual content viewport.");
        foreach (string name in new[] { "FitWindowButton", "FitWidthButton" })
        {
            var button = (Button)window.FindName(name);
            var client = (FrameworkElement)window.Content;
            var rect = button.TransformToAncestor(client).TransformBounds(new Rect(button.RenderSize));
            Require(button.IsEnabled && rect.Left >= 0 && rect.Right <= client.ActualWidth + 1, "Fit controls are clipped or disabled.");
        }
    }
    private static void Wait(Window window, Func<bool> done)
    { window.UpdateLayout(); TextWindowChecks.PumpUntil(() => { window.UpdateLayout(); return done(); }, TimeSpan.FromSeconds(6)); }
    private static void Click(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        Require(button.IsEnabled && button.Command is not null && button.Command.CanExecute(null), "Fit action unavailable: " + name);
        button.Command!.Execute(null);
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill }, null, new Rect(content.RenderSize));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string folder = Path.GetFullPath("artifacts/validation/ui-previews"); Directory.CreateDirectory(folder);
        using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
    }
    private static BitmapSource Article(int width, int height, int dpi = 96)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int y = 20, section = 1; y < height; y += 180, section++)
            {
                void Text(string text, double size, double top, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1), new Point(40, top));
                Text("PUBLIC ARTICLE / Section " + section, 30, y, Brushes.DarkSlateGray);
                Text("Fit width makes a long screenshot readable.", 22, y + 55, Brushes.Black);
                Text("Scroll to read further. Original image pixels stay unchanged.", 20, y + 95, Brushes.SlateGray);
            }
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(drawing); image.Freeze();
        if (dpi == 96) return image;
        var pixels = new byte[width * height * 4]; image.CopyPixels(pixels, width * 4, 0);
        var adjusted = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Pbgra32, null, pixels, width * 4); adjusted.Freeze(); return adjusted;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Dialogs(string path) : IEditorDialogs
    {
        public string? ChoosePngPath() => path;
        public TextInput? EditText(string text, string color, double fontSize) => null;
        public CloseChoice ConfirmClose() => CloseChoice.Discard;
        public void ShowError(string message) => throw new InvalidOperationException(message);
    }
}
