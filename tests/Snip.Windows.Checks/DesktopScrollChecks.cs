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
    private sealed record FixtureState(long Handle, PixelRect Region, double Offset, int Header = 0, int Footer = 0);

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
            foreach (string kind in new[] { "table", "chat", "frozen", "continuous", "sheet" })
            {
                stage = kind + " external window";
                using var fixture = new Fixture(folder, kind);
                var state = fixture.State(); var region = state.Region;
                var target = new IntPtr(state.Handle);
                Require(DesktopWindows.Snapshot().Any(w => w.Handle == target), "External desktop fixture not found independently of current process.");
                DesktopWindows.Activate(target); Pause(160);
                var original = new EditorViewModel(desktop.CaptureRegion(region), renderer, export, new Dialogs());
                AssertPixels(original.Image, fixture.Page, 0, state, original.Image);
                var task = features.HandleAsync(new(original, region, SelectionAction.Scroll, BrowserCaptureMode.DesktopManual));
                var bar = Ready(task); var vm = (ManualScrollCaptureViewModel)bar.DataContext;
                var barBounds = Bounds(bar);
                Require(bar.Viewport == region && barBounds.Intersect(region).IsEmpty, "Desktop mode expanded user selection or placed normal controls inside capture.");
                Require(DesktopWindows.Foreground == target && vm.Drag is null, "Desktop toolbar stole focus or entered browser drag mode.");
                int idleReads = desktop.Reads; Pause(550);
                Require(desktop.Reads == idleReads, "Idle manual capture keeps photographing the desktop.");
                var pointer = new PixelPoint(region.X + region.Width / 2, region.Y + state.Header + (region.Height - state.Header - state.Footer) / 2);
                Move(pointer); clock.Restart(); timer.Start();
                stage = kind + " physical wheel";
                for (int step = 0; step < 5; step++)
                {
                    int before = vm.Frames; Wheel(kind is "frozen" or "continuous" or "sheet" ? 1 : 3);
                    if (kind == "continuous") Pause(25);
                    else
                    {
                        try { Wait(() => task.IsCompleted || vm.Frames > before, 6); }
                        catch (TimeoutException)
                        {
                            Console.WriteLine($"Native wheel state: kind={kind}; step={step}; offset={fixture.State().Offset}; frames={vm.Frames}; height={vm.Height}; reads={desktop.Reads}; foreground={DesktopWindows.Foreground == target}; status={vm.Status}.");
                            throw;
                        }
                    }
                    if (task.IsCompleted) task.GetAwaiter().GetResult();
                    Require(!task.IsCompleted, "Manual capture finished without explicit completion.");
                }
                if (kind == "continuous") Wait(() => task.IsCompleted || vm.Height == region.Height + fixture.State().Offset, 6);
                timer.Stop();
                double offset = fixture.State().Offset;
                Require(offset >= (kind is "frozen" or "continuous" or "sheet" ? 160 : 300) && desktop.CursorPosition == pointer, "Wheel did not reach actual target or capture moved the cursor.");
                if (kind == "continuous") Require(vm.Frames > 1 && vm.CompleteText == "完成", "Continuous wheel input without stopping lost intermediate overlap.");
                if (kind == "frozen") Require(state.Header > region.Height / 2 && !vm.CompleteText.Contains("保存"), "Large frozen header caused ordinary wheel input to pause.");
                SavePreview("46-desktop-manual-" + kind, region, bar, desktop.Inner);
                if (kind == "table") Click(TextWindowChecks.Descendants<Button>(bar).Single(b => b.Name == "CompleteManualButton"));
                else HotkeyChecks.Press(0x11, 0x0D);
                Wait(() => task.IsCompleted, 8); task.GetAwaiter().GetResult(); Require(failure is null, "Desktop capture reported a failure: " + failure?.GetType().Name);
                var editor = app.Windows.OfType<EditorWindow>().Single();
                var result = (EditorViewModel)editor.DataContext;
                Require(result.PixelWidth == region.Width && result.PixelHeight == region.Height + offset, "Desktop result size lost a viewport or included fixed window chrome.");
                AssertPixels(result.Image, fixture.Page, (int)offset, state, original.Image);
                Require(!app.Windows.OfType<ManualScrollCaptureWindow>().Any(), "Desktop controls survived successful completion.");
                editor.CancelScreenshot();
                Console.WriteLine($"PASS external {kind} desktop: native wheel pass-through, exact original/stitched pixels, selected width, no idle captures, cursor/focus retained, real {(kind == "table" ? "button" : "Ctrl+Enter")} completion; offset={offset:0}.");

                if (kind == "sheet")
                {
                    stage = "sheet recovery"; fixture.Command(0); DesktopWindows.Activate(target); Pause(120);
                    task = features.HandleAsync(new(original, region, SelectionAction.Scroll, BrowserCaptureMode.DesktopManual)); bar = Ready(task); vm = (ManualScrollCaptureViewModel)bar.DataContext;
                    Move(pointer); Wheel(1); Wait(() => vm.Frames > 1, 6);
                    int acceptedHeight = vm.Height, acceptedOffset = (int)fixture.State().Offset;
                    fixture.Command(1800); Wheel(1); Wait(() => vm.CompleteText == "保存已采集", 6);
                    // Let the input-driven sampler go idle before arranging the next native
                    // input. Otherwise our fixture command can legitimately add valid rows.
                    Pause(650);
                    fixture.Command(acceptedOffset + 24); WheelUp();
                    Wait(() => vm.CompleteText == "完成" && vm.Status.Contains("已采集范围"), 6);
                    Require(vm.Height == acceptedHeight, "Recovery duplicated rows already collected.");
                    Pause(650);
                    fixture.Command(acceptedOffset + 48); fixture.ChangeToolbar(); WheelUp(); Pause(550);
                    Console.WriteLine($"Sheet return state: accepted-offset={acceptedOffset}; current-offset={fixture.State().Offset}; accepted-height={acceptedHeight}; current-height={vm.Height}; frames={vm.Frames}; complete={vm.CompleteText}.");
                    Require(vm.CompleteText == "完成" && vm.Height == acceptedHeight, "Changed frozen toolbar kept the returned body paused.");
                    for (int step = 0; step < 3; step++) { int before = vm.Frames; Wheel(1); Wait(() => vm.Frames > before, 6); }
                    int finalOffset = (int)fixture.State().Offset;
                    HotkeyChecks.Press(0x11, 0x0D); Wait(() => task.IsCompleted, 8); task.GetAwaiter().GetResult();
                    editor = app.Windows.OfType<EditorWindow>().Single(); result = (EditorViewModel)editor.DataContext;
                    Require(result.PixelHeight == region.Height + finalOffset && !result.Status.Contains("最后一段"), "Recovered capture remained partial or lost rows.");
                    AssertPixels(result.Image, fixture.Page, finalOffset, state, original.Image); editor.CancelScreenshot();
                    Console.WriteLine("PASS sparse sheet recovery: pause, native upward wheel inside collected range, changed frozen toolbar, resume and exact whole output without duplicated rows.");
                }

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
            Require(pulses > 20 && maxGap < 1000 && desktop.ContinuousReads > 0, "Native desktop capture blocked dispatcher heartbeat or never used continuous reads.");
            var flush = journal.FlushAsync(); Wait(() => flush.IsCompleted, 3); flush.GetAwaiter().GetResult();
            string logs = File.ReadAllText(Path.Combine(folder, "logs/events.jsonl"));
            Require(logs.Contains("desktop-target-ready") && logs.Contains("manual-partial-completed") && !logs.Contains("Public desktop fixture", StringComparison.Ordinal) && !logs.Contains(folder, StringComparison.Ordinal), "Desktop capture diagnostics lack technical stages or include fixture content/path.");
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/desktop-scroll.json", JsonSerializer.Serialize(new {
                ExternalNonBrowserWindows = true, TableAndChatFixtures = true, RealWheelPassThrough = true, ExactStitchedPixels = true,
                FixedSelectionWidth = true, IdleNoCapture = true, CursorUnchanged = true, NonactivatingControls = true,
                CleanCaptureWithoutOwnUi = true, NativeButtonCompletion = true, NativeCtrlEnterCompletion = true,
                UnmatchedTailExcluded = true, PartialResultNotice = true, EscapeCancels = true, HooksRemoved = true,
                ResizeStopsCapture = true, FocusChangeCancels = true, UiResponsive = true, TechnicalDiagnosticsOnly = true,
                LargeFrozenHeader = true, NativeSingleWheelStep = true, FrozenHeaderFooterOnce = true, ContinuousWheelWithoutPauses = true,
                SparseSpreadsheet = true, ReturnInsideRangeRecovers = true, ChangedToolbarRecovers = true, ResumeWithoutDuplicateRows = true, RapidWheel25Milliseconds = true,
                OutsideOutlineContinuousRead = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText("artifacts/validation/desktop-scroll-performance.json", JsonSerializer.Serialize(new { UiPulses = pulses, MaxUiPulseGapMilliseconds = Math.Round(maxGap, 2), CapturedReads = desktop.Reads, desktop.ContinuousReads }));
            Console.WriteLine($"PASS desktop manual capture integration; UI pulses={pulses}, max gap={maxGap:F1}ms; no Excel/ChatGPT application-specific claim."); return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Desktop stage: " + stage); Console.Error.WriteLine(error); if (failure is not null) Console.Error.WriteLine(failure);
            try
            {
                var flush = journal.FlushAsync(); Wait(() => flush.IsCompleted, 3); flush.GetAwaiter().GetResult();
                string path = Path.Combine(folder, "logs/events.jsonl");
                Directory.CreateDirectory("artifacts/validation/desktop-debug");
                File.Copy(path, "artifacts/validation/desktop-debug/technical-events.jsonl", true);
                foreach (var line in File.ReadLines(path).TakeLast(8))
                {
                    using var record = JsonDocument.Parse(line);
                    Console.WriteLine("Desktop technical stage: " + record.RootElement.GetProperty("Stage").GetString() + " " + record.RootElement.GetProperty("Fields").GetRawText());
                }
            }
            catch (Exception diagnosticError) { Console.WriteLine("Technical export: " + diagnosticError.GetType().Name); }
            return 1;
        }
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
        var grid = new Grid { Margin = new Thickness(40, 80, 20, 20), Children = { scroll }, VerticalAlignment = VerticalAlignment.Top };
        Border? header = null;
        if (kind is "frozen" or "sheet")
        {
            grid.Height = 430; grid.RowDefinitions.Add(new() { Height = new(220) }); grid.RowDefinitions.Add(new() { Height = new(180) }); grid.RowDefinitions.Add(new() { Height = new(30) });
            header = new Border { Width = 620, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.LightBlue, Child = new TextBlock { Text = "Public fixed toolbar / 冻结表头\n\nDocument title and editing controls\n\nColumn A     Column B     Column C\n\nFrozen row 1\n\nFrozen row 2", Margin = new(12), FontSize = 16 } };
            grid.Children.Add(header); scroll.Height = 180; Grid.SetRow(scroll, 1);
            var footer = new Border { Width = 620, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.LightGray, Child = new TextBlock { Text = "Public sheet tabs / 固定状态栏", Margin = new(10, 4, 0, 0) } }; Grid.SetRow(footer, 2); grid.Children.Add(footer);
        }
        var window = new Window { Style = null, Title = "Public desktop fixture " + kind, Width = 760, Height = 650, Left = 40, Top = 30, Content = grid, Background = Brushes.White, UseLayoutRounding = true };
        PixelRect? fixedRegion = null; int fixedHeader = 0, fixedFooter = 0;
        void State()
        {
            if (!window.IsLoaded) return;
            if (fixedRegion is null)
            {
                var p = image.PointToScreen(new Point(16, 12)); var q = image.PointToScreen(new Point(496, 392));
                if (kind == "continuous") q = image.PointToScreen(new Point(496, 192));
                if (kind is "frozen" or "sheet")
                {
                    p.Y = grid.PointToScreen(new Point(0, 12)).Y; q.Y = grid.PointToScreen(new Point(0, 422)).Y;
                    fixedHeader = (int)Math.Round(image.PointToScreen(new Point()).Y - p.Y);
                    fixedFooter = (int)Math.Round(q.Y - image.PointToScreen(new Point(0, scroll.ViewportHeight)).Y);
                }
                fixedRegion = new PixelRect(p.X, p.Y, q.X - p.X, q.Y - p.Y).RoundOut();
            }
            var state = new FixtureState(new WindowInteropHelper(window).Handle.ToInt64(), fixedRegion.Value, scroll.VerticalOffset, fixedHeader, fixedFooter);
            string temp = statePath + ".new"; File.WriteAllText(temp, JsonSerializer.Serialize(state)); File.Move(temp, statePath, true);
        }
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.AddHook((IntPtr _, int message, IntPtr w, IntPtr _, ref bool handled) =>
        {
            if (message == 0x8053) { scroll.ScrollToVerticalOffset(w.ToInt32()); handled = true; }
            if (message == 0x8054) { window.Width += 30; handled = true; }
            if (message == 0x8055 && header is not null) { header.Background = Brushes.LemonChiffon; handled = true; }
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
                if (kind is "table" or "frozen" or "sheet")
                {
                    dc.DrawRectangle(kind != "sheet" && row % 2 == 0 ? Brushes.AliceBlue : Brushes.White, new Pen(Brushes.LightGray, 1), new Rect(0, y, 600, 60));
                    foreach (int x in new[] { 60, 260, 440 }) dc.DrawLine(new Pen(Brushes.LightGray, 1), new Point(x, y), new Point(x, y + 60));
                }
                var text = new FormattedText(kind == "sheet" ? $"{row + 1:00} 项目 {random.Next(10000, 99999)}" : $"{row + 1:00}   {(kind == "table" ? "Record / 项目" : "Answer / 内容")}   {random.Next(10000, 99999)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), kind == "sheet" ? 13 : 17, Brushes.DarkSlateGray, 1);
                dc.DrawText(text, new Point(12, y + 8));
                for (int x = 24; kind != "sheet" && x < 580; x += 21)
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
        public void ChangeToolbar() { SendMessageTimeout(new(State().Handle), 0x8055, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _); Pause(160); }
        public void Dispose() { stop.Set(); if (!process.WaitForExit(2000)) process.Kill(true); process.Dispose(); stop.Dispose(); }
    }
    private static void AssertPixels(BitmapSource result, BitmapSource page, int offset, FixtureState? state = null, BitmapSource? initial = null)
    {
        BitmapSource expected;
        if (state is { Header: > 0 } && initial is not null)
        {
            int width = (int)state.Region.Width, height = (int)state.Region.Height + offset, stride = width * 4;
            var pixels = new byte[height * stride]; var first = Pixels(initial);
            Buffer.BlockCopy(first, 0, pixels, 0, state.Header * stride);
            var body = new CroppedBitmap(page, new Int32Rect(16, 0, width, (int)state.Region.Height - state.Header - state.Footer + offset));
            var content = Pixels(body); Buffer.BlockCopy(content, 0, pixels, state.Header * stride, content.Length);
            Buffer.BlockCopy(first, first.Length - state.Footer * stride, pixels, pixels.Length - state.Footer * stride, state.Footer * stride);
            expected = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        }
        else expected = new CroppedBitmap(page, new Int32Rect(16, 12, 480, (int)(state?.Region.Height ?? 380) + offset));
        expected.Freeze();
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
    private static void WheelUp() => Send([new() { Data = new() { Mouse = new() { Wheel = 120, Flags = 0x0800 } } }]);
    private static void Click(Button b) { var p = b.PointToScreen(new Point(b.ActualWidth / 2, b.ActualHeight / 2)); Move(new(p.X, p.Y)); Send([new() { Data = new() { Mouse = new() { Flags = 2 } } }, new() { Data = new() { Mouse = new() { Flags = 4 } } }]); }
    private static void Send(Input[] input)
    {
        // Match real input: inject from outside the hook's UI thread and pump that
        // dispatcher. A synchronous SendInput here can time out and silently remove hooks.
        var sending = Task.Run(() => SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()));
        Wait(() => sending.IsCompleted, 3);
        Require(sending.GetAwaiter().GetResult() == input.Length, "Native mouse input rejected.");
    }
    private static void Pause(int ms) { var task = Task.Delay(ms); Wait(() => task.IsCompleted, 3); }
    private static void Wait(Func<bool> done, int seconds) => TextWindowChecks.PumpUntil(done, TimeSpan.FromSeconds(seconds));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class CountingCapture : IDesktopCaptureService
    {
        public DesktopCaptureService Inner { get; } = new(); private int reads; public int Reads => Volatile.Read(ref reads);
        private int continuousReads; public int ContinuousReads => Volatile.Read(ref continuousReads);
        public IReadOnlyList<DisplayInfo> GetDisplays() => Inner.GetDisplays(); public DesktopFrame Capture() => Inner.Capture();
        public BitmapSource CaptureRegion(PixelRect r) { Interlocked.Increment(ref reads); return Inner.CaptureRegion(r); }
        public BitmapSource CaptureRegion(PixelRect r, bool synchronizeComposition)
        { Interlocked.Increment(ref reads); if (!synchronizeComposition) Interlocked.Increment(ref continuousReads); return Inner.CaptureRegion(r, synchronizeComposition); }
        public PixelPoint CursorPosition => Inner.CursorPosition; public void PlaceOverlay(Window window, PixelRect bounds) => Inner.PlaceOverlay(window, bounds);
    }
    private sealed class Dialogs : IEditorDialogs
    {
        public string? ChoosePngPath() => null; public TextInput? EditText(string text, string color, double size) => null;
        public CloseChoice ConfirmClose() => CloseChoice.Discard; public void ShowError(string message) => throw new InvalidOperationException(message);
    }
}
