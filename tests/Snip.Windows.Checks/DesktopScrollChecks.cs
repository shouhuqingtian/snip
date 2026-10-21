using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

/// <summary>Actual external WPF scrolling windows, physical wheel/button/key input and production FeatureHub.</summary>
internal static class DesktopScrollChecks
{
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Data; }
    [StructLayout(LayoutKind.Explicit)] private struct Union
    { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Wheel, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    private sealed record FixtureState(long Handle, PixelRect Region, double Offset);

    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string stage = "initialization";
        using var deadline = new System.Threading.Timer(_ => { Console.Error.WriteLine("FAIL desktop scroll deadline: " + Volatile.Read(ref stage)); Environment.Exit(1); }, null, TimeSpan.FromSeconds(100), Timeout.InfiniteTimeSpan);
        string folder = Path.Combine(Path.GetTempPath(), "Snip.DesktopScroll-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        using var journal = new DiagnosticJournal(Path.Combine(folder, "logs"));
        var desktop = new CountingCapture(); var export = new ImageExportService(); var renderer = new AnnotationRenderer();
        using var pins = new PinManager(export, () => { });
        Exception? failure = null;
        var features = new FeatureHub(renderer, export, desktop, pins, journal, captureFailure: error => failure = error);
        int pulses = 0; double maxGap = 0; var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { pulses++; maxGap = Math.Max(maxGap, clock.Elapsed.TotalMilliseconds); clock.Restart(); };
        try
        {
            Ui.EnsureTheme();
            foreach (string kind in new[] { "table", "chat" })
            {
                stage = kind + " external window";
                using var fixture = new Fixture(folder, kind);
                var state = fixture.State(); var region = state.Region;
                var target = new IntPtr(state.Handle);
                Require(DesktopWindows.Snapshot().Any(w => w.Handle == target), "External desktop fixture not found independently of current process.");
                DesktopWindows.Activate(target); Pause(160);
                var original = new EditorViewModel(desktop.CaptureRegion(region), renderer, export, new Dialogs());
                AssertPixels(original.Image, fixture.Page, 0);
                var task = features.HandleAsync(new(original, region, SelectionAction.Scroll, BrowserCaptureMode.DesktopManual));
                var bar = Ready(task); var vm = (ManualScrollCaptureViewModel)bar.DataContext;
                var barBounds = Bounds(bar);
                Require(bar.Viewport == region && barBounds.Intersect(region).IsEmpty, "Desktop mode expanded user selection or placed normal controls inside capture.");
                Require(DesktopWindows.Foreground == target && vm.Drag is null, "Desktop toolbar stole focus or entered browser drag mode.");
                int idleReads = desktop.Reads; Pause(550);
                Require(desktop.Reads == idleReads, "Idle manual capture keeps photographing the desktop.");
                var pointer = new PixelPoint(region.X + region.Width / 2, region.Y + region.Height / 2);
                Move(pointer); clock.Restart(); timer.Start();
                stage = kind + " physical wheel";
                for (int step = 0; step < 5; step++)
                {
                    int before = vm.Frames; Wheel(3);
                    Wait(() => task.IsCompleted || vm.Frames > before, 6);
                    if (task.IsCompleted) task.GetAwaiter().GetResult();
                    Require(!task.IsCompleted, "Manual capture finished without explicit completion.");
                }
                timer.Stop();
                double offset = fixture.State().Offset;
                Require(offset > 300 && desktop.CursorPosition == pointer, "Wheel did not reach actual target or capture moved the cursor.");
                SavePreview("46-desktop-manual-" + kind, region, bar, desktop.Inner);
                if (kind == "table") Click(TextWindowChecks.Descendants<Button>(bar).Single(b => b.Name == "CompleteManualButton"));
                else HotkeyChecks.Press(0x11, 0x0D);
                Wait(() => task.IsCompleted, 8); task.GetAwaiter().GetResult(); Require(failure is null, "Desktop capture reported a failure: " + failure?.GetType().Name);
                var editor = app.Windows.OfType<EditorWindow>().Single();
                var result = (EditorViewModel)editor.DataContext;
                Require(result.PixelWidth == region.Width && result.PixelHeight == region.Height + offset, "Desktop result size lost a viewport or included fixed window chrome.");
                AssertPixels(result.Image, fixture.Page, (int)offset);
                Require(!app.Windows.OfType<ManualScrollCaptureWindow>().Any(), "Desktop controls survived successful completion.");
                editor.CancelScreenshot();
                Console.WriteLine($"PASS external {kind} desktop: native wheel pass-through, exact original/stitched pixels, selected width, no idle captures, cursor/focus retained, real {(kind == "table" ? "button" : "Ctrl+Enter")} completion; offset={offset:0}.");

                if (kind == "table")
                {
                    stage = "partial result"; fixture.Command(0); DesktopWindows.Activate(target); Pause(120);
                    task = features.HandleAsync(new(original, region, SelectionAction.Scroll, BrowserCaptureMode.DesktopManual)); bar = Ready(task); vm = (ManualScrollCaptureViewModel)bar.DataContext;
                    Move(pointer); Wheel(3); Wait(() => vm.Frames > 1, 6);
                    int acceptedHeight = vm.Height; fixture.Command(2000); Wheel(1);
                    Wait(() => vm.CompleteText == "保存已采集", 6);
                    Require(vm.CanFinish, "A rejected overlap disabled saving already captured content.");
                    HotkeyChecks.Press(0x11, 0x0D); Wait(() => task.IsCompleted, 8); task.GetAwaiter().GetResult();
                    editor = app.Windows.OfType<EditorWindow>().Single(); result = (EditorViewModel)editor.DataContext;
                    Require(result.PixelHeight == acceptedHeight && result.Status.Contains("最后一段"), "Unmatched content was silently stitched or partial notice disappeared.");
                    AssertPixels(result.Image, fixture.Page, acceptedHeight - (int)region.Height); editor.CancelScreenshot();
                    Console.WriteLine("PASS excessive scroll jump pauses joining; explicit completion preserves exactly accepted pixels and displays partial-result notice.");

                    stage = "Escape and input cleanup"; fixture.Command(0); DesktopWindows.Activate(target); Pause(120);
                    task = features.HandleAsync(new(original, region, SelectionAction.Scroll, BrowserCaptureMode.DesktopManual)); bar = Ready(task);
                    HotkeyChecks.Press(0x1B); Wait(() => task.IsCompleted, 5); task.GetAwaiter().GetResult();
                    editor = app.Windows.OfType<EditorWindow>().Single(); Require(ReferenceEquals(editor.DataContext, original), "Esc published a late stitched image."); editor.CancelScreenshot();
                    int afterCancel = desktop.Reads; DesktopWindows.Activate(target); Move(pointer); Wheel(1); Pause(500);
                    Require(desktop.Reads == afterCancel && !app.Windows.OfType<ManualScrollCaptureWindow>().Any(), "Cancelled hooks or sampling remained active.");
                    Console.WriteLine("PASS real Escape cancels idle sampling, removes hooks/controls and restores original editor; later wheel input cannot revive capture.");

                    stage = "geometry protection"; fixture.Command(0); DesktopWindows.Activate(target); Pause(120);
                    task = features.HandleAsync(new(original, region, SelectionAction.Scroll, BrowserCaptureMode.DesktopManual)); bar = Ready(task);
                    fixture.Resize(); HotkeyChecks.Press(0x11, 0x0D); Wait(() => task.IsCompleted, 6); task.GetAwaiter().GetResult();
                    editor = app.Windows.OfType<EditorWindow>().Single(); Require(failure is InvalidOperationException && ReferenceEquals(editor.DataContext, original), "Resized target was silently joined."); failure = null; editor.CancelScreenshot();
                    Console.WriteLine("PASS target resize stops native capture, retains original editor and closes controls.");

                    stage = "focus protection"; fixture.Command(0); DesktopWindows.Activate(target); Pause(120);
                    task = features.HandleAsync(new(original, region, SelectionAction.Scroll, BrowserCaptureMode.DesktopManual)); bar = Ready(task);
                    var other = new Window { Style = null, Width = 200, Height = 180, Background = Brushes.Beige };
                    try
                    {
                        other.Show(); other.Activate(); Wait(() => task.IsCompleted, 5); task.GetAwaiter().GetResult();
                        editor = app.Windows.OfType<EditorWindow>().Single();
                        Require(ReferenceEquals(editor.DataContext, original) && !app.Windows.OfType<ManualScrollCaptureWindow>().Any(), "Switching focus captured another application's content or retained controls.");
                        editor.CancelScreenshot();
                    }
                    finally { other.Close(); }
                    Console.WriteLine("PASS switching foreground applications cancels capture instead of reading unrelated content.");
                }
            }
            Require(pulses > 20 && maxGap < 1000, "Native desktop capture blocked dispatcher heartbeat.");
            var flush = journal.FlushAsync(); Wait(() => flush.IsCompleted, 3); flush.GetAwaiter().GetResult();
            string logs = File.ReadAllText(Path.Combine(folder, "logs/events.jsonl"));
            Require(logs.Contains("desktop-target-ready") && logs.Contains("manual-partial-completed") && !logs.Contains("Public desktop fixture", StringComparison.Ordinal) && !logs.Contains(folder, StringComparison.Ordinal), "Desktop capture diagnostics lack technical stages or include fixture content/path.");
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/desktop-scroll.json", JsonSerializer.Serialize(new {
                ExternalNonBrowserWindows = true, TableAndChatFixtures = true, RealWheelPassThrough = true, ExactStitchedPixels = true,
                FixedSelectionWidth = true, IdleNoCapture = true, CursorUnchanged = true, NonactivatingControls = true,
                CleanCaptureWithoutOwnUi = true, NativeButtonCompletion = true, NativeCtrlEnterCompletion = true,
                UnmatchedTailExcluded = true, PartialResultNotice = true, EscapeCancels = true, HooksRemoved = true,
                ResizeStopsCapture = true, FocusChangeCancels = true, UiResponsive = true, TechnicalDiagnosticsOnly = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText("artifacts/validation/desktop-scroll-performance.json", JsonSerializer.Serialize(new { UiPulses = pulses, MaxUiPulseGapMilliseconds = Math.Round(maxGap, 2), CapturedReads = desktop.Reads }));
            Console.WriteLine($"PASS desktop manual capture integration; UI pulses={pulses}, max gap={maxGap:F1}ms; no Excel/ChatGPT application-specific claim."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("Desktop stage: " + stage); Console.Error.WriteLine(error); if (failure is not null) Console.Error.WriteLine(failure); return 1; }
        finally
        {
            timer.Stop(); features.CancelBrowserCapture();
            foreach (var editor in app.Windows.OfType<EditorWindow>().ToArray()) editor.CancelScreenshot();
            SynchronizationContext.SetSynchronizationContext(previous);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    public static int RunFixture(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string statePath = args[1], kind = args[2]; using var stop = EventWaitHandle.OpenExisting(args[3]);
        var page = Page(kind); var image = new Image { Source = page, Width = 600, Height = 3600, Stretch = Stretch.None, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var scroll = new ScrollViewer { Content = image, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, Width = 620, Height = 430, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var grid = new Grid { Margin = new Thickness(40, 80, 20, 20), Children = { scroll } };
        var window = new Window { Style = null, Title = "Public desktop fixture " + kind, Width = 760, Height = 650, Left = 40, Top = 30, Content = grid, Background = Brushes.White, UseLayoutRounding = true };
        PixelRect? fixedRegion = null;
        void State()
        {
            if (!window.IsLoaded) return;
            if (fixedRegion is null)
            {
                var p = image.PointToScreen(new Point(16, 12)); var q = image.PointToScreen(new Point(496, 392));
                fixedRegion = new PixelRect(p.X, p.Y, q.X - p.X, q.Y - p.Y).RoundOut();
            }
            var state = new FixtureState(new WindowInteropHelper(window).Handle.ToInt64(), fixedRegion.Value, scroll.VerticalOffset);
            string temp = statePath + ".new"; File.WriteAllText(temp, JsonSerializer.Serialize(state)); File.Move(temp, statePath, true);
        }
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.AddHook((IntPtr _, int message, IntPtr w, IntPtr _, ref bool handled) =>
        {
            if (message == 0x8053) { scroll.ScrollToVerticalOffset(w.ToInt32()); handled = true; }
            if (message == 0x8054) { window.Width += 30; handled = true; }
            return IntPtr.Zero;
        });
        scroll.ScrollChanged += (_, _) => State();
        window.Loaded += (_, _) => { scroll.Focus(); State(); };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) }; timer.Tick += (_, _) => { if (stop.WaitOne(0)) { timer.Stop(); window.Close(); app.Shutdown(); } };
        timer.Start(); window.Show(); window.Activate(); app.Run(); return 0;
    }
    private static BitmapSource Page(string kind)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 3600)); var random = new Random(kind == "table" ? 53 : 1053);
            for (int row = 0; row < 60; row++)
            {
                double y = row * 60;
                if (kind == "table")
                {
                    dc.DrawRectangle(row % 2 == 0 ? Brushes.AliceBlue : Brushes.White, new Pen(Brushes.LightGray, 1), new Rect(0, y, 600, 60));
                    foreach (int x in new[] { 60, 260, 440 }) dc.DrawLine(new Pen(Brushes.LightGray, 1), new Point(x, y), new Point(x, y + 60));
                }
                var text = new FormattedText($"{row + 1:00}   {(kind == "table" ? "Record / 项目" : "Answer / 内容")}   {random.Next(10000, 99999)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 17, Brushes.DarkSlateGray, 1);
                dc.DrawText(text, new Point(12, y + 8));
                for (int x = 24; x < 580; x += 21)
                { var color = Color.FromRgb((byte)random.Next(30, 170), (byte)random.Next(60, 190), (byte)random.Next(60, 190)); dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, y + 39, random.Next(5, 18), 4)); }
            }
        }
        var bitmap = new RenderTargetBitmap(600, 3600, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        // The screen composites glyph-edge alpha against the white window. Flatten the fixture
        // explicitly so expected pixels are actual displayed pixels, not uncomposited Pbgra data.
        var pixels = new byte[600 * 3600 * 4]; bitmap.CopyPixels(pixels, 600 * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        { int white = 255 - pixels[i + 3]; for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Min(255, pixels[i + c] + white); pixels[i + 3] = 255; }
        var opaque = BitmapSource.Create(600, 3600, 96, 96, PixelFormats.Bgra32, null, pixels, 600 * 4); opaque.Freeze(); return opaque;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string path; private readonly EventWaitHandle stop; private readonly Process process;
        public BitmapSource Page { get; }
        public Fixture(string folder, string kind)
        {
            path = Path.Combine(folder, kind + ".json"); string name = "Local\\SnipDesktopStop" + Guid.NewGuid().ToString("N"); stop = new(false, EventResetMode.ManualReset, name); Page = DesktopScrollChecks.Page(kind);
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true }; start.ArgumentList.Add(typeof(DesktopScrollChecks).Assembly.Location);
            foreach (string value in new[] { "--desktop-scroll-fixture", path, kind, name }) start.ArgumentList.Add(value);
            process = Process.Start(start)!; Wait(() => File.Exists(path) || process.HasExited, 8); Require(!process.HasExited, "Desktop fixture exited early."); Pause(200);
        }
        public FixtureState State()
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<FixtureState>(file)!;
        }
        public void Command(int offset) { SendMessageTimeout(new(State().Handle), 0x8053, new(offset), IntPtr.Zero, 2, 1000, out _); Pause(200); }
        public void Resize() { SendMessageTimeout(new(State().Handle), 0x8054, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _); Pause(160); }
        public void Dispose() { stop.Set(); if (!process.WaitForExit(2000)) process.Kill(true); process.Dispose(); stop.Dispose(); }
    }
    private static void AssertPixels(BitmapSource result, BitmapSource page, int offset)
    {
        var expected = new CroppedBitmap(page, new Int32Rect(16, 12, 480, 380 + offset)); expected.Freeze();
        var actualBytes = Pixels(result); var expectedBytes = Pixels(expected);
        if (result.PixelWidth != expected.PixelWidth || result.PixelHeight != expected.PixelHeight || !actualBytes.SequenceEqual(expectedBytes))
        {
            Directory.CreateDirectory("artifacts/validation/desktop-debug");
            var export = new ImageExportService(); export.SavePng(result, $"artifacts/validation/desktop-debug/actual-{offset}.png"); export.SavePng(expected, $"artifacts/validation/desktop-debug/expected-{offset}.png");
            var differences = Enumerable.Range(0, Math.Min(actualBytes.Length, expectedBytes.Length)).Where(i => actualBytes[i] != expectedBytes[i]).ToArray();
            Console.WriteLine($"Pixel dimensions actual={result.PixelWidth}x{result.PixelHeight}, expected={expected.PixelWidth}x{expected.PixelHeight}; different bytes={differences.Length}; first differences=" + string.Join(";", differences.Take(12).Select(i => $"{i}:{actualBytes[i]}/{expectedBytes[i]}")));
            throw new InvalidOperationException("Native long screenshot differs from public source pixels, including overlap, fixed chrome or own controls.");
        }
    }
    private static ManualScrollCaptureWindow Ready(Task task)
    {
        Wait(() => task.IsCompleted || Application.Current.Windows.OfType<ManualScrollCaptureWindow>().Any(w => ((ManualScrollCaptureViewModel)w.DataContext).Ready), 8);
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); throw new InvalidOperationException("Desktop capture stopped before ready."); }
        return Application.Current.Windows.OfType<ManualScrollCaptureWindow>().Single();
    }
    private static PixelRect Bounds(Window w) { var a = w.PointToScreen(new Point()); var b = w.PointToScreen(new Point(w.ActualWidth, w.ActualHeight)); return PixelRect.FromPoints(new(a.X, a.Y), new(b.X, b.Y)); }
    private static void SavePreview(string name, PixelRect region, Window bar, DesktopCaptureService capture)
    {
        var r = Bounds(bar); var bounds = new PixelRect(Math.Min(region.X, r.X) - 8, Math.Min(region.Y, r.Y) - 8, Math.Max(region.Right, r.Right) - Math.Min(region.X, r.X) + 16, Math.Max(region.Bottom, r.Bottom) - Math.Min(region.Y, r.Y) + 16).RoundOut();
        Directory.CreateDirectory("artifacts/validation/ui-previews"); new ImageExportService().SavePng(capture.CaptureRegion(bounds), "artifacts/validation/ui-previews/" + name + ".png");
    }
    private static byte[] Pixels(BitmapSource image) { var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] b = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(b, bitmap.PixelWidth * 4, 0); return b; }
    private static void Move(PixelPoint p) { Require(SetPhysicalCursorPos((int)p.X, (int)p.Y), "Native pointer rejected."); Pause(60); }
    private static void Wheel(int count) => Send(Enumerable.Range(0, count).Select(_ => new Input { Data = new() { Mouse = new() { Wheel = unchecked((uint)-120), Flags = 0x0800 } } }).ToArray());
    private static void Click(Button b) { var p = b.PointToScreen(new Point(b.ActualWidth / 2, b.ActualHeight / 2)); Move(new(p.X, p.Y)); Send([new() { Data = new() { Mouse = new() { Flags = 2 } } }, new() { Data = new() { Mouse = new() { Flags = 4 } } }]); }
    private static void Send(Input[] input) => Require(SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) == input.Length, "Native mouse input rejected.");
    private static void Pause(int ms) { var task = Task.Delay(ms); Wait(() => task.IsCompleted, 3); }
    private static void Wait(Func<bool> done, int seconds) => TextWindowChecks.PumpUntil(done, TimeSpan.FromSeconds(seconds));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class CountingCapture : IDesktopCaptureService
    {
        public DesktopCaptureService Inner { get; } = new(); private int reads; public int Reads => Volatile.Read(ref reads);
        public IReadOnlyList<DisplayInfo> GetDisplays() => Inner.GetDisplays(); public DesktopFrame Capture() => Inner.Capture();
        public BitmapSource CaptureRegion(PixelRect r) { Interlocked.Increment(ref reads); return Inner.CaptureRegion(r); }
        public PixelPoint CursorPosition => Inner.CursorPosition; public void PlaceOverlay(Window window, PixelRect bounds) => Inner.PlaceOverlay(window, bounds);
    }
    private sealed class Dialogs : IEditorDialogs
    {
        public string? ChoosePngPath() => null; public TextInput? EditText(string text, string color, double size) => null;
        public CloseChoice ConfirmClose() => CloseChoice.Discard; public void ShowError(string message) => throw new InvalidOperationException(message);
    }
}
