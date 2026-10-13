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
        Process? browser = null; string stage = "launch";
        try
        {
            var html = new StringBuilder("<!doctype html><meta charset='utf-8'><title>Snip scroll fixture</title><style>body{margin:0;font:22px Arial}.row{height:73px;box-sizing:border-box;border-bottom:2px solid #333;display:flex;align-items:center;padding:0 30px}</style>");
            var random = new Random(28);
            for (int i = 0; i < 50; i++) html.Append($"<div class='row' style='background:rgb({random.Next(80, 240)},{random.Next(80, 240)},{random.Next(80, 240)})'>Row {i:D3} · Chrome native scrolling fixture · {random.Next()}</div>");
            var file = Path.Combine(directory, "page.html"); File.WriteAllText(file, html.ToString());
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
            stage = "colored whole page";
            var entire = service.CaptureAsync(region, progress, cancel.Token, BrowserCaptureMode.EntirePage).GetAwaiter().GetResult();
            export.SavePng(entire, Path.Combine(output, "entire-page.png"));
            if (entire.PixelHeight < 3500 || entire.PixelHeight > 4000) throw new InvalidOperationException($"Unexpected full page height: {entire.PixelHeight}");
            Console.WriteLine($"PASS real Chrome entire page: {entire.PixelWidth}x{entire.PixelHeight}; ScrollPattern disabled.");
            stage = "visible page";
            var visible = service.CaptureAsync(region, progress, cancel.Token, BrowserCaptureMode.VisiblePage).GetAwaiter().GetResult();
            export.SavePng(visible, Path.Combine(output, "visible-page.png"));
            if (visible.PixelHeight >= 1000 || visible.PixelWidth != entire.PixelWidth) throw new InvalidOperationException("Visible-page viewport mismatch.");
            Console.WriteLine($"PASS real Chrome visible page: {visible.PixelWidth}x{visible.PixelHeight}");
            stage = "selected region";
            var selected = service.CaptureAsync(region, progress, cancel.Token, BrowserCaptureMode.SelectedRegion).GetAwaiter().GetResult();
            export.SavePng(selected, Path.Combine(output, "selected-region.png"));
            if (selected.PixelHeight != (int)region.Height || selected.PixelWidth != (int)region.Width) throw new InvalidOperationException("Selected region mismatch.");
            Console.WriteLine($"PASS real Chrome selected region: {selected.PixelWidth}x{selected.PixelHeight}");
            // A normal white article with embedded screenshots and a fixed bottom toolbar.
            var rich = new StringBuilder("<!doctype html><meta charset='utf-8'><title>Snip rich scroll fixture</title><style>body{margin:0;background:#eee;font:16px Arial}.article{width:760px;margin:auto;background:white}.section{height:540px;box-sizing:border-box;padding:20px 24px}h2{margin:0;height:30px;font:20px Arial}p{margin:5px 0;line-height:20px;height:60px}.bar{position:fixed;bottom:12px;left:35%;background:#555;color:white;padding:10px;font:14px Arial}</style><div class='article'>");
            for (int section = 0; section < 8; section++)
            {
                rich.Append($"<div class='section'><h2>Article section {section:D2} - unique instruction {section * 7919}</h2><svg width='620' height='400' xmlns='http://www.w3.org/2000/svg'><rect width='620' height='400' fill='white'/><rect x='4' y='4' width='108' height='392' fill='#30475c'/>");
                for (int row = 0; row < 9; row++) rich.Append($"<text x='16' y='{35 + row * 38}' fill='white' font-size='13'>Menu {section}:{row} item</text>");
                rich.Append($"<text x='140' y='25' font-size='12'>Order {section * 371 + 27} | Amount {section * 17 + 6}</text><path d='M 170 35 L 205 170 L 196 150 M 205 170 L 208 146' stroke='red' stroke-width='3' fill='none'/></svg><p>Step {section + 1}: update the selected record and notify the responsible team.<br>Document {section * 8317 + 93}: confirm the title and recipients.<br>These instructions are local test data.</p></div>");
            }
            rich.Append("</div><div class='bar'>Return · Approve · Examples · Create task</div>");
            var richFile = Path.Combine(directory, "rich.html"); File.WriteAllText(richFile, rich.ToString());
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
                System.Windows.Interop.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                ownWindow.Show(); desktop.PlaceOverlay(ownWindow, new PixelRect(richWindow.Bounds.X + 140, richWindow.Bounds.Y + 220, 340, 150)); ownWindow.UpdateLayout();
                var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
                if (!ContainsMagenta(desktop.CaptureRegion(richRegion))) throw new InvalidOperationException("Occluding test window was not visible before capture.");
                BitmapSource article;
                using (var hidden = new CaptureWindowScope([ownWindow]))
                {
                    if (ownWindow.IsVisible) throw new InvalidOperationException("Own window was not hidden.");
                    stage = "sparse article with fixed toolbar";
                    article = service.CaptureAsync(richRegion, progress, cancel.Token, BrowserCaptureMode.EntirePage).GetAwaiter().GetResult();
                }
                if (!ownWindow.IsVisible) throw new InvalidOperationException("Own window was not restored after capture.");
                export.SavePng(article, Path.Combine(output, "rich-article.png"));
                if (ContainsMagenta(article)) throw new InvalidOperationException("Own progress window leaked into capture.");
                if (article.PixelHeight < 4250 || article.PixelHeight > 4400) throw new InvalidOperationException("Rich article stitched height mismatch: " + article.PixelHeight);
                Console.WriteLine($"PASS real Chrome sparse article with image and fixed toolbar: {article.PixelWidth}x{article.PixelHeight}; own topmost window excluded and restored.");
            }
            finally { ownWindow.Close(); }
            return 0;
        }
        catch (Exception e) { Console.WriteLine("::error::Chrome " + stage + ": " + e.Message.Replace("\n", " ").Replace("\r", " ")); Console.Error.WriteLine("FAIL real Chrome: " + e); return 1; }
        finally
        {
            try { if (browser is not null && !browser.HasExited) { browser.Kill(true); browser.WaitForExit(5000); } } catch { }
            browser?.Dispose();
            try { Directory.Delete(directory, true); } catch { }
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
