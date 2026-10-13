using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Snip.Core;
using Snip.Infrastructure;

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
        Process? browser = null;
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
            var entire = service.CaptureAsync(region, progress, cancel.Token, BrowserCaptureMode.EntirePage).GetAwaiter().GetResult();
            export.SavePng(entire, Path.Combine(output, "entire-page.png"));
            if (entire.PixelHeight < 3500 || entire.PixelHeight > 4000) throw new InvalidOperationException($"Unexpected full page height: {entire.PixelHeight}");
            Console.WriteLine($"PASS real Chrome entire page: {entire.PixelWidth}x{entire.PixelHeight}; ScrollPattern disabled.");
            var visible = service.CaptureAsync(region, progress, cancel.Token, BrowserCaptureMode.VisiblePage).GetAwaiter().GetResult();
            export.SavePng(visible, Path.Combine(output, "visible-page.png"));
            if (visible.PixelHeight >= 1000 || visible.PixelWidth != entire.PixelWidth) throw new InvalidOperationException("Visible-page viewport mismatch.");
            Console.WriteLine($"PASS real Chrome visible page: {visible.PixelWidth}x{visible.PixelHeight}");
            var selected = service.CaptureAsync(region, progress, cancel.Token, BrowserCaptureMode.SelectedRegion).GetAwaiter().GetResult();
            export.SavePng(selected, Path.Combine(output, "selected-region.png"));
            if (selected.PixelHeight != (int)region.Height || selected.PixelWidth != (int)region.Width) throw new InvalidOperationException("Selected region mismatch.");
            Console.WriteLine($"PASS real Chrome selected region: {selected.PixelWidth}x{selected.PixelHeight}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL real Chrome: " + e); return 1; }
        finally
        {
            try { if (browser is not null && !browser.HasExited) { browser.Kill(true); browser.WaitForExit(5000); } } catch { }
            browser?.Dispose();
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
