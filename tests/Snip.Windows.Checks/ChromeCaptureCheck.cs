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
using System.Windows.Controls;
using Snip.App.ViewModels;
using Snip.App.Views;

namespace Snip.Windows.Checks;

internal static class ChromeCaptureCheck
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder title, int size);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    private static string Title(IntPtr hwnd) { var title = new StringBuilder(256); GetWindowText(hwnd, title, title.Capacity); return title.ToString(); }
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var chrome = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google/Chrome/Application/chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google/Chrome/Application/chrome.exe")
        }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Chrome not installed for integration check.");
        var directory = Path.Combine(Path.GetTempPath(), "Snip.Chrome.Check-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var output = Path.GetFullPath("artifacts/chrome-check"); Directory.CreateDirectory(output);
        using var diagnostics = new DiagnosticJournal(Path.Combine(output, "logs"));
        Process? browser = null; string stage = "launch";
        using var watchdog = new System.Threading.Timer(_ => { Console.WriteLine("::error title=Chrome capture deadline::" + Volatile.Read(ref stage)); Environment.Exit(1); }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
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
            DesktopWindows.Activate(window.Handle);
            var region = new PixelRect(window.Bounds.X + 60, window.Bounds.Y + 180, Math.Min(450, window.Bounds.Width - 100), Math.Min(300, window.Bounds.Height - 220)).RoundOut();
            var export = new ImageExportService(); export.SavePng(WaitForColoredFixture(desktop, region), Path.Combine(output, "initial.png"));
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
            stage = "physical browser target with transparent overlay";
            BrowserTargetChecks.Verify(window, region, () =>
            {
                var throughOverlay = Capture(region, BrowserCaptureMode.FromCurrentPage);
                if (throughOverlay.PixelHeight < 160 || throughOverlay.PixelWidth < 80)
                    throw new InvalidOperationException("Physical browser viewport capture is empty.");
            });
            stage = "colored whole page";
            var entire = Capture(region, BrowserCaptureMode.EntirePage);
            export.SavePng(entire, Path.Combine(output, "entire-page.png"));
            if (entire.PixelHeight < 3500 || entire.PixelHeight > 4000) throw new InvalidOperationException($"Unexpected full page height: {entire.PixelHeight}");
            Console.WriteLine($"PASS real Chrome entire page: {entire.PixelWidth}x{entire.PixelHeight}; ScrollPattern disabled.");
            stage = "current viewport from middle to bottom";
            Wheel(region, 12000); Task.Delay(650).GetAwaiter().GetResult();
            var atTopPixels = Pixels(desktop.CaptureRegion(region));
            Wheel(region, -600); Task.Delay(650).GetAwaiter().GetResult();
            if (atTopPixels.SequenceEqual(Pixels(desktop.CaptureRegion(region)))) throw new InvalidOperationException("Native test input did not move the fixture away from the top before current-page capture.");
            var suffix = Capture(region, BrowserCaptureMode.FromCurrentPage);
            export.SavePng(suffix, Path.Combine(output, "current-to-bottom.png"));
            if (suffix.PixelWidth != entire.PixelWidth || suffix.PixelHeight >= entire.PixelHeight - 100 || suffix.PixelHeight < 1000)
                throw new InvalidOperationException($"Current-page capture restarted at the top or lost the remaining document: whole={entire.PixelWidth}x{entire.PixelHeight}, suffix={suffix.PixelWidth}x{suffix.PixelHeight}.");
            Console.WriteLine($"PASS real Chrome from current viewport to bottom: {suffix.PixelWidth}x{suffix.PixelHeight}; excludes content above.");
            stage = "current viewport already at bottom";
            // Chrome can expose a ScrollPattern on demand even with renderer accessibility disabled.
            // Capture then restores the original position, so explicitly place this fixture at the bottom.
            Wheel(region, -12000); Task.Delay(650).GetAwaiter().GetResult();
            var bottom = Capture(region, BrowserCaptureMode.FromCurrentPage);
            if (bottom.PixelHeight >= 1000 || bottom.PixelWidth != entire.PixelWidth) throw new InvalidOperationException("At-bottom current capture must return the visible viewport.");
            Console.WriteLine("PASS current mode at page bottom returns one viewport without failure.");
            stage = "real dragging at page edge and release";
            Wheel(region, 12000); Task.Delay(650).GetAwaiter().GetResult();
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                var renderer = new AnnotationRenderer(); using var pins = new PinManager(export);
                var features = new FeatureHub(renderer, export, desktop, pins, diagnostics);
                EditorViewModel Original() => new(desktop.CaptureRegion(region), renderer, export, new EditorDialogs(() => null));
                var entryClock = Stopwatch.StartNew();
                var task = features.HandleAsync(new(Original(), region, SelectionAction.Scroll, BrowserCaptureMode.ManualScroll));
                TextWindowChecks.PumpUntil(() => task.IsCompleted || app.Windows.OfType<ManualScrollCaptureWindow>().Any(w => ((ManualScrollCaptureViewModel)w.DataContext).Ready), TimeSpan.FromSeconds(12));
                double readyMs = entryClock.Elapsed.TotalMilliseconds;
                if (task.IsCompleted) { task.GetAwaiter().GetResult(); throw new InvalidOperationException("Drag capture returned before mouse input."); }
                var toolbar = app.Windows.OfType<ManualScrollCaptureWindow>().Single();
                ((Border)toolbar.Content).Background = Brushes.Magenta; toolbar.UpdateLayout();
                if (DesktopWindows.Foreground != window.Handle) throw new InvalidOperationException("Manual controls stole browser focus.");
                var bounds = toolbar.PointToScreen(new Point());
                var end = toolbar.PointToScreen(new Point(toolbar.ActualWidth, toolbar.ActualHeight));
                if (!new PixelRect(bounds.X, bounds.Y, end.X - bounds.X, end.Y - bounds.Y).Intersect(region).IsEmpty)
                    throw new InvalidOperationException("Manual controls overlap the captured rectangle.");
                var viewport = toolbar.Viewport;
                var startPoint = new Point(viewport.X + 80, viewport.Y + 80);
                var edgePoint = new Point(viewport.X + 400, viewport.Bottom - 10);
                Move(startPoint);
                Send([new() { Type = 0, Data = new() { Mouse = new() { Flags = 2 } } }]);
                var vm = (ManualScrollCaptureViewModel)toolbar.DataContext;
                TextWindowChecks.PumpUntil(() => vm.Drag!.Started, TimeSpan.FromSeconds(3));
                int pulses = 0; double maxGap = 0; var pulseClock = Stopwatch.StartNew();
                var pulse = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
                pulse.Tick += (_, _) => { pulses++; maxGap = Math.Max(maxGap, pulseClock.Elapsed.TotalMilliseconds); pulseClock.Restart(); };
                var moveClock = Stopwatch.StartNew(); pulse.Start();
                try
                {
                    var moving = Task.Run(async () =>
                    {
                        for (int i = 0; i < 80; i++) { Move(new(startPoint.X + 40 + i % 120, startPoint.Y + 40 + i % 40)); await Task.Delay(5); }
                    });
                    TextWindowChecks.PumpUntil(() => moving.IsCompleted, TimeSpan.FromSeconds(6)); moving.GetAwaiter().GetResult();
                }
                finally { pulse.Stop(); maxGap = Math.Max(maxGap, pulseClock.Elapsed.TotalMilliseconds); }
                if (pulses == 0) throw new InvalidOperationException("Manual native pointer input starved the UI dispatcher.");
                var responsiveness = new { ReadyMilliseconds = Math.Round(readyMs, 2), NativeMoves = 80, MoveMilliseconds = Math.Round(moveClock.Elapsed.TotalMilliseconds, 2), UiPulses = pulses, MaxUiPulseGapMilliseconds = Math.Round(maxGap, 2) };
                Directory.CreateDirectory("artifacts/validation"); File.WriteAllText("artifacts/validation/manual-chrome-responsiveness.json", System.Text.Json.JsonSerializer.Serialize(responsiveness, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("PASS real manual Chrome entry and 80 native pointer moves with responsive dispatcher: " + System.Text.Json.JsonSerializer.Serialize(responsiveness));
                Move(edgePoint);
                TextWindowChecks.PumpUntil(() => vm.Drag!.AtLowerEdge, TimeSpan.FromSeconds(3));
                Console.WriteLine("PASS native drag down and move reach lower edge before scrolling.");
                TextWindowChecks.PumpUntil(() => task.IsCompleted || vm.Frames >= 3, TimeSpan.FromSeconds(12));
                if (task.IsCompleted) { task.GetAwaiter().GetResult(); throw new InvalidOperationException("Drag capture stopped before release."); }
                var releasePoint = new Point(edgePoint.X, viewport.Y + 200);
                Move(releasePoint);
                Send([new() { Type = 0, Data = new() { Mouse = new() { Flags = 4 } } }]);
                TextWindowChecks.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(12)); task.GetAwaiter().GetResult();
                if (app.Windows.OfType<ManualScrollCaptureWindow>().Any()) throw new InvalidOperationException("Drag controls were not closed after release.");
                var editor = app.Windows.OfType<EditorWindow>().Single();
                var manual = ((EditorViewModel)editor.DataContext).Compose(); export.SavePng(manual, Path.Combine(output, "manual-region.png"));
                if (manual.PixelWidth != 320 || manual.PixelHeight <= 200 || manual.PixelHeight >= 2000)
                    throw new InvalidOperationException("Drag selection ignored the selected coordinates or captured the whole document.");
                if (ContainsMagenta(manual)) throw new InvalidOperationException("Drag controls were photographed into the result.");
                BitmapSource finalStrip;
                using (var hiddenEditor = new CaptureWindowScope([editor]))
                    finalStrip = desktop.CaptureRegion(new(startPoint.X, releasePoint.Y - 50, 320, 50));
                var a = new CroppedBitmap(manual, new Int32Rect(0, manual.PixelHeight - 50, manual.PixelWidth, 50));
                var aBytes = new byte[320 * 50 * 4]; var bBytes = new byte[aBytes.Length]; a.CopyPixels(aBytes, 320 * 4, 0); finalStrip.CopyPixels(bBytes, 320 * 4, 0);
                if (!aBytes.SequenceEqual(bBytes)) throw new InvalidOperationException("Drag screenshot did not end at the released document point or includes its border.");
                editor.Close(); Console.WriteLine($"PASS real drag Chrome capture: {manual.PixelWidth}x{manual.PixelHeight}; native left drag, edge auto scroll, release, exact final strip, controls excluded.");
                stage = "manual native Esc cancellation";
                var cancelTask = features.HandleAsync(new(Original(), region, SelectionAction.Scroll, BrowserCaptureMode.ManualScroll));
                TextWindowChecks.PumpUntil(() => cancelTask.IsCompleted || app.Windows.OfType<ManualScrollCaptureWindow>().Any(w => ((ManualScrollCaptureViewModel)w.DataContext).Ready), TimeSpan.FromSeconds(12));
                Key(0x1B);
                TextWindowChecks.PumpUntil(() => cancelTask.IsCompleted, TimeSpan.FromSeconds(8)); cancelTask.GetAwaiter().GetResult();
                if (app.Windows.OfType<ManualScrollCaptureWindow>().Any()) throw new InvalidOperationException("Esc left manual capture controls running.");
                foreach (var restored in app.Windows.OfType<EditorWindow>().ToArray()) restored.Close();
                Console.WriteLine("PASS real native Esc cancels manual capture and closes controls/hooks.");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            stage = "selected text, sidebar and native drag gesture";
            var selectedStart = new ProcessStartInfo(chrome) { UseShellExecute = false };
            selectedStart.ArgumentList.Add("--user-data-dir=" + Path.Combine(directory, "profile")); selectedStart.ArgumentList.Add(new Uri(Fixture("selected-text-sidebar.html")).AbsoluteUri);
            using var selectedHandoff = Process.Start(selectedStart);
            WindowCaptureInfo? selectedWindow = null;
            for (int i = 0; i < 100; i++)
            {
                selectedWindow = DesktopWindows.Snapshot().FirstOrDefault(w => Title(w.Handle).Contains("Snip selected drag fixture", StringComparison.Ordinal));
                if (selectedWindow is not null) break; Task.Delay(100).GetAwaiter().GetResult();
            }
            if (selectedWindow is null) throw new InvalidOperationException("Selected text fixture did not load.");
            DesktopWindows.Activate(selectedWindow.Handle); Task.Delay(650).GetAwaiter().GetResult();
            var selectedHit = new PixelRect(selectedWindow.Bounds.X + 80, selectedWindow.Bounds.Y + 180, 400, 280);
            Wheel(selectedHit, 12000); Task.Delay(650).GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                var renderer = new AnnotationRenderer(); using var pins = new PinManager(export);
                var features = new FeatureHub(renderer, export, desktop, pins, diagnostics);
                var selectedRegion = new PixelRect(selectedWindow.Bounds.X + 80, selectedWindow.Bounds.Y + 180, 400, 280);
                var original = new EditorViewModel(desktop.CaptureRegion(selectedRegion), renderer, export, new EditorDialogs(() => null));
                var captureTask = features.HandleAsync(new(original, selectedRegion, SelectionAction.Scroll, BrowserCaptureMode.ManualScroll));
                TextWindowChecks.PumpUntil(() => captureTask.IsCompleted || app.Windows.OfType<ManualScrollCaptureWindow>().Any(w => ((ManualScrollCaptureViewModel)w.DataContext).Ready), TimeSpan.FromSeconds(12));
                if (captureTask.IsCompleted) throw new InvalidOperationException("Selected fixture capture stopped before dragging.");
                var bar = app.Windows.OfType<ManualScrollCaptureWindow>().Single(); var viewport = bar.Viewport;
                int Hit(double x, double y)
                {
                    var previousDpi = SetThreadDpiAwarenessContext(new(-4));
                    if (previousDpi == IntPtr.Zero) throw new InvalidOperationException("Cannot set native fixture DPI context.");
                    try
                    {
                        if (SendMessageTimeout(selectedWindow.Handle, 0x0084, IntPtr.Zero, new(((int)y & 65535) << 16 | (int)x & 65535), 2, 500, out var result) == IntPtr.Zero) throw new InvalidOperationException("Browser native hit test failed.");
                        return result.ToInt32();
                    }
                    finally { SetThreadDpiAwarenessContext(previousDpi); }
                }
                Console.WriteLine($"::notice title=Edge wheel hit test::old-point={Hit(viewport.X + 385, viewport.Bottom - 4)}; new-point={Hit(viewport.X + 385, viewport.Y + viewport.Height / 2)}; HTCLIENT=1; HTBOTTOM=15");
                ((Border)bar.Content).Background = Brushes.Magenta;
                Move(new(viewport.X + 65, viewport.Y + 65));
                Send([new() { Type = 0, Data = new() { Mouse = new() { Flags = 2 } } }]);
                TextWindowChecks.PumpUntil(() => ((ManualScrollCaptureViewModel)bar.DataContext).Drag!.Started, TimeSpan.FromSeconds(3));
                Move(new(viewport.X + 385, viewport.Bottom + 8));
                TextWindowChecks.PumpUntil(() => captureTask.IsCompleted || ((ManualScrollCaptureViewModel)bar.DataContext).Frames >= 4, TimeSpan.FromSeconds(12));
                if (captureTask.IsCompleted) throw new InvalidOperationException("Selected text fixture failed while dragging.");
                Move(new(viewport.X + 385, viewport.Y + 250));
                Send([new() { Type = 0, Data = new() { Mouse = new() { Flags = 4 } } }]);
                TextWindowChecks.PumpUntil(() => captureTask.IsCompleted, TimeSpan.FromSeconds(12)); captureTask.GetAwaiter().GetResult();
                var resultEditor = app.Windows.OfType<EditorWindow>().Single();
                var result = ((EditorViewModel)resultEditor.DataContext).Compose(); export.SavePng(result, Path.Combine(output,"selected-drag.png"));
                if (result.PixelWidth != 320 || result.PixelHeight <= 300 || ContainsMagenta(result)) throw new InvalidOperationException("Selected text drag produced incorrect pixels or included controls.");
                if (Title(selectedWindow.Handle).Contains("leaked mouse input", StringComparison.Ordinal)) throw new InvalidOperationException("Screenshot drag leaked into browser text selection.");
                resultEditor.Close(); Console.WriteLine($"PASS selected text + fixed sidebar + footer native drag: {result.PixelWidth}x{result.PixelHeight}; mouse gesture consumed before browser selection.");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
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
        catch (Exception e)
        {
            // Only public fixtures and the journal's technical, content-free fields are emitted.
            foreach (var bar in app.Windows.OfType<ManualScrollCaptureWindow>())
            {
                var vm = (ManualScrollCaptureViewModel)bar.DataContext;
                Console.WriteLine($"::notice title=Browser drag state::frames={vm.Frames}; started={vm.Drag?.Started}; dragging={vm.Drag?.Dragging}; edge={vm.Drag?.AtLowerEdge}; offset={vm.Drag?.ScrollOffset}; viewport={bar.Viewport}");
            }
            diagnostics.FlushAsync().GetAwaiter().GetResult();
            var journal = Path.Combine(diagnostics.DirectoryPath, "events.jsonl");
            if (File.Exists(journal)) foreach (var line in File.ReadLines(journal).TakeLast(8).Reverse()) Console.WriteLine("::notice title=Browser phase::" + line.Replace("%", "%25"));
            Console.WriteLine("::error::Chrome " + stage + ": " + e.ToString().Replace("\r", "").Replace("\n", "%0A")); Console.Error.WriteLine("FAIL real Chrome: " + e); return 1;
        }
        finally
        {
            try { if (browser is not null && !browser.HasExited) { browser.Kill(true); browser.WaitForExit(5000); } } catch { }
            browser?.Dispose();
            try { Directory.Delete(directory, true); } catch { }
        }
    }
    private static BitmapSource WaitForColoredFixture(IDesktopCaptureService capture, PixelRect region)
    {
        // A Chrome title can precede the first renderer paint, especially on a loaded runner.
        // Verify the public fixture's actual pixels rather than treating a fixed delay as readiness.
        var palette = Enumerable.Range(0, 50).Select(i => ((80 + i * 17 % 160) << 16) | ((80 + i * 29 % 160) << 8) | (80 + i * 43 % 160)).ToHashSet();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            var image = capture.CaptureRegion(region);
            var pixels = Pixels(image); var visible = new HashSet<int>();
            for (int y = 0; y < image.PixelHeight; y += 9)
            for (int x = 0; x < image.PixelWidth; x += 13)
            {
                int index = (y * image.PixelWidth + x) * 4;
                int rgb = (pixels[index + 2] << 16) | (pixels[index + 1] << 8) | pixels[index];
                if (palette.Contains(rgb)) visible.Add(rgb);
            }
            if (visible.Count >= 3)
            {
                Console.WriteLine($"PASS Chrome renderer readiness: {visible.Count} public fixture row colors visible after {clock.ElapsedMilliseconds} ms.");
                return image;
            }
            Task.Delay(100).GetAwaiter().GetResult();
        }
        throw new InvalidOperationException("Chrome fixture title appeared but its colored page did not paint within 15 seconds.");
    }
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Wheel, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    private static void Wheel(PixelRect region, int delta)
    {
        Move(new(region.X + region.Width / 2, region.Y + region.Height / 2));
        Send([new() { Type = 0, Data = new() { Mouse = new() { Wheel = unchecked((uint)delta), Flags = 0x0800 } } }]);
    }
    private static void Click(Point point)
    {
        SetPhysicalCursorPos((int)point.X, (int)point.Y);
        Send([new() { Type = 0, Data = new() { Mouse = new() { Flags = 0x0002 } } }, new() { Type = 0, Data = new() { Mouse = new() { Flags = 0x0004 } } }]);
    }
    private static void Move(Point point)
    {
        var displays = new DesktopCaptureService().GetDisplays();
        double left = displays.Min(d => d.Bounds.X), top = displays.Min(d => d.Bounds.Y);
        double width = displays.Max(d => d.Bounds.Right) - left, height = displays.Max(d => d.Bounds.Bottom) - top;
        Send([new() { Type = 0, Data = new() { Mouse = new() { X = Math.Clamp((int)Math.Ceiling((point.X - left) * 65536 / width), 0, 65535), Y = Math.Clamp((int)Math.Ceiling((point.Y - top) * 65536 / height), 0, 65535), Flags = 0xC001 } } }]);
        // Pump the actual low-level hook, rather than assuming SetPhysicalCursorPos injects a move.
        var paint = Task.Delay(40); TextWindowChecks.PumpUntil(() => paint.IsCompleted, TimeSpan.FromSeconds(2));
    }
    private static void Key(ushort key) => Send([new() { Type = 1, Data = new() { Keyboard = new() { Key = key } } }, new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = 2 } } }]);
    private static void Send(Input[] inputs)
    { if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length) throw new InvalidOperationException("Fixture browser input was rejected."); }
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
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0); return pixels;
    }

}
