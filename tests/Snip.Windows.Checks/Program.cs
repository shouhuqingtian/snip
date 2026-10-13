using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--chrome")) return ChromeCaptureCheck.Run();
        if (args.Contains("--ocr")) return NativeOcrCheck.Run();
        int passed = 0, failed = 0;
        var renderer = new AnnotationRenderer();
        var image = MakeImage();
        void Check(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine($"PASS {name}"); }
            catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
        }
        Check("image pixel dimensions", () =>
        {
            var result = renderer.Render(image, []);
            Require(result.PixelWidth == 100 && result.PixelHeight == 80 && Pixel(result, 70, 50).SequenceEqual(Pixel(image, 70, 50)));
        });
        Check("rectangle outline preserves interior", () =>
        {
            var result = renderer.Render(image, [Shape(AnnotationTool.Rectangle)]);
            Require(IsRed(result, 10, 25)); Require(Pixel(result, 25, 25).SequenceEqual(Pixel(image, 25, 25)));
        });
        Check("ellipse", () => Require(IsRed(renderer.Render(image, [Shape(AnnotationTool.Ellipse)]), 10, 25)));
        Check("horizontal arrow", () => Require(IsRed(renderer.Render(image, [Shape(AnnotationTool.Arrow) with { End = new(60, 10) }]), 30, 10)));
        Check("pen dot", () => Require(IsRed(renderer.Render(image, [Shape(AnnotationTool.Pen) with { Points = [new(55, 55)] }]), 55, 55)));
        Check("text rendering", () =>
        {
            var result = renderer.Render(image, [Shape(AnnotationTool.Text) with { Text = "截图\n测试", FontSize = 16 }]);
            Require(!Bytes(result).SequenceEqual(Bytes(image)));
        });
        Check("mosaic changes selected region and leaves outside intact", () =>
        {
            var result = renderer.Render(image, [Shape(AnnotationTool.Mosaic)]);
            Require(Pixel(result, 11, 11).SequenceEqual(Pixel(result, 15, 15)));
            Require(!Pixel(result, 11, 11).SequenceEqual(Pixel(image, 11, 11)));
            Require(Pixel(result, 60, 60).SequenceEqual(Pixel(image, 60, 60)));
        });
        Check("mosaic also flattens prior annotations", () =>
        {
            var pen = Shape(AnnotationTool.Pen) with { Points = [new(13, 13)], StrokeWidth = 5 };
            var result = renderer.Render(image, [pen, Shape(AnnotationTool.Mosaic)]);
            Require(Pixel(result, 13, 13).SequenceEqual(Pixel(result, 16, 16)));
            Require(!IsRed(result, 13, 13));
        });
        Check("PNG save reload and overwrite", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "Snip.Checks-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
            try
            {
                var service = new ImageExportService(); var path = Path.Combine(directory, "capture.png");
                service.SavePng(image, path); service.SavePng(renderer.Render(image, [Shape(AnnotationTool.Rectangle)]), path);
                using var stream = File.OpenRead(path);
                var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Require(decoded.PixelWidth == 100 && decoded.PixelHeight == 80 && IsRed(decoded, 10, 25));
                Require(Directory.GetFiles(directory).Length == 1);
            }
            finally { Directory.Delete(directory, true); }
        });
        Check("editor move delete undo redo", () =>
        {
            var vm = MakeEditor(image, renderer);
            vm.BeginPointer(new(10, 10), false); vm.EndPointer(new(40, 40));
            vm.Tool = AnnotationTool.Select;
            vm.BeginPointer(new(10, 20), false); vm.EndPointer(new(20, 30));
            Require(vm.Selected!.Start == new PixelPoint(20, 20));
            vm.DeleteCommand.Execute(null); Require(vm.BaseAnnotations.Count == 0);
            vm.UndoCommand.Execute(null); Require(vm.BaseAnnotations[0].Start == new PixelPoint(20, 20));
            vm.UndoCommand.Execute(null); Require(vm.BaseAnnotations[0].Start == new PixelPoint(10, 10));
            vm.RedoCommand.Execute(null); Require(vm.BaseAnnotations[0].Start == new PixelPoint(20, 20));
        });
        Check("editor gesture cancellation", () =>
        {
            var vm = MakeEditor(image, renderer); vm.BeginPointer(new(10, 10), false); vm.MovePointer(new(40, 40)); vm.CancelGesture();
            Require(vm.BaseAnnotations.Count == 0 && !vm.IsDirty && vm.Preview is null);
        });
        Check("editor committed text can be edited and undone", () =>
        {
            var dialogs = new FakeDialogs { Text = new("第一行\n第二行", "#007AFF", 24) };
            var vm = new EditorViewModel(image, renderer, new FakeExport(), dialogs) { Tool = AnnotationTool.Text };
            vm.BeginPointer(new(5, 5), false);
            Require(vm.BaseAnnotations.Single().Text.Contains("第二行"));
            vm.Tool = AnnotationTool.Select; dialogs.Text = new("修改", "#FF0000", 16);
            vm.BeginPointer(new(8, 8), true);
            Require(vm.BaseAnnotations.Single().Text == "修改");
            vm.UndoCommand.Execute(null); Require(vm.BaseAnnotations.Single().Text.Contains("第二行"));
        });
        Check("editor save failure keeps dirty document", () =>
        {
            var dialogs = new FakeDialogs { Path = "test.png" };
            var vm = new EditorViewModel(image, renderer, new FakeExport { Fail = true }, dialogs);
            vm.BeginPointer(new(10, 10), false); vm.EndPointer(new(40, 40));
            Require(!vm.Save() && vm.IsDirty && dialogs.Error is not null);
        });
        Check("editor save marks exported revision", () =>
        {
            var service = new FakeExport();
            var vm = new EditorViewModel(image, renderer, service, new FakeDialogs { Path = "test.png" });
            vm.BeginPointer(new(10, 10), false); vm.EndPointer(new(40, 40));
            Require(vm.Save() && !vm.IsDirty && service.Saved is not null);
            vm.UndoCommand.Execute(null); Require(vm.IsDirty);
        });
        Check("filled rectangle exports opaque interior", () => Require(IsRed(renderer.Render(image, [Shape(AnnotationTool.Rectangle) with { Filled = true }]), 25, 25)));
        Check("sticker exports source pixels", () =>
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = new MemoryStream(); encoder.Save(stream);
            var sticker = Shape(AnnotationTool.Sticker) with { Start = new(0, 0), End = new(100, 80), StickerPng = stream.ToArray() };
            Require(Pixel(renderer.Render(image, [sticker]), 70, 50).SequenceEqual(Pixel(image, 70, 50)));
        });
        Check("inline text commits and undo removes it", () =>
        {
            var vm = MakeEditor(image, renderer); vm.UseInlineText = true; vm.Tool = AnnotationTool.Text;
            vm.BeginPointer(new(10, 10), false); Require(vm.InlineText is not null && vm.BaseAnnotations.Count == 0);
            vm.CommitInlineText("原位文字"); Require(vm.BaseAnnotations.Single().Text == "原位文字");
            vm.UndoCommand.Execute(null); Require(vm.BaseAnnotations.Count == 0);
        });
        Check("sticker can be moved resized and undone", () =>
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = new MemoryStream(); encoder.Save(stream);
            var vm = MakeEditor(image, renderer); vm.AddSticker(stream.ToArray(), 20, 20);
            var before = vm.BaseAnnotations.Single();
            vm.BeginPointer(before.End, false); vm.EndPointer(before.End + new PixelPoint(10, 10));
            Require(vm.BaseAnnotations.Single().Bounds.Width == before.Bounds.Width + 10);
            vm.UndoCommand.Execute(null); Require(vm.BaseAnnotations.Single().Bounds == before.Bounds);
        });
        Check("large image renders across tile boundary", () =>
        {
            var bytes = Enumerable.Repeat((byte)255, 100 * 5000 * 4).ToArray();
            var tall = BitmapSource.Create(100, 5000, 96, 96, PixelFormats.Bgra32, null, bytes, 400); tall.Freeze();
            var rectangle = Shape(AnnotationTool.Rectangle) with { Start = new(10, 2000), End = new(40, 2100), Filled = true };
            var output = renderer.Render(tall, [rectangle]); Require(output.PixelHeight == 5000 && IsRed(output, 25, 2047) && IsRed(output, 25, 2049));
        });
        Check("OCR initialization leaves dispatcher responsive and cancellation preserves results", () => ResponsivenessChecks.Ocr(image));
        Check("translation synchronous provider runs off the dispatcher", () => ResponsivenessChecks.Translation(image));
        Check("diagnostic bundle contains native display DPI and app version without user identity", () =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "Snip.Desktop.Diagnostics-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
            try
            {
                using var diagnostics = new DiagnosticJournal(Path.Combine(folder, "logs"));
                diagnostics.Record("app", "started");
                string path = Path.Combine(folder, "report.zip");
                diagnostics.ExportAsync(path, DesktopDiagnostics.EnvironmentInfo(new DesktopCaptureService())).GetAwaiter().GetResult();
                using var zip = ZipFile.OpenRead(path);
                using var reader = new StreamReader(zip.GetEntry("environment.json")!.Open());
                string text = reader.ReadToEnd(); using var json = JsonDocument.Parse(text);
                var environment = json.RootElement.GetProperty("Environment");
                Require(environment.GetProperty("Displays").GetArrayLength() > 0 && environment.GetProperty("Displays")[0].GetProperty("DpiX").GetUInt32() >= 96);
                Require(Version.TryParse(environment.GetProperty("AppVersion").GetString()!.Split('+')[0].Split('-')[0], out var version) && version.Major > 0);
                Require(!text.Contains("UserName") && !text.Contains("MachineName") && !text.Contains(folder));
            }
            finally { Directory.Delete(folder, true); }
        });
        Console.WriteLine($"Total {passed + failed}; passed {passed}; failed {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static EditorViewModel MakeEditor(BitmapSource image, AnnotationRenderer renderer) => new(image, renderer, new FakeExport(), new FakeDialogs());
    private static Annotation Shape(AnnotationTool tool) => new(Guid.NewGuid(), tool, new(10, 10), new(40, 40), "#FF0000", 4, 16, 8, "", []);
    private static BitmapSource MakeImage()
    {
        var data = new byte[100 * 80 * 4];
        for (int y = 0; y < 80; y++) for (int x = 0; x < 100; x++)
        { int i = (y * 100 + x) * 4; data[i] = (byte)(x * 2); data[i + 1] = (byte)(y * 3); data[i + 2] = (byte)x; data[i + 3] = 255; }
        var image = BitmapSource.Create(100, 80, 96, 96, PixelFormats.Bgra32, null, data, 400); image.Freeze(); return image;
    }
    private static byte[] Bytes(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var data = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(data, source.PixelWidth * 4, 0); return data;
    }
    private static byte[] Pixel(BitmapSource image, int x, int y) => Bytes(image).Skip((y * image.PixelWidth + x) * 4).Take(4).ToArray();
    private static bool IsRed(BitmapSource image, int x, int y) { var p = Pixel(image, x, y); return p[2] > 230 && p[0] < 25 && p[1] < 25; }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }

    private sealed class FakeDialogs : IEditorDialogs
    {
        public string? Path { get; set; }
        public TextInput? Text { get; set; }
        public string? Error { get; private set; }
        public string? ChoosePngPath() => Path;
        public TextInput? EditText(string text, string color, double fontSize) => Text;
        public CloseChoice ConfirmClose() => CloseChoice.Cancel;
        public void ShowError(string message) => Error = message;
    }
    private sealed class FakeExport : IImageExportService
    {
        public bool Fail { get; init; }
        public BitmapSource? Saved { get; private set; }
        public void SavePng(BitmapSource image, string path) { if (Fail) throw new IOException("simulated"); Saved = image; }
        public Task CopyAsync(BitmapSource image) => Task.CompletedTask;
    }
}
