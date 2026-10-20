using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Snip.App.Services;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class SmartBrowserChecks
{
    private delegate bool ChildCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, ChildCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    internal static void Verify()
    {
        string chrome = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Select(p => Path.Combine(p, "Google/Chrome/Application/chrome.exe")).First(File.Exists);
        string directory = Path.Combine(Path.GetTempPath(), "Snip.SmartBrowser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "index.html");
        File.WriteAllText(file, """
            <!doctype html><meta charset="utf-8"><title>Snip smart region fixture</title>
            <style>*{box-sizing:border-box}body{margin:0;background:#eef7f4}button{position:fixed;left:80px;top:60px;width:220px;height:80px;background:#087f65;color:white;border:0;font-size:20px}section{position:fixed;left:80px;top:180px;width:420px;height:200px;background:white;border:2px solid #087f65}</style>
            <button>Public capture action</button><section role="region" aria-label="Public browser panel"><h2>Public panel</h2><ul><li>Public row one</li><li>Public row two</li></ul></section>
            """);
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
        foreach (string argument in new[] { "--new-window", "--no-first-run", "--disable-background-mode", "--force-renderer-accessibility", "--force-device-scale-factor=1", "--disable-gpu",
            "--user-data-dir=" + Path.Combine(directory, "profile"), "--window-size=850,650", "--window-position=40,40", new Uri(file).AbsoluteUri }) start.ArgumentList.Add(argument);
        using var launcher = Process.Start(start)!;
        WindowCaptureInfo? browser = null;
        SelectionSession? session = null;
        try
        {
            var clock = Stopwatch.StartNew();
            while (browser is null && clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                browser = DesktopWindows.Snapshot(includeCurrentProcess: true).FirstOrDefault(w =>
                {
                    var title = new StringBuilder(128); GetWindowText(w.Handle, title, title.Capacity); return title.ToString().Contains("Snip smart region fixture", StringComparison.Ordinal);
                });
                Pause(50);
            }
            Require(browser is not null, "Chrome did not open the public smart-selection fixture.");
            DesktopWindows.Activate(browser!.Handle);
            PixelRect viewport = default;
            ChildCallback callback = (child, _) =>
            {
                var name = new StringBuilder(128); GetClassName(child, name, name.Capacity);
                if (name.ToString() == "Chrome_RenderWidgetHostHWND" && GetWindowRect(child, out var rect))
                {
                    var r = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                    if (r.Width * r.Height > viewport.Width * viewport.Height) viewport = r;
                }
                return true;
            };
            EnumChildWindows(browser.Handle, callback, IntPtr.Zero);
            Require(!viewport.IsEmpty, "Chrome native viewport was not available for physical-coordinate fixture points.");
            var desktop = new DesktopCaptureService();
            TextWindowChecks.PumpUntil(() =>
            {
                var pixel = desktop.CaptureRegion(new(viewport.X + 88, viewport.Y + 68, 1, 1));
                byte[] bytes = new byte[4]; pixel.CopyPixels(bytes, 4, 0);
                return Math.Abs(bytes[0] - 0x65) <= 2 && Math.Abs(bytes[1] - 0x7f) <= 2 && Math.Abs(bytes[2] - 0x08) <= 2;
            }, TimeSpan.FromSeconds(7));
            var frame = desktop.Capture();
            var buttonPoint = new PixelPoint(viewport.X + 190, viewport.Y + 100);
            Require(SetPhysicalCursorPos((int)buttonPoint.X, (int)buttonPoint.Y), "Could not move over the browser button.");
            var export = new Export();
            session = new SelectionSession(frame, desktop, new AnnotationRenderer(), export, [browser],
                () => new HoverRegionProbe(() => new ProcessStartInfo(Path.GetFullPath("src/Snip.App/bin/Release/net8.0-windows10.0.19041.0/Snip.exe"))));
            session.Show();
            session.ViewModel.Changed += (_, _) => Console.WriteLine("Public Chrome hover bounds: " + session.ViewModel.DisplayedSelection);
            var expectedButton = new PixelRect(viewport.X + 80, viewport.Y + 60, 220, 80);
            TextWindowChecks.PumpUntil(() => session.ViewModel.DisplayedSelection == expectedButton, TimeSpan.FromSeconds(7));
            Console.WriteLine("PASS real Chrome accessibility button selection under a frozen overlay: " + session.ViewModel.DisplayedSelection);
            var panelPoint = new PixelPoint(viewport.X + 450, viewport.Y + 340);
            SetPhysicalCursorPos((int)panelPoint.X, (int)panelPoint.Y);
            var expectedPanel = new PixelRect(viewport.X + 80, viewport.Y + 180, 420, 200);
            TextWindowChecks.PumpUntil(() => session.ViewModel.DisplayedSelection == expectedPanel, TimeSpan.FromSeconds(7));
            var selected = session.ViewModel.DisplayedSelection;
            HotkeyChecks.Press(0x0D); TextWindowChecks.PumpUntil(() => session.Completion.IsCompleted && Clipboard.ContainsImage(), TimeSpan.FromSeconds(5));
            var actual = Clipboard.GetImage();
            var wanted = new CroppedBitmap(frame.Image, new Int32Rect((int)(selected.X - frame.Bounds.X), (int)(selected.Y - frame.Bounds.Y), (int)selected.Width, (int)selected.Height));
            byte[] a = new byte[actual.PixelWidth * actual.PixelHeight * 4], b = new byte[wanted.PixelWidth * wanted.PixelHeight * 4];
            actual.CopyPixels(a, actual.PixelWidth * 4, 0); wanted.CopyPixels(b, wanted.PixelWidth * 4, 0);
            Require(actual.PixelWidth == wanted.PixelWidth && actual.PixelHeight == wanted.PixelHeight && a.SequenceEqual(b) && export.Copies == 1, "Chrome smart selection Enter copied a different region.");
            Console.WriteLine("PASS real Chrome panel selection and Enter copy: exact original frozen page pixels; no browser extension or model.");
        }
        finally
        {
            session?.Dispose();
            try
            {
                using var process = browser is not null ? Process.GetProcessById(browser.ProcessId) : null;
                if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
    private static void Pause(int milliseconds) { var task = Task.Delay(milliseconds); TextWindowChecks.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(2)); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Export : IImageExportService
    {
        public int Copies;
        public void SavePng(BitmapSource image, string path) => throw new InvalidOperationException("No save dialog expected.");
        public Task CopyAsync(BitmapSource image) { Copies++; return new ImageExportService().CopyAsync(image); }
    }
}
