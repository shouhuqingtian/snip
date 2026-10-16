using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class SelfCaptureChecks
{
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);

    internal static void Verify(HotkeyService hotkey)
    {
        var app = Application.Current;
        var desktop = new RecordingCapture();
        var previousCursor = desktop.CursorPosition;
        var export = new ImageExportService();
        var renderer = new AnnotationRenderer();
        using var pins = new PinManager(export);
        using var coordinator = new CaptureCoordinator(desktop, renderer, export, new FeatureHub(renderer, export, desktop, pins));
        var main = new MainWindow { AllowClose = true, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 20 };
        var result = new Window { Style = null, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = 720, Top = 100, Width = 240, Height = 180, Background = Brushes.OrangeRed };
        var pin = new PinWindow(GreenImage(), export) { Left = 720, Top = 340 };
        Task? active = null;
        Exception? commandError = null;
        main.DataContext = new MainViewModel(() => active = coordinator.CaptureAsync(), error => commandError = error);
        void Open(object? sender, EventArgs args) => active = coordinator.CaptureAsync();
        void WaitOverlay() => Wait(() => app.Windows.OfType<SelectionWindow>().Any(w => w.IsActive));
        void Start(Window target)
        {
            active = null; main.Activate(); Move(target.PointToScreen(new Point(100, 90)));
            HotkeyChecks.Press(0x11, 0x12, 0x41); WaitOverlay();
            Require(main.IsVisible && result.IsVisible && pin.IsVisible, "Hotkey hid one of the application's visible windows.");
        }
        PixelRect Bounds(Window target)
        {
            var item = DesktopWindows.Snapshot(includeCurrentProcess: true).Single(w => w.Handle == new WindowInteropHelper(target).Handle);
            var displays = desktop.GetDisplays();
            double x = displays.Min(d => d.Bounds.X), y = displays.Min(d => d.Bounds.Y);
            var bounds = new PixelRect(x, y, displays.Max(d => d.Bounds.Right) - x, displays.Max(d => d.Bounds.Bottom) - y);
            return item.Bounds.RoundOut().Intersect(bounds);
        }
        void Copy(PixelRect region)
        {
            HotkeyChecks.Press(0x0D); Wait(() => active?.IsCompleted == true); active!.GetAwaiter().GetResult();
            Require(!app.Windows.OfType<SelectionWindow>().Any(), "Self capture left overlays alive.");
            var actual = Clipboard.GetImage(); var frame = desktop.Last!;
            var expected = new CroppedBitmap(frame.Image, new Int32Rect((int)(region.X - frame.Bounds.X), (int)(region.Y - frame.Bounds.Y), (int)region.Width, (int)region.Height));
            Require(actual is not null && actual.PixelWidth == expected.PixelWidth && actual.PixelHeight == expected.PixelHeight && Pixels(actual).SequenceEqual(Pixels(expected)), "Own window was missing or clipboard differs from its exact frozen desktop pixels.");
        }
        try
        {
            main.Show(); result.Show(); pin.Show(); main.Activate(); main.UpdateLayout(); result.UpdateLayout(); pin.UpdateLayout(); Pause();
            var mainBounds = Bounds(main); var resultBounds = Bounds(result); var pinBounds = Bounds(pin);
            Require(DesktopWindows.Snapshot().All(w => w.ProcessId != Environment.ProcessId), "Default browser target enumeration no longer excludes its own process.");
            Require(hotkey.Register(HotkeyGesture.Default), "Cannot register self-capture shortcut."); hotkey.Triggered += Open;

            Start(main); Copy(mainBounds);
            Require(main.IsVisible && result.IsVisible && pin.IsVisible, "Copy changed existing application window visibility.");
            Console.WriteLine("PASS real coordinator Ctrl+Alt+A captures and auto-selects actual MainWindow without hiding it; Enter copies its complete frozen pixels.");

            Start(result); Copy(resultBounds);
            var captured = Clipboard.GetImage();
            byte[] center = new byte[4]; new FormatConvertedBitmap(captured, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(captured.PixelWidth / 2, captured.PixelHeight / 2, 1, 1), center, 4, 0);
            Require(center[2] == 255 && center[1] == 69 && center[0] == 0, "Own result window was replaced by the desktop behind it.");
            Console.WriteLine("PASS real coordinator captures another same-process result window instead of hiding every application window.");

            active = null; main.Activate(); Move(pin.PointToScreen(new Point(60, 40)));
            HotkeyChecks.Press(0x11, 0x12, 0x41); WaitOverlay(); Copy(pinBounds);
            Console.WriteLine("PASS actual topmost PinWindow remains in the screenshot and is automatically selected; clipboard pixels match.");

            active = null; main.Activate(); Move(result.PointToScreen(new Point(100, 90)));
            var button = (Button)main.FindName("StartCaptureButton");
            Wait(() => button.IsEnabled);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); WaitOverlay();
            Require(main.IsVisible && result.IsVisible && pin.IsVisible, "Main capture button hid its launcher or another visible application window.");
            Copy(resultBounds);
            Require(main.IsVisible && commandError is null, "Main launcher did not remain visible after capture.");
            Console.WriteLine("PASS actual MainWindow StartCaptureButton keeps the launcher, result and pin windows visible and capturable.");

            active = null; main.Activate(); Move(main.PointToScreen(new Point(100, 90)));
            Wait(() => button.IsEnabled);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); WaitOverlay();
            Copy(mainBounds);
            Require(main.IsVisible && commandError is null, "The launcher could not capture itself from its own button.");
            Console.WriteLine("PASS actual StartCaptureButton can capture its own MainWindow; default target and copied frozen pixels match.");

            Clipboard.SetText("Public self-capture cancellation sentinel");
            active = coordinator.CaptureAsync(); WaitOverlay(); HotkeyChecks.Press(0x1B); Wait(() => active.IsCompleted); active.GetAwaiter().GetResult();
            Require(main.IsVisible && result.IsVisible && pin.IsVisible && Clipboard.GetText() == "Public self-capture cancellation sentinel", "Cancel changed launcher visibility or clipboard.");
            desktop.Fail = true; active = coordinator.CaptureAsync(); Wait(() => active.IsCompleted);
            bool failed = false; try { active.GetAwaiter().GetResult(); } catch (IOException) { failed = true; }
            Require(failed && main.IsVisible && result.IsVisible && pin.IsVisible, "Capture failure changed application window visibility.");
            Console.WriteLine("PASS native Esc and simulated desktop capture failure preserve all application windows; cancel preserves clipboard.");
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/self-capture.json", JsonSerializer.Serialize(new { HotkeyCapturesMain = true, HotkeyCapturesResult = true, HotkeyCapturesTopmostPin = true, FrozenPixelsMatch = true, MainButtonPreservesLauncher = true, MainButtonCapturesItself = true, LauncherVisibleAfterCopy = true, LauncherVisibleAfterCancel = true, LauncherVisibleAfterFailure = true, BrowserEnumerationPreserved = true }));
        }
        finally
        {
            hotkey.Triggered -= Open; coordinator.Dispose(); pin.Close(); result.Close(); main.Close();
            SetPhysicalCursorPos((int)previousCursor.X, (int)previousCursor.Y);
        }
    }
    private static void Move(Point point)
    { Require(SetPhysicalCursorPos((int)point.X, (int)point.Y), "Native pointer placement failed."); Pause(); }
    private static void Wait(Func<bool> done) => TextWindowChecks.PumpUntil(done, TimeSpan.FromSeconds(8));
    private static void Pause() { var task = Task.Delay(100); Wait(() => task.IsCompleted); }
    private static BitmapSource GreenImage()
    {
        var data = new byte[120 * 80 * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i + 1] = 128; data[i + 3] = 255; }
        var image = BitmapSource.Create(120, 80, 96, 96, PixelFormats.Bgra32, null, data, 480); image.Freeze(); return image;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        var data = new byte[source.PixelWidth * source.PixelHeight * 3]; source.CopyPixels(data, source.PixelWidth * 3, 0); return data;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class RecordingCapture : IDesktopCaptureService
    {
        private readonly DesktopCaptureService desktop = new();
        public DesktopFrame? Last { get; private set; }
        public bool Fail { get; set; }
        public IReadOnlyList<DisplayInfo> GetDisplays() => desktop.GetDisplays();
        public PixelPoint CursorPosition => desktop.CursorPosition;
        public void PlaceOverlay(Window window, PixelRect bounds) => desktop.PlaceOverlay(window, bounds);
        public DesktopFrame Capture() => Fail ? throw new IOException("Public simulated capture failure.") : Last = desktop.Capture();
    }
}
