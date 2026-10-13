using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Snip.Core;
using Snip.Infrastructure;
using Snip.App.Services;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Snip.Windows.Checks;

internal static class ChromeCaptureCheck
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder title, int size);
    private static string Title(IntPtr hwnd) { var title = new StringBuilder(256); GetWindowText(hwnd, title, title.Capacity); return title.ToString(); }
    public static int Run()
    {
        var chrome = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google/Chrome/Application/chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google/Chrome/Application/chrome.exe")
        }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Chrome not installed for integration check.");
        var directory = Path.Combine(Path.GetTempPath(), "Snip.Chrome.Check-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var output = Path.GetFullPath("artifacts/chrome-check"); Directory.CreateDirectory(output);
        using var diagnostics = new DiagnosticJournal(Path.Combine(output, "logs"));
        Process? browser = null; string stage = "launch";
        try
        {
            var file = Fixture("colored-rows.html");
            var desktop = new DesktopCaptureService(); var display = desktop.GetDisplays().First(d => d.Primary).Bounds;
            var width = Math.Min(900, (int)display.Width - 80); var height = Math.Min(700, (int)display.Height - 80);
            var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
            foreach (var arg in new[] { "--new-window", "--no-first-run", "--disable-background-mode", "--disable-renderer-accessibility", "--disable-gpu", "--user-data-dir=" + Path.Combine(directory, "profile"), $"--window-size={width},{height}", $"--window-position={(int)display.X + 40},{(int)display.Y + 40}", new Uri(file).AbsoluteUri }) start.ArgumentList.Add(arg);
            browser = Process.Start(start)!;
            WindowCaptureInfo? window = null;
            for (int i = 0; i < 300; i++)
            {
                // Chrome may bootstrap a different browser process; identify our unique local page,
                // not the short-lived launcher's PID.
                window = DesktopWindows.Snapshot().FirstOrDefault(w => Title(w.Handle).Contains("Snip scroll fixture", StringComparison.Ordinal));
                if (window is not null) break; Task.Delay(100).GetAwaiter().GetResult();
            }
            if (window is null)
            {
                Console.WriteLine("Launcher exited: " + browser.HasExited);
                foreach (var candidate in DesktopWindows.Snapshot()) Console.WriteLine($"Visible window: PID {candidate.ProcessId} {Title(candidate.Handle)} {candidate.Bounds}");
                throw new InvalidOperationException("Chrome did not create the fixture desktop window.");
            }
            Task.Delay(1500).GetAwaiter().GetResult(); DesktopWindows.Activate(window.Handle);
            var region = new PixelRect(window.Bounds.X + 60, window.Bounds.Y + 180, Math.Min(450, window.Bounds.Width - 100), Math.Min(300, window.Bounds.Height - 220)).RoundOut();
            var export = new ImageExportService(); export.SavePng(desktop.CaptureRegion(region), Path.Combine(output, "initial.png"));
            var service = new BrowserScrollCapture(desktop); using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var progress = new Progress<string>(Console.WriteLine);
            BitmapSource Capture(PixelRect selectedRegion, BrowserCaptureMode mode)
            {
                using var operation = diagnostics.Begin("browser-capture", new { Mode = mode.ToString() });
                try
                {
                    var result = service.CaptureAsync(selectedRegion, progress, cancel.Token, mode, operation).GetAwaiter().GetResult();
                    operation.Complete(new { result.PixelWidth, result.PixelHeight }); return result;
                }
                catch (Exception error) { operation.Fail(error); throw; }
            }
            stage = "colored whole page";
            var entire = Capture(region, BrowserCaptureMode.EntirePage);
            export.SavePng(entire, Path.Combine(output, "entire-page.png"));
            if (entire.PixelHeight < 3500 || entire.PixelHeight > 4000) throw new InvalidOperationException($"Unexpected full page height: {entire.PixelHeight}");
            Console.WriteLine($"PASS real Chrome entire page: {entire.PixelWidth}x{entire.PixelHeight}; ScrollPattern disabled.");
            stage = "visible page";
            var visible = Capture(region, BrowserCaptureMode.VisiblePage);
            export.SavePng(visible, Path.Combine(output, "visible-page.png"));
            if (visible.PixelHeight >= 1000 || visible.PixelWidth != entire.PixelWidth) throw new InvalidOperationException("Visible-page viewport mismatch.");
            Console.WriteLine($"PASS real Chrome visible page: {visible.PixelWidth}x{visible.PixelHeight}");
            stage = "selected region";
            var selected = Capture(region, BrowserCaptureMode.SelectedRegion);
            export.SavePng(selected, Path.Combine(output, "selected-region.png"));
            if (selected.PixelHeight != (int)region.Height || selected.PixelWidth != (int)region.Width) throw new InvalidOperationException("Selected region mismatch.");
            Console.WriteLine($"PASS real Chrome selected region: {selected.PixelWidth}x{selected.PixelHeight}");
            // A normal white article with embedded screenshots and a fixed bottom toolbar.
            var richFile = Fixture("article-fixed-footer.html");
            var richStart = new ProcessStartInfo(chrome) { UseShellExecute = false };
            richStart.ArgumentList.Add("--user-data-dir=" + Path.Combine(directory, "profile")); richStart.ArgumentList.Add(new Uri(richFile).AbsoluteUri);
            using var handoff = Process.Start(richStart);
            WindowCaptureInfo? richWindow = null;
            for (int i = 0; i < 100; i++)
            {
                richWindow = DesktopWindows.Snapshot().FirstOrDefault(w => Title(w.Handle).Contains("Snip rich scroll fixture", StringComparison.Ordinal));
                if (richWindow is not null) break; Task.Delay(100).GetAwaiter().GetResult();
            }
            if (richWindow is null) throw new InvalidOperationException("Rich article window was not created.");
            DesktopWindows.Activate(richWindow.Handle);
            var richRegion = new PixelRect(richWindow.Bounds.X + 80, richWindow.Bounds.Y + 180, 400, 280);
            var ownWindow = new Window { Title = "Capture progress regression", Width = 340, Height = 150,
                Left = richWindow.Bounds.X + 140, Top = richWindow.Bounds.Y + 220, Topmost = true,
                WindowStyle = WindowStyle.None, Background = Brushes.Magenta };
            try
            {
                stage = "own window setup";
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                ownWindow.Show(); desktop.PlaceOverlay(ownWindow, new PixelRect(richWindow.Bounds.X + 140, richWindow.Bounds.Y + 220, 340, 150)); ownWindow.UpdateLayout();
                var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
                if (!ContainsMagenta(desktop.CaptureRegion(richRegion))) throw new InvalidOperationException("Occluding test window was not visible before capture.");
                BitmapSource article;
                using (var hidden = new CaptureWindowScope([ownWindow]))
                {
                    if (ownWindow.IsVisible) throw new InvalidOperationException("Own window was not hidden.");
                    stage = "sparse article with fixed toolbar";
                    article = Capture(richRegion, BrowserCaptureMode.EntirePage);
                }
                if (!ownWindow.IsVisible) throw new InvalidOperationException("Own window was not restored after capture.");
                export.SavePng(article, Path.Combine(output, "rich-article.png"));
                if (ContainsMagenta(article)) throw new InvalidOperationException("Own progress window leaked into capture.");
                if (article.PixelHeight < 4250 || article.PixelHeight > 4400) throw new InvalidOperationException("Rich article stitched height mismatch: " + article.PixelHeight);
                VerifySectionMarkers(article);
                Console.WriteLine($"PASS real Chrome sparse article with image and fixed toolbar: {article.PixelWidth}x{article.PixelHeight}; own topmost window excluded and restored.");
            }
            finally { ownWindow.Close(); }
            stage = "short page";
            var shortStart = new ProcessStartInfo(chrome) { UseShellExecute = false };
            shortStart.ArgumentList.Add("--user-data-dir=" + Path.Combine(directory, "profile")); shortStart.ArgumentList.Add(new Uri(Fixture("short-page.html")).AbsoluteUri);
            using var shortHandoff = Process.Start(shortStart);
            WindowCaptureInfo? shortWindow = null;
            for (int i = 0; i < 100; i++)
            {
                shortWindow = DesktopWindows.Snapshot().FirstOrDefault(w => Title(w.Handle).Contains("Snip short scroll fixture", StringComparison.Ordinal));
                if (shortWindow is not null) break; Task.Delay(100).GetAwaiter().GetResult();
            }
            if (shortWindow is null) throw new InvalidOperationException("Short page window was not created.");
            DesktopWindows.Activate(shortWindow.Handle);
            var shortRegion = new PixelRect(shortWindow.Bounds.X + 80, shortWindow.Bounds.Y + 180, 400, 280);
            var shortPage = Capture(shortRegion, BrowserCaptureMode.EntirePage);
            export.SavePng(shortPage, Path.Combine(output, "short-page.png"));
            if (shortPage.PixelHeight >= 1000 || shortPage.PixelWidth != entire.PixelWidth) throw new InvalidOperationException("Short-page viewport mismatch.");
            Console.WriteLine("PASS real Chrome short page without scrolling.");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("::error::Chrome " + stage + ": " + e.ToString().Replace("\r", "").Replace("\n", "%0A")); Console.Error.WriteLine("FAIL real Chrome: " + e); return 1; }
        finally
        {
            try { if (browser is not null && !browser.HasExited) { browser.Kill(true); browser.WaitForExit(5000); } } catch { }
            browser?.Dispose();
            try { Directory.Delete(directory, true); } catch { }
        }
    }
    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "scrolling", name);
        if (!File.Exists(path)) throw new FileNotFoundException("Missing versioned scrolling fixture", name);
        return path;
    }
    private static void VerifySectionMarkers(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        var pixels = new byte[width * height * 4]; converted.CopyPixels(pixels, width * 4, 0);
        foreach (string color in new[] { "E31729", "27A453", "3763D8", "BB7321", "8742B6", "1C9AAB", "A84C65", "567E32" })
        {
            int rgb = Convert.ToInt32(color, 16), groups = 0; bool previous = false;
            for (int y = 0; y < height; y++)
            {
                bool found = false;
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    if (pixels[i + 2] == (rgb >> 16) && pixels[i + 1] == ((rgb >> 8) & 255) && pixels[i] == (rgb & 255)) { found = true; break; }
                }
                if (found && !previous) groups++; previous = found;
            }
            if (groups != 1) throw new InvalidOperationException($"Article section marker {color} appears {groups} times; lost or duplicated section.");
        }
    }
    private static bool ContainsMagenta(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] > 250 && pixels[i + 1] < 10 && pixels[i + 2] > 250) return true;
        return false;
    }

}
