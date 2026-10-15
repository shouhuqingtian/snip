using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class ManualPreviewChecks
{
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Ui.EnsureTheme();
        var host = new Window { Style = null, WindowStyle = WindowStyle.None, Background = Brushes.White, Left = 30, Top = 30, Width = 900, Height = 720 };
        ManualScrollCaptureController? controller = null;
        try
        {
            host.Show(); host.Activate(); host.UpdateLayout();
            var start = host.PointToScreen(new Point(40, 130)); var end = host.PointToScreen(new Point(740, 530));
            var region = new PixelRect(start.X, start.Y, end.X - start.X, end.Y - start.Y).RoundOut();
            var desktop = new DesktopCaptureService(); var baseline = Pixels(desktop.CaptureRegion(region));
            var watch = Stopwatch.StartNew();
            controller = new ManualScrollCaptureController(() => throw new InvalidOperationException("Preview stole focus."));
            controller.Bind(region, new WindowInteropHelper(host).Handle); double bindMs = watch.Elapsed.TotalMilliseconds;
            controller.ViewModel.Update(new(1, (int)region.Height, "Public preview fixture", false));
            controller.Control.Begin(new(20, 30));
            int before = controller.PreviewUpdates; watch.Restart();
            for (int i = 0; i < 1000; i++) { controller.Control.Move(new(100 + i % 300, 120 + i % 80)); controller.RefreshSelection(); }
            double burstMs = watch.Elapsed.TotalMilliseconds;
            Require(controller.PreviewUpdates == before, "Pointer callbacks synchronously redraw the outline.");
            TextWindowChecks.PumpUntil(() => controller.PreviewUpdates > before, TimeSpan.FromSeconds(3));
            Require(controller.PreviewUpdates == before + 1 && controller.PreviewSurfacePixels < region.Width * region.Height / 20, "Pointer burst was not coalesced into thin border surfaces.");
            var selected = controller.Control.VisibleSelection;
            var pixel = new PixelRect(region.X + selected.X + 10, region.Y + selected.Y, 1, 1);
            var accent = ((SolidColorBrush)Ui.Brush("AccentBrush")).Color;
            TextWindowChecks.PumpUntil(() => { var b = Pixels(desktop.CaptureRegion(pixel)); return b[0] == accent.B && b[1] == accent.G && b[2] == accent.R; }, TimeSpan.FromSeconds(3));
            int updates = controller.PreviewUpdates - before;
            controller.SetPreviewVisible(false);
            for (int i = 0; i < 50; i++) { controller.Control.Move(new(200 + i, 200)); controller.RefreshSelection(); }
            var wait = Task.Delay(120); TextWindowChecks.PumpUntil(() => wait.IsCompleted, TimeSpan.FromSeconds(3));
            Require(baseline.SequenceEqual(Pixels(desktop.CaptureRegion(region))), "Queued preview redraw leaked a border into a hidden screenshot.");
            controller.SetPreviewVisible(true);
            Require(DesktopWindows.Foreground == new WindowInteropHelper(host).Handle, "Outline activated over the target.");
            controller.Dispose(); controller = null;
            Require(!app.Windows.OfType<ManualScrollCaptureWindow>().Any(), "Manual toolbar survived disposal.");

            using var fourK = new CaptureOutline(accent); fourK.Show(new(-5000, -5000, 3840, 2160), 2);
            long borderPixels = fourK.SurfacePixels;
            Require(borderPixels == 23984, "4K border unexpectedly allocates a viewport-sized surface.");
            var report = new { BindMilliseconds = Math.Round(bindMs, 2), PointerUpdates = 1000, BurstMilliseconds = Math.Round(burstMs, 2), AppliedPreviewUpdates = updates, Old4KSurfacePixels = 3840L * 2160, New4KSurfacePixels = borderPixels, HiddenCaptureClean = true };
            Directory.CreateDirectory("artifacts/validation"); File.WriteAllText("artifacts/validation/manual-preview-performance.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS actual native manual preview: 1000 pointer updates coalesced, visible accent pixels, hidden capture remains clean despite queued moves, focus/disposal and 4K thin surfaces.");
            Console.WriteLine(JsonSerializer.Serialize(report)); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { controller?.Dispose(); host.Close(); SynchronizationContext.SetSynchronizationContext(context); }
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes, converted.PixelWidth * 4, 0); return bytes;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
