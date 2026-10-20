using System.Diagnostics;
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
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class HoverRegionChecks
{
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
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
            var expected = Bounds(button); int releasedPid = workerPid;
            HotkeyChecks.Press(0x0D);
            Wait(() => session.Completion.IsCompleted && Clipboard.ContainsImage(), "Enter did not copy the hovered internal region.");
            var result = Clipboard.GetImage();
            var crop = new CroppedBitmap(frame!.Image, new Int32Rect((int)(expected.X - frame.Bounds.X), (int)(expected.Y - frame.Bounds.Y), (int)expected.Width, (int)expected.Height));
            Require(export!.Copies == 1 && result.PixelWidth == crop.PixelWidth && result.PixelHeight == crop.PixelHeight && Pixels(result).SequenceEqual(Pixels(crop)), "Smart Enter copied different pixels from the highlighted frozen region.");
            Wait(() => Exited(releasedPid), "Smart selection worker survived copy completion.");
            Console.WriteLine("PASS native smart hover: toolbar button, text input, list, list row and panel; Enter copies the frozen internal region without a click; helper exits.");

            VerifyStaleAndManual(desktop, window, button);
            VerifyTimeout(desktop, window);
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/smart-selection.json", JsonSerializer.Serialize(new {
                ToolbarButton = true, TextInput = true, List = true, ListRow = true, Panel = true,
                EnterCopiesFrozenRegion = true, WorkerReleased = true, StaleResultsIgnored = true,
                ManualSelectionPreserved = true, EscapeResponsive = true, TimeoutFallsBack = true,
                NoRestartStorm = true, WarmHoverMilliseconds = times
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            hotkey.Triggered -= Open; foreach (var owned in sessions) owned.Dispose(); window.Close();
            SetPhysicalCursorPos((int)originalCursor.X, (int)originalCursor.Y);
        }
    }

    private static void VerifyStaleAndManual(DesktopCaptureService desktop, Window owner, FrameworkElement button)
    {
        var target = DesktopWindows.Snapshot(includeCurrentProcess: true).Single(w => w.Handle == new WindowInteropHelper(owner).Handle);
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
            var watch = Stopwatch.StartNew(); var task = probe.FindAsync(target, point, default);
            Wait(() => task.IsCompleted, "Stalled provider did not time out.");
            Require(task.GetAwaiter().GetResult() is null && ticks >= 15 && watch.Elapsed < TimeSpan.FromSeconds(5), "Provider timeout blocked the UI or returned an invented region.");
            Wait(() => Exited(pid), "Timed out worker was not terminated.");
            var retry = probe.FindAsync(target, point, default); Wait(() => retry.IsCompleted, "Disabled provider did not complete.");
            Require(retry.GetAwaiter().GetResult() is null && launches == 1, "Moving after a provider failure restarted helpers repeatedly.");
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
        public TaskCompletionSource<PixelRect?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PixelRect?> FindAsync(WindowCaptureInfo target, PixelPoint point, CancellationToken token) { Started = true; return Result.Task; }
        public void Dispose() => Disposed = true;
    }
}
