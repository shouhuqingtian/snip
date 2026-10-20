using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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

internal static class HoverRegionChecks
{
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    private delegate bool WindowCallback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal static int StallWorker() { Thread.Sleep(20_000); return 0; }
    private static string WorkerExe => Path.GetFullPath("src/Snip.App/bin/Release/net8.0-windows10.0.19041.0/Snip.exe");
    private static HoverRegionProbe RealProbe() => new(() => new ProcessStartInfo(WorkerExe));
    public static int Run()
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { using var hotkey = new HotkeyService(); Verify(hotkey); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    internal static void Verify(HotkeyService hotkey)
    {
        var desktop = new DesktopCaptureService();
        var originalCursor = desktop.CursorPosition;
        var button = new Button { Content = "Public action", Width = 150, Height = 44, Margin = new Thickness(12) };
        var tools = new ToolBar(); tools.Items.Add(button);
        var edit = new TextBox { Text = "Public editable text", Width = 250, Height = 60, Margin = new Thickness(12) };
        var list = new ListBox { Width = 250, Height = 120, Margin = new Thickness(12), ItemsSource = new[] { "Public row one", "Public row two", "Public row three" } };
        var panel = new GroupBox { Header = "Public panel", Width = 270, Height = 100, Margin = new Thickness(12), Content = new Border { Background = Brushes.PaleGreen } };
        var stack = new StackPanel(); stack.Children.Add(tools); stack.Children.Add(edit); stack.Children.Add(list); stack.Children.Add(panel);
        var window = new Window { Style = null, Title = "Public smart selection fixture", Left = 70, Top = 70, Width = 420, Height = 540, Content = stack, Background = Brushes.White };
        SelectionSession? session = null; RecordingExport? export = null; DesktopFrame? frame = null;
        int workerPid = 0;
        var times = new List<double>();
        var sessions = new List<SelectionSession>();
        void Open(object? sender, EventArgs args)
        {
            export = new RecordingExport();
            frame = desktop.Capture();
            var targets = DesktopWindows.Snapshot(includeCurrentProcess: true);
            session = new SelectionSession(frame, desktop, new AnnotationRenderer(), export, targets, () =>
            {
                var service = RealProbe(); service.WorkerStarted += pid => Volatile.Write(ref workerPid, pid); return service;
            });
            sessions.Add(session); session.Show();
            session.ViewModel.Changed += (_, _) => Console.WriteLine("Public fixture hover bounds: " + session.ViewModel.DisplayedSelection);
        }
        PixelRect Bounds(FrameworkElement element)
        {
            var a = element.PointToScreen(new()); var b = element.PointToScreen(new(element.ActualWidth, element.ActualHeight));
            return new PixelRect(a.X, a.Y, b.X - a.X, b.Y - a.Y).RoundOut();
        }
        PixelPoint Center(FrameworkElement element) { var r = Bounds(element); return new(r.X + r.Width / 2, r.Y + r.Height / 2); }
        void Hover(FrameworkElement element)
        {
            var expected = Bounds(element); var clock = Stopwatch.StartNew(); Move(Center(element));
            Wait(() => session!.ViewModel.DisplayedSelection == expected, "Internal region not selected: " + element.GetType().Name);
            times.Add(clock.Elapsed.TotalMilliseconds);
        }
        try
        {
            window.Show(); window.Activate(); window.UpdateLayout(); Pause(100);
            Require(hotkey.Register(HotkeyGesture.Default), "Smart selection hotkey could not register.");
            hotkey.Triggered += Open;
            Move(Center(button)); HotkeyChecks.Press(0x11, 0x12, 0x41);
            Wait(() => session is not null && session.ViewModel.DisplayedSelection == Bounds(button), "Initial hover did not refine the real toolbar button under the frozen overlay.");
            Require(session!.ViewModel.Selection.IsEmpty && session.Editor is null, "Hover entered editing without a click.");
            Hover(edit);
            // Target the blank part of the list: rows should not replace the complete list region there.
            var listBounds = Bounds(list); Move(new(listBounds.Right - 12, listBounds.Bottom - 12));
            Wait(() => session!.ViewModel.DisplayedSelection == listBounds, "List background was not recognized as the list region.");
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            Hover(item);
            var panelBounds = Bounds(panel); Move(new(panelBounds.X + 30, panelBounds.Bottom - 25));
            Wait(() => session!.ViewModel.DisplayedSelection == panelBounds, "Panel background was not recognized.");
            Hover(button);
            SavePreview(Application.Current.Windows.OfType<SelectionWindow>().Single(w => w.IsActive));
            using var liveWorker = Process.GetProcessById(workerPid);
            double workerMiB = liveWorker.WorkingSet64 / (1024.0 * 1024.0);
            var expected = Bounds(button); int releasedPid = workerPid;
            HotkeyChecks.Press(0x0D);
            Wait(() => session.Completion.IsCompleted && Clipboard.ContainsImage(), "Enter did not copy the hovered internal region.");
            var result = Clipboard.GetImage();
            var crop = new CroppedBitmap(frame!.Image, new Int32Rect((int)(expected.X - frame.Bounds.X), (int)(expected.Y - frame.Bounds.Y), (int)expected.Width, (int)expected.Height));
            Require(export!.Copies == 1 && result.PixelWidth == crop.PixelWidth && result.PixelHeight == crop.PixelHeight && Pixels(result).SequenceEqual(Pixels(crop)), "Smart Enter copied different pixels from the highlighted frozen region.");
            Wait(() => Exited(releasedPid), "Smart selection worker survived copy completion.");
            Console.WriteLine("PASS native smart hover: toolbar button, text input, list, list row and panel; Enter copies the frozen internal region without a click; helper exits.");
            hotkey.Triggered -= Open;

            VerifyStaleAndManual(desktop, window, button);
            VerifyTimeout(desktop, window);
            VerifyCoordinator(hotkey, window, button);
            VerifyCancelNoPrompt(desktop, window);
            SmartBrowserChecks.Verify();
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/smart-selection.json", JsonSerializer.Serialize(new {
                ToolbarButton = true, TextInput = true, List = true, ListRow = true, Panel = true,
                EnterCopiesFrozenRegion = true, WorkerReleased = true, StaleResultsIgnored = true,
                ManualSelectionPreserved = true, EscapeResponsive = true, TimeoutFallsBack = true,
                NoRestartStorm = true, CoordinatorWorkerHost = true, CancelButtonNoPrompt = true, EscapeNoPrompt = true, EditorCancelNoPrompt = true,
                CancelPreservesClipboard = true, ChromeButton = true, ChromePanel = true, ChromeEnterPixels = true,
                WorkerWorkingSetMiB = workerMiB, WarmHoverMilliseconds = times
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            hotkey.Triggered -= Open; foreach (var owned in sessions) owned.Dispose(); window.Close();
            SetPhysicalCursorPos((int)originalCursor.X, (int)originalCursor.Y);
        }
    }

    private static void VerifyCancelNoPrompt(DesktopCaptureService desktop, Window owner)
    {
        var target = DesktopWindows.Snapshot(includeCurrentProcess: true).Single(w => w.Handle == new WindowInteropHelper(owner).Handle);
        foreach (bool escape in new[] { false, true })
        {
            Clipboard.SetText("Public dirty-selection cancellation sentinel");
            using var selection = new SelectionSession(desktop.Capture(), desktop, new AnnotationRenderer(), new RecordingExport(), [target]);
            selection.Show(); selection.ViewModel.SetSelection(new(target.Bounds.X + 20, target.Bounds.Y + 50, 220, 180)); selection.BeginEditing();
            selection.Editor!.BeginPointer(new(20, 20), false); selection.Editor.EndPointer(new(100, 80));
            Require(selection.Editor.IsDirty, "Cancellation fixture must contain unsaved annotations.");
            using var detector = new PromptDetector();
            if (escape) HotkeyChecks.Press(0x1B);
            else
            {
                var button = Application.Current.Windows.OfType<SelectionWindow>().SelectMany(w => TextWindowChecks.Descendants<Button>(w))
                    .Single(b => b.IsVisible && Equals(b.ToolTip, "取消截图"));
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            }
            Wait(() => selection.Completion.IsCompleted || detector.Seen, "Cancelling a dirty screenshot required another action.");
            Require(!detector.Seen && selection.Completion.IsCompleted && Clipboard.ContainsText() && Clipboard.GetText() == "Public dirty-selection cancellation sentinel", "Dirty screenshot cancellation prompted again or changed clipboard.");
        }
        var renderer = new AnnotationRenderer(); var export = new RecordingExport();
        using var pins = new PinManager(export);
        var hub = new FeatureHub(renderer, export, desktop, pins);
        var image = desktop.CaptureRegion(new(target.Bounds.X + 20, target.Bounds.Y + 50, 220, 180));
        var vm = new EditorViewModel(image, renderer, export, new NoDialogs());
        vm.BeginPointer(new(20, 20), false); vm.EndPointer(new(100, 80));
        var editor = hub.OpenEditor(vm);
        using (var detector = new PromptDetector())
        {
            var cancel = TextWindowChecks.Descendants<Button>(editor).Single(b => b.IsVisible && Equals(b.ToolTip, "取消截图"));
            ((IInvokeProvider)new ButtonAutomationPeer(cancel).GetPattern(PatternInterface.Invoke)).Invoke();
            Wait(() => !editor.IsVisible || detector.Seen, "Independent editor cancel required confirmation.");
            Require(!detector.Seen && !editor.IsVisible && export.Copies == 0 && Clipboard.GetText() == "Public dirty-selection cancellation sentinel", "Editor red cancel prompted or exported instead of discarding.");
        }
        Console.WriteLine("PASS dirty screenshot red cancel and native Escape plus independent-editor red cancel close directly without confirmation; clipboard is preserved.");
    }

    private static void VerifyCoordinator(HotkeyService hotkey, Window owner, FrameworkElement button)
    {
        var desktop = new RecordingCapture(); var export = new RecordingExport(); var renderer = new AnnotationRenderer();
        using var pins = new PinManager(export);
        using var coordinator = new CaptureCoordinator(desktop, renderer, export, new FeatureHub(renderer, export, desktop, pins));
        Task? active = null;
        void Open(object? sender, EventArgs args) => active = coordinator.CaptureAsync();
        hotkey.Triggered += Open;
        try
        {
            owner.Activate(); var point = button.PointToScreen(new(button.ActualWidth / 2, button.ActualHeight / 2));
            var a = button.PointToScreen(new()); var b = button.PointToScreen(new(button.ActualWidth, button.ActualHeight));
            var expected = new PixelRect(a.X, a.Y, b.X - a.X, b.Y - a.Y).RoundOut();
            SetPhysicalCursorPos((int)point.X, (int)point.Y); HotkeyChecks.Press(0x11, 0x12, 0x41);
            Wait(() => Application.Current.Windows.OfType<SelectionWindow>().Any(w => TextWindowChecks.Descendants<Border>(w)
                .Any(frame => frame.Name == "SelectionHint" && frame.Child is TextBlock hint && hint.Text.StartsWith($"{expected.Width:0} × {expected.Height:0} 像素", StringComparison.Ordinal))), "Real coordinator did not use Snip's internal-region worker.");
            HotkeyChecks.Press(0x0D); Wait(() => active?.IsCompleted == true, "Coordinator smart Enter did not finish."); active!.GetAwaiter().GetResult();
            var result = Clipboard.GetImage(); var frozen = desktop.Last!;
            var crop = new CroppedBitmap(frozen.Image, new Int32Rect((int)(expected.X - frozen.Bounds.X), (int)(expected.Y - frozen.Bounds.Y), (int)expected.Width, (int)expected.Height));
            Require(export.Copies == 1 && result.PixelWidth == crop.PixelWidth && result.PixelHeight == crop.PixelHeight && Pixels(result).SequenceEqual(Pixels(crop)), "Coordinator smart crop differs from frozen control pixels.");
            Console.WriteLine("PASS actual CaptureCoordinator Ctrl+Alt+A uses Snip worker from a separate test host; hovered internal control Enter copies exact frozen pixels.");
        }
        finally { hotkey.Triggered -= Open; }
    }

    private static void VerifyStaleAndManual(DesktopCaptureService desktop, Window owner, FrameworkElement button)
    {
        var target = DesktopWindows.Snapshot(includeCurrentProcess: true).Single(w => w.Handle == new WindowInteropHelper(owner).Handle);
        var stale = new DelayedProbe();
        using (var hover = new SelectionSession(desktop.Capture(), desktop, new AnnotationRenderer(), new RecordingExport(), [target], () => stale))
        {
            hover.Show();
            Wait(() => stale.Started, "Stale-result fixture did not start.");
            for (int i = 0; i < 1000; i++) hover.UpdateHoveredTarget(new(target.Bounds.X + 30 + i % 20, target.Bounds.Y + 30));
            Require(stale.Calls == 1, "Mouse motion queued concurrent accessibility work.");
            var outside = new PixelPoint(target.Bounds.Right + 25, target.Bounds.Y + 30);
            SetPhysicalCursorPos((int)outside.X, (int)outside.Y); hover.UpdateHoveredTarget(outside);
            stale.Result.SetResult(target.Bounds); Pause(150);
            Require(hover.ViewModel.DisplayedSelection.IsEmpty, "An old hover result selected a window after the pointer left it.");
        }
        var originalPoint = button.PointToScreen(new(20, 20));
        SetPhysicalCursorPos((int)originalPoint.X, (int)originalPoint.Y);
        var delayed = new DelayedProbe();
        using var selection = new SelectionSession(desktop.Capture(), desktop, new AnnotationRenderer(), new RecordingExport(), [target], () => delayed);
        selection.Show();
        var p = button.PointToScreen(new(20, 20)); selection.UpdateHoveredTarget(new(p.X, p.Y));
        Wait(() => delayed.Started, "Delayed provider did not start.");
        var manual = new PixelRect(target.Bounds.X + 15, target.Bounds.Y + 80, 120, 80);
        selection.ViewModel.SetSelection(manual); selection.BeginEditing();
        delayed.Result.SetResult(new(target.Bounds.X, target.Bounds.Y, 80, 80)); Pause(100);
        Require(selection.ViewModel.DisplayedSelection == manual && delayed.Disposed, "Late hover result overwrote a manual selection or retained its helper.");
        HotkeyChecks.Press(0x1B); Wait(() => selection.Completion.IsCompleted, "Escape did not cancel smart selection.");
        Console.WriteLine("PASS delayed provider result cannot overwrite manual editing; Escape closes the overlay and releases the provider.");
    }
    private static void VerifyTimeout(DesktopCaptureService desktop, Window owner)
    {
        var target = DesktopWindows.Snapshot(includeCurrentProcess: true).Single(w => w.Handle == new WindowInteropHelper(owner).Handle);
        int pid = 0, launches = 0, ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => ticks++; timer.Start();
        try
        {
            using var probe = new HoverRegionProbe(() =>
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!); info.ArgumentList.Add("--hover-stall"); return info;
            });
            probe.WorkerStarted += value => { pid = value; launches++; };
            var point = new PixelPoint(target.Bounds.X + 25, target.Bounds.Y + 25);
            SetPhysicalCursorPos((int)point.X, (int)point.Y);
            var frame = desktop.Capture();
            using var selection = new SelectionSession(frame, desktop, new AnnotationRenderer(), new RecordingExport(), [target], () => probe);
            var watch = Stopwatch.StartNew(); selection.Show();
            Wait(() => pid != 0, "Stalled provider did not start.");
            Wait(() => Exited(pid), "Timed out worker was not terminated.");
            Pause(100);
            Require(selection.ViewModel.DisplayedSelection == target.Bounds.Intersect(frame.Bounds) && ticks >= 15 && watch.Elapsed < TimeSpan.FromSeconds(5), "Provider timeout blocked the UI or lost the complete-window fallback.");
            for (int i = 0; i < 100; i++) selection.UpdateHoveredTarget(new(point.X + i % 10, point.Y));
            Pause(150);
            Require(launches == 1, "Moving after a provider failure restarted helpers repeatedly.");
            HotkeyChecks.Press(0x1B); Wait(() => selection.Completion.IsCompleted, "Escape failed after provider timeout.");
            Console.WriteLine($"PASS actual stalled helper timeout {watch.ElapsedMilliseconds} ms; UI heartbeat {ticks}; process terminated; no restart storm.");
        }
        finally { timer.Stop(); }
    }

    private static void SavePreview(SelectionWindow window)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/validation/ui-previews");
        using var file = File.Create("artifacts/validation/ui-previews/40-smart-selection.png"); encoder.Save(file);
    }
    private static byte[] Pixels(BitmapSource image) { var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes; }
    private static bool Exited(int pid) { if (pid == 0) return false; try { using var p = Process.GetProcessById(pid); return p.HasExited; } catch (ArgumentException) { return true; } }
    private static void Move(PixelPoint point) { Require(SetPhysicalCursorPos((int)point.X, (int)point.Y), "Native mouse move failed."); Pause(40); }
    private static void Wait(Func<bool> condition, string message) { try { TextWindowChecks.PumpUntil(condition, TimeSpan.FromSeconds(7)); } catch (TimeoutException) { throw new InvalidOperationException(message); } }
    private static void Pause(int milliseconds) { var task = Task.Delay(milliseconds); TextWindowChecks.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(2)); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class RecordingExport : IImageExportService
    {
        public int Copies;
        public void SavePng(BitmapSource image, string path) => throw new InvalidOperationException("No fixture save dialog expected.");
        public Task CopyAsync(BitmapSource image) { Copies++; return new ImageExportService().CopyAsync(image); }
    }
    private sealed class DelayedProbe : IHoverRegionProbe
    {
        public bool Started, Disposed;
        public int Calls;
        public TaskCompletionSource<PixelRect?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PixelRect?> FindAsync(WindowCaptureInfo target, PixelPoint point, CancellationToken token) { Calls++; Started = true; return Result.Task; }
        public void Dispose() => Disposed = true;
    }
    private sealed class RecordingCapture : IDesktopCaptureService
    {
        private readonly DesktopCaptureService inner = new();
        public DesktopFrame? Last;
        public IReadOnlyList<DisplayInfo> GetDisplays() => inner.GetDisplays();
        public DesktopFrame Capture() => Last = inner.Capture();
        public PixelPoint CursorPosition => inner.CursorPosition;
        public void PlaceOverlay(Window window, PixelRect bounds) => inner.PlaceOverlay(window, bounds);
    }
    private sealed class PromptDetector : IDisposable
    {
        private int seen;
        private readonly System.Threading.Timer timer;
        public bool Seen => Volatile.Read(ref seen) != 0;
        public PromptDetector()
        {
            timer = new System.Threading.Timer(_ =>
            {
                WindowCallback callback = (window, _) =>
                {
                    GetWindowThreadProcessId(window, out int pid);
                    if (pid != Environment.ProcessId) return true;
                    var name = new StringBuilder(64); GetClassName(window, name, name.Capacity);
                    if (name.ToString() == "#32770") { Interlocked.Exchange(ref seen, 1); PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); }
                    return true;
                };
                EnumWindows(callback, IntPtr.Zero);
            }, null, 0, 25);
        }
        public void Dispose() => timer.Dispose();
    }
    private sealed class NoDialogs : IEditorDialogs
    {
        public string? ChoosePngPath() => throw new InvalidOperationException("No save expected.");
        public TextInput? EditText(string text, string color, double fontSize) => throw new InvalidOperationException("No text dialog expected.");
        public CloseChoice ConfirmClose() => throw new InvalidOperationException("No confirmation expected.");
        public void ShowError(string message) => throw new InvalidOperationException("No error expected.");
    }
}
