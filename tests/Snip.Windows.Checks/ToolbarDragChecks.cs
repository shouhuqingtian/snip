using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class ToolbarDragChecks
{
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Wheel, Flags, Time; public UIntPtr Extra; }
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Ui.EnsureTheme();
        var desktop = new DesktopCaptureService(); var cursor = desktop.CursorPosition;
        var display = desktop.GetDisplays().First(d => d.Primary);
        var owner = new Window { Style = null, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Brushes.White };
        var publicText = new StackPanel { Margin = new Thickness(36, 28, 36, 0) };
        publicText.Children.Add(new TextBlock { Text = "Public text revealed by moving the toolbar", FontSize = 26, Foreground = Brushes.DarkSlateGray });
        for (int i = 0; i < 10; i++) publicText.Children.Add(new TextBlock { Text = $"Public fixture row {i + 1}: capture, annotate, and copy.", FontSize = 18, Margin = new Thickness(0, 15, 0, 0) });
        owner.Content = publicText;
        SelectionSession? session = null; var export = new RecordingExport();
        using var watchdog = new System.Threading.Timer(_ => { Console.Error.WriteLine("FAIL toolbar drag deadline."); Environment.Exit(1); }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        try
        {
            owner.Show(); desktop.PlaceOverlay(owner, display.Bounds); owner.Activate(); owner.UpdateLayout(); Pause();
            var frame = desktop.Capture();
            session = new SelectionSession(frame, desktop, new AnnotationRenderer(), export, []);
            session.Show(); session.ViewModel.SetSelection(display.Bounds); session.BeginEditing(); Pause();
            var window = app.Windows.OfType<SelectionWindow>().Single(w => w.Display.Name == display.Name);
            window.Activate(); window.UpdateLayout();
            var toolbar = TextWindowChecks.Descendants<EditorToolbar>(window).Single();
            var grip = TextWindowChecks.Descendants<Border>(toolbar).Single(b => b.Name == "ToolbarDragHandle");
            var original = Position(toolbar); double width = window.Width, height = window.Height;
            var selection = session.ViewModel.Selection; var source = Pixels(session.Editor!.Compose());
            var start = Center(grip); Move(start); Buttons(2); Move(start + new Vector(0, 140));
            Require(Mouse.Captured == toolbar && Position(toolbar).Y > original.Y + 60, "Native grip drag did not detach the toolbar.");
            HotkeyChecks.Press(0x0D); Buttons(0x20, 0x40); Pause();
            Require(!session.Completion.IsCompleted && export.Copies == 0, "Enter or middle click copied during toolbar drag.");
            Buttons(4); var placed = Position(toolbar); Pause();
            Require(Mouse.Captured is null && Position(toolbar) == placed && session.ViewModel.Selection == selection && session.Editor.BaseAnnotations.Count == 0, "Toolbar drag changed the selection, drew a mark, or snapped back.");
            CheckBounds(window, toolbar);
            Console.WriteLine("PASS native toolbar grip drag reveals covered public text, preserves selection/annotations, ignores Enter/middle while dragging and stays placed after release.");

            var arrow = TextWindowChecks.Descendants<RadioButton>(toolbar).Single(b => Equals(b.ToolTip, "箭头")); Click(arrow);
            Require(session.Editor.Tool == AnnotationTool.Arrow && Position(toolbar) == placed, "Tool click was consumed by dragging or reset the position.");
            var stroke = TextWindowChecks.Descendants<ComboBox>(toolbar).First(); Click(stroke); Wait(() => stroke.IsDropDownOpen);
            var lastWidth = (ComboBoxItem)stroke.ItemContainerGenerator.ContainerFromIndex(stroke.Items.Count - 1); Click(lastWidth);
            Require(session.Editor.StrokeWidth == 8 && Position(toolbar) == placed, $"Stroke dropdown operation failed: width={session.Editor.StrokeWidth}, selected={stroke.SelectedItem}, position={Position(toolbar)}, expected={placed}, popup={stroke.IsDropDownOpen}.");
            var color = TextWindowChecks.Descendants<Button>(toolbar).Single(b => Equals(b.ToolTip, "#007AFF")); Click(color);
            Require(session.Editor.Color == "#007AFF" && Position(toolbar) == placed, "Color selection was intercepted or moved the toolbar.");
            var blank = toolbar.PointToScreen(new Point(toolbar.ActualWidth / 2, toolbar.ActualHeight - 3));
            Move(blank); Buttons(2); Move(blank + new Vector(0, 65)); Buttons(4);
            Require(Position(toolbar).Y > placed.Y + 30 && !session.Completion.IsCompleted && session.Editor.BaseAnnotations.Count == 0, "Blank padding could not move the toolbar safely.");
            placed = Position(toolbar); CheckBounds(window, toolbar); Preview(window);
            Console.WriteLine("PASS native tool, linewidth dropdown and palette keep normal behavior; blank toolbar padding also drags without drawing or confirming.");

            start = Center(grip); Move(start); Buttons(2); Move(start + new Vector(0, -70)); HotkeyChecks.Press(0x1B); Buttons(4);
            Require(!session.Completion.IsCompleted && Position(toolbar) == placed && Mouse.Captured is null, "Esc during a drag did not restore the previous position and keep the screenshot open.");
            start = Center(grip); Move(start); Buttons(2); Move(start + new Vector(0, -60)); Mouse.Capture(null); Buttons(4); Pause();
            Require(Position(toolbar) == placed && !session.Completion.IsCompleted, "Lost mouse capture left a stale drag or lost the previous placement.");
            Console.WriteLine("PASS native Esc cancels only the active toolbar move; real WPF capture loss restores placement and releases drag state.");

            Drag(Center(grip), new Point(display.Bounds.X + 2, display.Bounds.Y + 2)); CheckBounds(window, toolbar);
            Drag(Center(grip), new Point(display.Bounds.Right - 2, display.Bounds.Bottom - 2)); CheckBounds(window, toolbar);
            window.Width = Math.Min(width, 720); window.Height = Math.Min(height, 540); window.UpdateLayout(); Pause(); CheckBounds(window, toolbar);
            var scaledBefore = Position(toolbar);
            start = Center(grip); Move(start); Buttons(2); Move(start + new Vector(0, -65)); Buttons(4); CheckBounds(window, toolbar);
            Require(Position(toolbar) != scaledBefore && session.ViewModel.Selection == selection, "Native drag failed after physical-image/overlay scale changed.");
            Require(Pixels(session.Editor.Compose()).SequenceEqual(source), "Toolbar movement or viewport scaling changed the captured pixels.");
            Console.WriteLine("PASS native edge dragging and resized overlay stay on screen, avoid the hint and preserve source pixel coordinates.");

            window.Width = width; window.Height = height; window.UpdateLayout();
            var adjust = TextWindowChecks.Descendants<Button>(toolbar).Single(b => Equals(b.ToolTip, "重新调整选区（清除当前标注）")); Click(adjust);
            Require(!session.Editing && !toolbar.IsVisible && Mouse.Captured is null, "Reselection did not hide the dragged toolbar.");
            session.ViewModel.SetSelection(selection); session.BeginEditing(); window.UpdateLayout(); Pause();
            Require(Position(toolbar) == original && toolbar.IsVisible, "A new selection inherited the old manually detached toolbar position.");
            Drag(Center(grip), Center(grip) + new Vector(0, 120));
            HotkeyChecks.Press(0x0D); Wait(() => session.Completion.IsCompleted);
            Require(export.Copies == 1 && Clipboard.ContainsImage() && Pixels(Clipboard.GetImage()).SequenceEqual(source), "Final native Enter did not copy the exact frozen pixels after moving the toolbar.");
            Require(!app.Windows.OfType<SelectionWindow>().Any() && Mouse.Captured is null, "Completed capture retained an overlay or mouse capture.");
            Console.WriteLine("PASS native reselection resets floating placement; Enter after drag release copies exact source pixels and closes all overlays.");
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/toolbar-drag.json", JsonSerializer.Serialize(new
            {
                NativeGripDrag = true, BlankPaddingDrag = true, PlacementPersists = true, ControlsNotIntercepted = true,
                SelectionUnchanged = true, NoAccidentalAnnotation = true, EnterMiddleIgnoredDuringDrag = true,
                EscapeRestoresPosition = true, CaptureLossRestoresPosition = true, EdgesClamped = true, HintAvoided = true,
                ResizedOverlayCoordinates = true, ReselectionResetsPosition = true, ExactCopiedPixels = true, CaptureReleased = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Buttons(4); session?.Dispose(); owner.Close(); SetPhysicalCursorPos((int)cursor.X, (int)cursor.Y); SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static Point Position(EditorToolbar toolbar) => new(Canvas.GetLeft(toolbar), Canvas.GetTop(toolbar));
    private static Point Center(FrameworkElement element) => element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
    private static void Click(FrameworkElement element) { Move(Center(element)); Buttons(2, 4); }
    private static void Drag(Point start, Point end) { Move(start); Buttons(2); Move(end); Buttons(4); }
    private static void Move(Point point) { Require(SetPhysicalCursorPos((int)point.X, (int)point.Y), "Native pointer movement failed."); Pause(); }
    private static void Buttons(params uint[] flags)
    {
        foreach (uint flag in flags)
        {
            Input[] input = [new() { Data = new() { Mouse = new() { Flags = flag } } }];
            Require(SendInput(1, input, Marshal.SizeOf<Input>()) == 1, "Native pointer input failed."); Pause();
        }
    }
    private static void Wait(Func<bool> done) => TextWindowChecks.PumpUntil(done, TimeSpan.FromSeconds(6));
    private static void Pause() { var delay = Task.Delay(75); Wait(() => delay.IsCompleted); }
    private static byte[] Pixels(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        var bytes = new byte[source.PixelWidth * source.PixelHeight * 3]; source.CopyPixels(bytes, source.PixelWidth * 3, 0); return bytes;
    }
    private static void CheckBounds(Window window, EditorToolbar toolbar)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var hint = TextWindowChecks.Descendants<Border>(window).Single(b => b.Name == "SelectionHint");
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
        var bounds = Bounds(toolbar); var hintBounds = Bounds(hint);
        Require(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= content.ActualWidth + 1 && bounds.Bottom <= content.ActualHeight + 1 && !bounds.IntersectsWith(hintBounds), "Dragged toolbar left the viewport or overlapped the hint.");
    }
    private static void Preview(Window window)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        Directory.CreateDirectory("artifacts/validation/ui-previews");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create("artifacts/validation/ui-previews/57-toolbar-drag.png"); encoder.Save(file);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class RecordingExport : IImageExportService
    {
        public int Copies { get; private set; }
        public void SavePng(BitmapSource image, string path) => throw new NotSupportedException();
        public async Task CopyAsync(BitmapSource image) { Copies++; await new ImageExportService().CopyAsync(image); }
    }
}
