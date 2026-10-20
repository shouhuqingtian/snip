using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class AnnotationInteractionChecks
{
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Wheel, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }

    internal static int Run()
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Ui.EnsureTheme();
        string directory = Path.Combine(Path.GetTempPath(), "Snip.Annotation-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var renderer = new AnnotationRenderer(); var dialogs = new Dialogs(Path.Combine(directory, "capture.png"));
        var vm = new EditorViewModel(Image(1000, 700), renderer, new ImageExportService(), dialogs);
        var window = new EditorWindow { DataContext = vm, Height = 860 };
        string stage = "opening";
        using var watchdog = new System.Threading.Timer(_ => { Console.WriteLine("::error title=Annotation input deadline::" + Volatile.Read(ref stage)); Environment.Exit(1); }, null, TimeSpan.FromSeconds(75), Timeout.InfiniteTimeSpan);
        try
        {
            window.Show(); window.Activate(); window.UpdateLayout(); vm.Zoom = .75; window.UpdateLayout(); Pause();
            var canvas = TextWindowChecks.Descendants<EditorCanvas>(window).Single();
            var viewport = (ScrollViewer)window.FindName("Viewport");
            void Move(PixelPoint point)
            {
                var screen = canvas.PointToScreen(new Point(point.X, point.Y));
                Require(SetPhysicalCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)), "Native pointer rejected."); Pause();
            }
            void Click(PixelPoint point) { Move(point); Mouse(2, 4); Pause(); }
            void Reveal(PixelPoint point, double scale)
            {
                viewport.ScrollToHorizontalOffset(Math.Max(0, point.X * scale - viewport.ViewportWidth / 2));
                viewport.ScrollToVerticalOffset(Math.Max(0, point.Y * scale - viewport.ViewportHeight / 2));
                window.UpdateLayout(); Pause();
            }
            void Drag(PixelPoint start, PixelPoint end)
            {
                Move(start); Mouse(2); Pause(); Move(end); Mouse(4); Wait(() => !vm.IsDrawing);
            }
            void Choose(AnnotationTool tool, string tip)
            {
                var button = TextWindowChecks.Descendants<RadioButton>(window.Toolbar).Single(b => Equals(b.ToolTip, tip));
                var screen = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                Require(SetPhysicalCursorPos((int)screen.X, (int)screen.Y), "Cannot reach tool button."); Mouse(2, 4); Wait(() => vm.Tool == tool);
            }
            void Undo() { HotkeyChecks.Press(0x11, 0x5A); Pause(); }
            void Near(double expected, double actual) => Require(Math.Abs(expected - actual) <= 3, $"Native geometry mismatch: {expected:0.##}/{actual:0.##}.");
            var tools = TextWindowChecks.Descendants<RadioButton>(window.Toolbar).ToArray();
            Require(tools.Length == 6 && Equals(tools[0].ToolTip, "矩形") && tools.All(b => !Equals(b.ToolTip, "选择 / 移动 / 缩放标注")), "Selection button remains or a drawing tool is missing.");

            stage = "direct rectangle selection and movement";
            Drag(new(100, 100), new(500, 400));
            var original = vm.BaseAnnotations.Single(); Require(vm.Save(), "Initial save failed.");
            Click(new(100, 180));
            Require(vm.Selected?.Id == original.Id && !vm.IsDirty, "Click selection created an edit or did not select the rectangle.");
            Drag(new(100, 180), new(101, 181)); Require(!vm.IsDirty, "One-pixel jitter moved the selected annotation.");
            Drag(new(100, 180), new(150, 210));
            var moved = vm.BaseAnnotations.Single(); Near(original.Start.X + 50, moved.Start.X); Near(original.Start.Y + 30, moved.Start.Y);
            Require(vm.Tool == AnnotationTool.Rectangle, "Direct move switched away from the drawing tool.");
            Console.WriteLine("PASS native rectangle outline click/drag selects and moves without a selection button; click and one-pixel jitter do not dirty history; drawing tool remains active.");

            stage = "resize, undo, redo and delete";
            Drag(moved.End, moved.End + new PixelPoint(80, 60));
            var resized = vm.BaseAnnotations.Single(); Near(moved.Bounds.Width + 80, resized.Bounds.Width); Near(moved.Bounds.Height + 60, resized.Bounds.Height);
            Undo(); Require(vm.BaseAnnotations.Single() == moved, "Resize undo lost the moved rectangle.");
            HotkeyChecks.Press(0x11, 0x59); Pause(); Require(vm.BaseAnnotations.Single() == resized, "Resize redo failed.");
            Click(new(resized.Start.X, resized.Start.Y + 60)); HotkeyChecks.Press(0x2E); Wait(() => vm.BaseAnnotations.Count == 0);
            Undo(); Require(vm.BaseAnnotations.Single() == resized, "Delete undo failed.");
            Console.WriteLine("PASS native resize points, Ctrl+Z/Ctrl+Y and Delete preserve exactly one transform per gesture and restore original annotations.");

            stage = "hollow and filled shapes";
            Drag(new(240, 220), new(340, 280)); Require(vm.BaseAnnotations.Count == 2 && vm.BaseAnnotations[0] == resized, "Hollow rectangle interior stole the new drawing.");
            vm.Filled = true; Drag(new(700, 100), new(900, 240)); var filled = vm.BaseAnnotations.Last();
            Drag(new(740, 150), new(760, 180)); var filledMoved = vm.BaseAnnotations.Last();
            Require(vm.BaseAnnotations.Count == 3 && filledMoved.Id == filled.Id, "Filled rectangle body did not move."); Near(filled.Start.X + 20, filledMoved.Start.X);
            Choose(AnnotationTool.Ellipse, "椭圆"); Drag(new(700, 400), new(900, 600));
            int beforeCorner = vm.BaseAnnotations.Count; Drag(new(704, 404), new(730, 430));
            Require(vm.BaseAnnotations.Count == beforeCorner + 1, "Empty corner outside a filled ellipse stole the new drawing."); vm.Filled = false;
            Console.WriteLine("PASS native hollow interiors keep drawing, filled rectangle bodies move, and empty filled-ellipse bounding-box corners stay available.");

            stage = "text double click and movement";
            Choose(AnnotationTool.Text, "文字（Ctrl+Enter 提交）"); Click(new(100, 520));
            Wait(() => TextWindowChecks.Descendants<TextBox>(canvas).Any());
            TextWindowChecks.Descendants<TextBox>(canvas).Single().Text = "Public text fixture";
            HotkeyChecks.Press(0x11, 0x0D); Wait(() => vm.BaseAnnotations.Any(a => a.Tool == AnnotationTool.Text)); Pause();
            Move(new(110, 530)); Mouse(2, 4, 2, 4); Wait(() => TextWindowChecks.Descendants<TextBox>(canvas).Any());
            TextWindowChecks.Descendants<TextBox>(canvas).Single().Text = "Changed public fixture";
            HotkeyChecks.Press(0x11, 0x0D); Wait(() => !TextWindowChecks.Descendants<TextBox>(canvas).Any());
            var text = vm.BaseAnnotations.Single(a => a.Tool == AnnotationTool.Text);
            Require(text.Text == "Changed public fixture", "Double click did not edit the existing text.");
            Drag(text.Start + new PixelPoint(40, 12), text.Start + new PixelPoint(80, 32));
            var textMoved = vm.BaseAnnotations.Single(a => a.Tool == AnnotationTool.Text); Near(text.Start.X + 40, textMoved.Start.X);
            Require(textMoved.Id == text.Id && vm.Tool == AnnotationTool.Text, "Text move created another text object or changed tools.");
            Console.WriteLine("PASS native text creation, direct double-click editing and dragging work while the text tool stays active.");

            stage = "sticker and drawing priority";
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Image(32, 24))); using var png = new MemoryStream(); encoder.Save(png);
            vm.AddSticker(png.ToArray(), 32, 24); var sticker = vm.BaseAnnotations.Last();
            var middle = new PixelPoint(sticker.Bounds.X + sticker.Bounds.Width / 2, sticker.Bounds.Y + sticker.Bounds.Height / 2);
            Drag(middle, middle + new PixelPoint(40, 30)); Near(sticker.Start.X + 40, vm.BaseAnnotations.Last().Start.X);
            Require(vm.Tool != AnnotationTool.Select, "Adding a sticker entered an invisible legacy selection mode.");
            Choose(AnnotationTool.Pen, "画笔"); int beforePen = vm.BaseAnnotations.Count;
            Drag(new(150, 230), new(170, 240)); Drag(new(170, 240), new(190, 250));
            Require(vm.BaseAnnotations.Count == beforePen + 2 && vm.BaseAnnotations[0] == resized, "Continuous pen gestures moved previous annotations.");
            Choose(AnnotationTool.Mosaic, "马赛克");
            Drag(textMoved.Start + new PixelPoint(12, 12), textMoved.Start + new PixelPoint(100, 36));
            Require(vm.BaseAnnotations.Last().Tool == AnnotationTool.Mosaic && vm.BaseAnnotations.Single(a => a.Id == text.Id) == textMoved, "Mosaic moved text instead of covering it.");
            Console.WriteLine("PASS native sticker drag avoids hidden selection mode; consecutive pen strokes and mosaic over existing text retain drawing priority.");

            stage = "actual display scales and export";
            Choose(AnnotationTool.Rectangle, "矩形"); vm.Zoom = 1;
            foreach (double scale in new[] { .5, 1.0 })
            {
                canvas.LayoutTransform = new ScaleTransform(scale, scale); window.UpdateLayout(); Pause();
                var point = new PixelPoint(resized.Start.X, resized.Start.Y + 60);
                Reveal(point, scale);
                Click(point); Move(point); Require(canvas.Cursor == System.Windows.Input.Cursors.SizeAll, "Outline cursor does not indicate movement.");
                Drag(point, point + new PixelPoint(20, 20)); Near(resized.Start.X + 20, vm.BaseAnnotations[0].Start.X); Undo();
                Require(vm.BaseAnnotations[0] == resized, "Move undo did not restore rectangle before resize.");
                Click(point); Reveal(resized.End, scale); Move(resized.End);
                var mouse = System.Windows.Input.Mouse.GetPosition(canvas);
                Console.WriteLine($"SCALE scale={scale}; transform={canvas.LayoutTransform.Value.M11}; cursor={canvas.Cursor}; captured={canvas.IsMouseCaptured}; selected={vm.Selected?.Tool}; bounds={vm.Selected?.Bounds}; requested={resized.End}; pointer={mouse}; direct={vm.PointerHandle(new(mouse.X,mouse.Y),scale)}; size={window.ActualWidth}x{window.ActualHeight}");
                Require(canvas.Cursor == System.Windows.Input.Cursors.SizeNWSE, "Resize cursor does not match the visible control point.");
                Drag(resized.End, resized.End + new PixelPoint(20, 20)); Near(resized.Bounds.Width + 20, vm.BaseAnnotations[0].Bounds.Width); Undo();
                Require(vm.BaseAnnotations[0] == resized, "Display scale changed annotation history geometry.");
            }
            vm.Zoom = .5; canvas.LayoutTransform = new ScaleTransform(.5,.5); window.UpdateLayout();
            Reveal(new(resized.Start.X, resized.Start.Y + 60), .5);
            Click(new(resized.Start.X, resized.Start.Y + 60));
            SavePreview(window);
            Require(vm.Save(), "Final PNG save failed.");
            using (var file = File.OpenRead(dialogs.Path))
            {
                var actual = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Require(actual.PixelWidth == 1000 && actual.PixelHeight == 700 && Pixels(actual).SequenceEqual(Pixels(vm.Compose())), "PNG includes editing handles or has resized pixels.");
            }
            Console.WriteLine("PASS native 50/100 percent canvas transforms use visible resize points and move cursors; saved PNG preserves original dimensions and contains no editing handles.");
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/direct-annotation.json", JsonSerializer.Serialize(new { ToolbarRemoved = true, NativeDirectMove = true, ClickJitterIgnored = true, ResizeUndoRedoDelete = true, HollowInteriorDraws = true, FilledBodyMoves = true, EllipseCornersDraw = true, TextDoubleClickAndMove = true, StickerMoves = true, PenMosaicDrawPriority = true, ActualDisplayScales = true, OriginalPixelsExport = true }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("Annotation stage: " + stage); Console.Error.WriteLine(error); return 1; }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); Directory.Delete(directory, true); }
    }
    private static void Mouse(params uint[] flags)
    {
        var inputs = flags.Select(flag => new Input { Type = 0, Data = new() { Mouse = new() { Flags = flag } } }).ToArray();
        var sending = Task.Run(() => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>())); Wait(() => sending.IsCompleted);
        if (sending.GetAwaiter().GetResult() != inputs.Length) throw new InvalidOperationException("Native annotation input rejected.");
    }
    private static void Wait(Func<bool> done) => TextWindowChecks.PumpUntil(done, TimeSpan.FromSeconds(6));
    private static void Pause() { var task = Task.Delay(65); Wait(() => task.IsCompleted); }
    private static void SavePreview(Window window)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(content), null, new Rect(content.RenderSize)); bitmap.Render(visual);
        string folder = "artifacts/validation/ui-previews"; Directory.CreateDirectory(folder);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(folder, "36-direct-annotation.png")); png.Save(file);
    }
    private static BitmapSource Image(int width, int height)
    { var data = Enumerable.Repeat((byte)255, width * height * 4).ToArray(); var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4); image.Freeze(); return image; }
    private static byte[] Pixels(BitmapSource image)
    { var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var bytes = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(bytes, source.PixelWidth * 4, 0); return bytes; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Dialogs(string path) : IEditorDialogs
    {
        public string Path => path;
        public string? ChoosePngPath() => path;
        public TextInput? EditText(string text, string color, double fontSize) => new(text, color, fontSize);
        public CloseChoice ConfirmClose() => CloseChoice.Discard;
        public void ShowError(string message) => throw new InvalidOperationException(message);
    }
}
