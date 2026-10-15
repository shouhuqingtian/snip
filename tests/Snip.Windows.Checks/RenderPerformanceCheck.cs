using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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

internal static class RenderPerformanceCheck
{
    public static int Run()
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            if (ManualPreviewChecks.Run() != 0) throw new InvalidOperationException("Manual capture preview performance failed.");
            if (EditorZoomChecks.Run() != 0) throw new InvalidOperationException("Editor fit-width workflow failed.");
            var renderer = new AnnotationRenderer();
            var image = Image(320, 240);
            var layers = Layers();
            layers.Add(Shape(AnnotationTool.Mosaic, -7, 211, 54, 260));
            var old = LegacyRender(image, layers);
            var current = renderer.Render(image, layers);
            Require(Hash(old) == Hash(current), "Optimized output differs for overlapping annotations or clipped mosaic.");
            var preview = Rasterize(renderer.BuildDrawing(image, layers), image.PixelWidth, image.PixelHeight);
            Require(Hash(preview) == Hash(current), "Preview pixels differ from exported PNG pixels.");
            Console.WriteLine("PASS vector preview and export match legacy pixels, including overlapping and clipped mosaic.");

            var vm = new EditorViewModel(image, renderer, new FakeExport(), new FakeDialogs());
            var weak = DetachCanvas(vm);
            Pump();
            // Collection is only used to verify reachability in a test, never as a production optimization.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Require(!weak.IsAlive, "Detached canvas is retained by editor event subscriptions.");
            GC.KeepAlive(vm);
            Console.WriteLine("PASS detached canvas is collectible while its editor stays alive.");

            var legacy = MeasureChild("legacy"); var optimized = MeasureChild("optimized");
            Require(legacy.Hash == optimized.Hash, "4K output checksum changed.");
            var report = new { Workload = "3840x2160; 4 annotation commits; 3 mosaic patches per preview", Legacy = legacy, Optimized = optimized };
            var folder = Path.GetFullPath("artifacts/validation"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "editor-performance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            Console.WriteLine("PASS 4K independent-process performance comparison completed; timings are evidence, not machine-dependent pass thresholds.");
            return InteractionPerformanceCheck.Run();
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    public static int Benchmark(string mode)
    {
        var image = Image(3840, 2160); var renderer = new AnnotationRenderer(); var layers = Layers();
        // Warm rendering infrastructure with a small image, not the measured 4K workload.
        _ = renderer.Render(Image(320, 240), layers);
        var process = Process.GetCurrentProcess(); process.Refresh();
        long initial = process.PrivateMemorySize64, allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        object? last = null;
        for (int i = 0; i < 4; i++)
        {
            layers.Add(Shape(AnnotationTool.Rectangle, 500 + i * 70, 600, 620 + i * 70, 760));
            last = mode == "legacy" ? LegacyRender(image, layers) : renderer.BuildDrawing(image, layers);
        }
        watch.Stop(); process.Refresh();
        long privateBytes = process.PrivateMemorySize64, peak = process.PeakWorkingSet64;
        long managed = GC.GetTotalAllocatedBytes(true) - allocated;
        GC.KeepAlive(last);
        var final = mode == "legacy" ? (BitmapSource)last! : renderer.Render(image, layers);
        Console.WriteLine(JsonSerializer.Serialize(new Measurement(mode, Math.Round(watch.Elapsed.TotalMilliseconds, 2), initial, privateBytes, peak, managed, Hash(final))));
        return 0;
    }
    private sealed record Measurement(string Mode, double PreviewMilliseconds, long InitialPrivateBytes, long FinalPrivateBytes, long PeakWorkingSetBytes, long ManagedAllocatedBytes, string Hash);
    private static Measurement MeasureChild(string mode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(RenderPerformanceCheck).Assembly.Location);
        start.ArgumentList.Add("--render-benchmark"); start.ArgumentList.Add(mode);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(60000)) { child.Kill(true); throw new TimeoutException("Render benchmark timed out."); }
        Require(child.ExitCode == 0, "Benchmark failed: " + error.GetAwaiter().GetResult());
        return JsonSerializer.Deserialize<Measurement>(output.GetAwaiter().GetResult())!;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DetachCanvas(EditorViewModel vm)
    {
        var canvas = new EditorCanvas { DataContext = vm };
        var grid = new Grid(); grid.Children.Add(canvas);
        var window = new Window { Content = grid, Width = 400, Height = 300, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout(); Pump();
        grid.Children.Remove(canvas); Pump(); window.Close();
        return new WeakReference(canvas);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static List<Annotation> Layers() =>
    [
        Shape(AnnotationTool.Rectangle, 10, 10, 160, 120) with { Filled = true },
        Shape(AnnotationTool.Pen, 13, 13, 50, 60) with { Points = [new(13,13), new(50,60)], StrokeWidth = 8 },
        Shape(AnnotationTool.Mosaic, 17, 23, 145, 99),
        Shape(AnnotationTool.Text, 30, 120, 100, 180) with { Text = "Public fixture", FontSize = 20 },
        Shape(AnnotationTool.Mosaic, 33, 41, 170, 151),
        Shape(AnnotationTool.Mosaic, 38, 127, 181, 191)
    ];
    private static Annotation Shape(AnnotationTool tool, int x, int y, int right, int bottom) => new(Guid.NewGuid(), tool, new(x,y), new(right,bottom), "#FF0000", 4, 16, 12, "", []);
    private static BitmapSource Image(int width, int height)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = (byte)(i % 251); data[i+1] = (byte)(i % 193); data[i+2] = (byte)(i % 127); data[i+3] = 255; }
        var image = BitmapSource.Create(width,height,96,96,PixelFormats.Pbgra32,null,data,width*4); image.Freeze(); return image;
    }
    private static string Hash(BitmapSource image)
    {
        var data = new byte[image.PixelWidth*image.PixelHeight*4]; image.CopyPixels(data,image.PixelWidth*4,0);
        return Convert.ToHexString(SHA256.HashData(data));
    }
    // Frozen pre-optimization renderer, retained only for output and performance comparisons.
    private static BitmapSource LegacyRender(BitmapSource image, IEnumerable<Annotation> annotations)
    {
        var group = new DrawingGroup(); group.Children.Add(new ImageDrawing(image,new Rect(0,0,image.PixelWidth,image.PixelHeight)));
        var renderer = new AnnotationRenderer();
        foreach(var a in annotations)
        {
            if(a.Tool == AnnotationTool.Mosaic)
            {
                var rect = a.Bounds.RoundOut().Intersect(new(0,0,image.PixelWidth,image.PixelHeight)); if(rect.IsEmpty) continue;
                var prior = Rasterize(group,image.PixelWidth,image.PixelHeight);
                int width=(int)rect.Width,height=(int)rect.Height,stride=width*4; var bytes=new byte[stride*height];
                prior.CopyPixels(new Int32Rect((int)rect.X,(int)rect.Y,width,height),bytes,stride,0);
                int size=Math.Clamp(a.MosaicSize,4,64);
                for(int y=0;y<height;y+=size) for(int x=0;x<width;x+=size)
                {
                    int endY=Math.Min(y+size,height),endX=Math.Min(x+size,width),count=(endY-y)*(endX-x); long blue=0,green=0,red=0;
                    for(int row=y;row<endY;row++) for(int col=x;col<endX;col++) { int i=row*stride+col*4;blue+=bytes[i];green+=bytes[i+1];red+=bytes[i+2]; }
                    for(int row=y;row<endY;row++) for(int col=x;col<endX;col++) { int i=row*stride+col*4;bytes[i]=(byte)(blue/count);bytes[i+1]=(byte)(green/count);bytes[i+2]=(byte)(red/count);bytes[i+3]=255; }
                }
                var patch=BitmapSource.Create(width,height,96,96,PixelFormats.Pbgra32,null,bytes,stride);patch.Freeze();
                group.Children.Add(new ImageDrawing(patch,AnnotationRenderer.Rect(rect)));
            }
            else { var layer=new DrawingGroup();using(var dc=layer.Open()) renderer.DrawAnnotation(dc,a);group.Children.Add(layer); }
        }
        return Rasterize(group,image.PixelWidth,image.PixelHeight);
    }
    private static BitmapSource Rasterize(Drawing drawing,int width,int height)
    {
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen()) dc.DrawDrawing(drawing);
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private sealed class FakeExport : IImageExportService { public void SavePng(BitmapSource image,string path) {} public Task CopyAsync(BitmapSource image)=>Task.CompletedTask; }
    private sealed class FakeDialogs : IEditorDialogs { public string? ChoosePngPath()=>null; public TextInput? EditText(string text,string color,double fontSize)=>null;public CloseChoice ConfirmClose()=>CloseChoice.Discard;public void ShowError(string message)=>throw new InvalidOperationException(message); }
}
