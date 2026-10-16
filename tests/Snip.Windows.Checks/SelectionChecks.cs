using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.Services;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class SelectionChecks
{
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Wheel, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }

    // Called by the real global-hotkey check, inside its STA message pump.
    internal static void Verify(HotkeyService hotkey)
    {
        Ui.EnsureTheme();
        var desktop = new DesktopCaptureService();
        var originalCursor = desktop.CursorPosition;
        var first = new Window { Style = null, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = 80, Top = 120, Width = 240, Height = 180, Background = Brushes.CornflowerBlue };
        var second = new Window { Style = null, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = 360, Top = 120, Width = 240, Height = 180, Background = Brushes.OrangeRed };
        SelectionSession? session = null;
        RecordingExport? export = null;
        TaskCompletionSource? copyGate = null;
        DesktopFrame? frame = null;
        IReadOnlyList<WindowCaptureInfo> targets = [];
        void Open(object? sender, EventArgs args)
        {
            if (session is not null && !session.Completion.IsCompleted) return;
            export = new RecordingExport(copyGate);
            session = new SelectionSession(frame!, desktop, new AnnotationRenderer(), export, targets);
            session.Show();
        }
        void Start()
        {
            session?.Dispose(); session = null;
            first.Activate(); Move(first.PointToScreen(new Point(100, 90)));
            HotkeyChecks.Press(0x11, 0x12, 0x41);
            Wait(() => session is not null && Application.Current.Windows.OfType<SelectionWindow>().Any(w => w.IsActive));
        }
        void Copy(PixelRect expected)
        {
            // No click, focus call or pointer movement between opening the overlay and Enter.
            HotkeyChecks.Press(0x0D);
            Wait(() => session!.Completion.IsCompleted);
            Require(export!.Copies == 1 && Clipboard.ContainsImage(), "Enter did not publish exactly one image.");
            var actual = Clipboard.GetImage();
            var wanted = new CroppedBitmap(frame!.Image, new Int32Rect((int)(expected.X - frame.Bounds.X), (int)(expected.Y - frame.Bounds.Y), (int)expected.Width, (int)expected.Height));
            Require(actual.PixelWidth == wanted.PixelWidth && actual.PixelHeight == wanted.PixelHeight && Pixels(actual).SequenceEqual(Pixels(wanted)), "Copied pixels differ from the highlighted frozen desktop region.");
            Require(!Application.Current.Windows.OfType<SelectionWindow>().Any(), "Successful copy left an overlay open.");
        }
        try
        {
            first.Show(); second.Show(); first.Activate(); first.UpdateLayout(); second.UpdateLayout();
            var firstPoint = first.PointToScreen(new Point(100, 90)); Move(firstPoint);
            var one = DesktopWindows.AtPhysicalPoint(new(firstPoint.X, firstPoint.Y))!;
            var secondPoint = second.PointToScreen(new Point(100, 90));
            var two = DesktopWindows.AtPhysicalPoint(new(secondPoint.X, secondPoint.Y))!;
            Require(one.Handle == new WindowInteropHelper(first).Handle && two.Handle == new WindowInteropHelper(second).Handle, "Public target windows are not exposed at the fixture points.");
            targets = [one, two]; frame = desktop.Capture();
            var expectedOne = one.Bounds.RoundOut().Intersect(frame.Bounds);
            var expectedTwo = two.Bounds.RoundOut().Intersect(frame.Bounds);
            Require(hotkey.Register(HotkeyGesture.Default), "Could not register Ctrl+Alt+A for capture.");
            hotkey.Triggered += Open;

            Start();
            Require(session!.ViewModel.Selection.IsEmpty && session.Editor is null && session.ViewModel.DisplayedSelection == expectedOne, "Default hover region was not initialized before pointer input.");
            Copy(expectedOne);
            Console.WriteLine("PASS real Ctrl+Alt+A then Enter without click/move copies the initial highlighted window pixel-for-pixel and closes all overlays.");

            Start(); Move(secondPoint);
            Wait(() => session!.ViewModel.DisplayedSelection == expectedTwo);
            Copy(expectedTwo);
            Console.WriteLine("PASS native hover changes the default target; Enter copies the displayed target instead of the previous window.");

            Start();
            var start = first.PointToScreen(new Point(30, 40)); var end = first.PointToScreen(new Point(180, 130));
            Move(start); Mouse(0x0002); Wait(() => session!.ViewModel.IsDragging);
            Move(end); HotkeyChecks.Press(0x0D); Pause();
            Require(!session!.Completion.IsCompleted && export!.Copies == 0 && session.ViewModel.IsDragging, "Enter copied an unfinished mouse drag.");
            Mouse(0x0004); Wait(() => session!.Editing);
            var dragged = new PixelRect(start.X, start.Y, end.X - start.X, end.Y - start.Y).RoundOut().Intersect(frame.Bounds);
            Require(session!.ViewModel.Selection == dragged, "Mouse re-selection changed to the default target.");
            Copy(dragged);
            Console.WriteLine("PASS native mouse re-selection still edits and copies its exact region; Enter while dragging does not copy.");

            Clipboard.SetText("Public selection cancellation sentinel"); Start(); HotkeyChecks.Press(0x1B);
            Wait(() => session!.Completion.IsCompleted);
            Require(export!.Copies == 0 && Clipboard.GetText() == "Public selection cancellation sentinel", "Esc changed the existing clipboard.");
            targets = []; Start(); HotkeyChecks.Press(0x0D); Pause();
            Require(!session!.Completion.IsCompleted && export!.Copies == 0, "Enter invented a region when no target was available.");
            HotkeyChecks.Press(0x1B); Wait(() => session!.Completion.IsCompleted);
            Console.WriteLine("PASS Esc preserves clipboard; Enter with no available region keeps selection open for manual capture.");

            targets = [one, two]; copyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Start(); HotkeyChecks.Press(0x0D); Wait(() => export!.Copies == 1);
            HotkeyChecks.Press(0x0D); Pause();
            Require(export!.Copies == 1 && !session!.Completion.IsCompleted, "Repeated Enter started concurrent clipboard exports.");
            copyGate.SetResult(); Wait(() => session!.Completion.IsCompleted);
            Require(export!.Copies == 1 && Clipboard.GetImage().PixelWidth == expectedOne.Width, "Delayed clipboard export did not complete.");
            Console.WriteLine("PASS repeated native Enter during a pending clipboard export performs only one copy.");
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/selection-enter.json", JsonSerializer.Serialize(new { InitialTargetWidth = expectedOne.Width, InitialTargetHeight = expectedOne.Height, DirectEnter = true, FrozenPixelsMatch = true, HoverSwitch = true, MouseReselect = true, DragEnterIgnored = true, EscapePreservesClipboard = true, EmptyTargetIgnored = true, DuplicateEnterIgnored = true }));
        }
        finally
        {
            hotkey.Triggered -= Open; copyGate?.TrySetResult(); session?.Dispose();
            Mouse(0x0004); first.Close(); second.Close();
            SetPhysicalCursorPos((int)originalCursor.X, (int)originalCursor.Y);
        }
    }
    private static void Move(Point point)
    {
        Require(SetPhysicalCursorPos((int)point.X, (int)point.Y), "Could not move native fixture pointer."); Pause();
    }
    private static void Mouse(uint flags)
    {
        Input[] inputs = [new() { Type = 0, Data = new() { Mouse = new() { Flags = flags } } }];
        Require(SendInput(1, inputs, Marshal.SizeOf<Input>()) == 1, "Native mouse button was rejected.");
    }
    private static void Wait(Func<bool> done) => TextWindowChecks.PumpUntil(done, TimeSpan.FromSeconds(6));
    private static void Pause() { var task = Task.Delay(100); Wait(() => task.IsCompleted); }
    private static byte[] Pixels(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        var data = new byte[source.PixelWidth * source.PixelHeight * 3]; source.CopyPixels(data, source.PixelWidth * 3, 0); return data;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class RecordingExport(TaskCompletionSource? gate) : IImageExportService
    {
        public int Copies { get; private set; }
        public void SavePng(BitmapSource image, string path) => throw new NotSupportedException();
        public async Task CopyAsync(BitmapSource image)
        {
            Copies++;
            if (gate is not null) await gate.Task;
            await new ImageExportService().CopyAsync(image);
        }
    }
}
