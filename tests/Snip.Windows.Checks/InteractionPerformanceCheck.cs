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

internal static class InteractionPerformanceCheck
{
    public static int Run()
    {
        Ui.EnsureTheme();
        CheckStrokeAndLayout();
        CheckStickerCache();
        var closed=CloseEditor();Pump();Collect();
        Require(closed.All(reference=>!reference.IsAlive),"Closed editor retained its window, view model or raw image.");
        Console.WriteLine("PASS closed independent editor, image and view model are collectible; no production forced GC.");
        CheckCaptureDispatcher();
        var results=new List<Measurement>();
        foreach(string workload in new[]{"pen","sticker","tile"})
        {
            var old=MeasureChild(workload,"previous-algorithm");var current=MeasureChild(workload,"optimized");
            Require(old.OutputHash==current.OutputHash,"Performance workload changed pixels: "+workload);
            if(workload=="pen")Require(current.ManagedAllocatedBytes<old.ManagedAllocatedBytes*.1,"Stroke updates still allocate whole point prefixes.");
            if(workload=="tile")Require(current.ManagedAllocatedBytes<old.ManagedAllocatedBytes*.5,"Long-image rasterization still allocates a new large buffer per tile.");
            results.Add(old);results.Add(current);
            Console.WriteLine($"MEASURE {workload}: {old.ElapsedMilliseconds:F2} → {current.ElapsedMilliseconds:F2} ms; managed allocations {old.ManagedAllocatedBytes} → {current.ManagedAllocatedBytes} bytes; equal output hashes.");
        }
        var folder=Path.GetFullPath("artifacts/validation");Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"interaction-performance.json"),JsonSerializer.Serialize(new
        {
            Baseline="Frozen 2.0.23 point-prefix copy, sticker decode and per-tile buffer algorithms; isolated synthetic workloads, not whole-application speed.",
            Workloads=new[]{"10000 pen pointer updates; identical points and final pixels", "24 sticker drawing rebuilds; 1024x768 PNG; 320x240 canvas", "1537x8305 annotated image; 2048-pixel tiles including partial edges"},
            Results=results
        },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS independent-process editing benchmarks; allocation bounds and output equality checked; timings are observations, not hardware-dependent pass thresholds.");
        return 0;
    }

    private static void CheckStrokeAndLayout()
    {
        var vm=Editor(Image(320,240));vm.Tool=AnnotationTool.Pen;
        var canvas=new EditorCanvas{DataContext=vm};var window=new Window{Content=canvas,Width=400,Height=320,ShowInTaskbar=false};
        try
        {
            window.Show();window.UpdateLayout();Pump();
            int commandChanges=0;vm.UndoCommand.CanExecuteChanged+=(_,_)=>commandChanges++;
            vm.BeginPointer(new(1,1),false);window.UpdateLayout();int initialChanges=commandChanges;
            for(int i=1;i<=1000;i++)vm.MovePointer(StrokePoint(i));
            // WPF InvalidateVisual normally invalidates Arrange; only the unnecessary Measure must stay valid.
            Require(canvas.IsMeasureValid && commandChanges==initialChanges,"Pointer updates invalidated text-free measurement or refreshed unchanged commands.");
            vm.EndPointer(StrokePoint(1000));var first=vm.BaseAnnotations.Single();var points=first.Points.ToArray();
            Require(first.Points.Count==1001 && first.Points.SequenceEqual(Enumerable.Range(0,1001).Select(StrokePoint)),"Pen commit changed or dropped sampled points.");
            vm.BeginPointer(new(20,20),false);vm.MovePointer(new(30,30));vm.EndPointer(new(30,30));
            Require(first.Points.SequenceEqual(points),"A later stroke mutated an earlier history snapshot.");
            vm.UndoCommand.Execute(null);Require(vm.BaseAnnotations.Count==1 && vm.BaseAnnotations[0].Points.SequenceEqual(points),"Stroke undo lost its original points.");
            vm.RedoCommand.Execute(null);Require(vm.BaseAnnotations.Count==2,"Stroke redo lost the later stroke.");
            vm.BeginPointer(new(50,50),false);vm.MovePointer(new(60,60));vm.CancelGesture();Require(vm.BaseAnnotations.Count==2,"Cancelled stroke entered history.");
            vm.Tool=AnnotationTool.Text;vm.BeginPointer(new(10,10),false);window.UpdateLayout();
            var input=TextWindowChecks.Descendants<TextBox>(canvas).Single();input.Text="Public inline fixture";
            vm.CommitPendingEdits();window.UpdateLayout();
            Require(!TextWindowChecks.Descendants<TextBox>(canvas).Any() && vm.BaseAnnotations.Any(a=>a.Text=="Public inline fixture"),"Layout optimization broke inline text creation or commit.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS 1000 pointer updates keep layout/commands stable; strokes preserve points, undo/redo and cancellation; inline text still measures and commits.");
    }

    private static void CheckStickerCache()
    {
        var image=Image(64,48);var renderer=new AnnotationRenderer();byte[] png=Png(32,24,123);
        var annotation=Shape(AnnotationTool.Sticker) with {StickerPng=png};
        BitmapSource Decode(Annotation a)=>(BitmapSource)((ImageDrawing)((DrawingGroup)renderer.BuildDrawing(image,[a]).Children[1]).Children[0]).ImageSource;
        var first=Decode(annotation);var next=Decode(annotation with {End=new(60,40)});
        var other=((ImageDrawing)((DrawingGroup)new AnnotationRenderer().BuildDrawing(image,[annotation]).Children[1]).Children[0]).ImageSource;
        Require(first.IsFrozen && ReferenceEquals(first,next) && ReferenceEquals(first,other),"Moving a sticker or using another renderer decoded the PNG again.");
        byte[] second=Png(32,24,321),combined=png.Concat(second).ToArray();
        var a=Decode(annotation with {StickerPng=combined.AsMemory(0,png.Length)});
        var b=Decode(annotation with {StickerPng=combined.AsMemory(png.Length,second.Length)});
        Require(Hash(a)!=Hash(b) && Hash(b)==Hash(Decode(annotation with {StickerPng=second})),"Different PNG slices of one buffer reused incorrect decoded pixels.");
        var weak=UnusedSticker(renderer,image);Pump();Collect();
        Require(weak.All(reference=>!reference.IsAlive),"Sticker cache rooted an unused encoded buffer or decoded image.");
        GC.KeepAlive(renderer);GC.KeepAlive(image);
        Console.WriteLine("PASS frozen sticker pixels are reused across motion/renderers; distinct memory slices remain correct; weak cache does not retain unused payloads/images.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] UnusedSticker(AnnotationRenderer renderer,BitmapSource image)
    {
        byte[] png=Png(36,28,42);
        var group=renderer.BuildDrawing(image,[Shape(AnnotationTool.Sticker) with {StickerPng=png}]);
        var decoded=((ImageDrawing)((DrawingGroup)group.Children[1]).Children[0]).ImageSource;
        return [new(png),new(decoded)];
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CloseEditor()
    {
        // Production has its own tray/main shell. Do not let this fixture become Application.MainWindow.
        var previous=Application.Current.MainWindow;var shell=new Window();Application.Current.MainWindow=shell;
        try
        {
            var image=Image(1600,900);var vm=Editor(image);var window=new EditorWindow{DataContext=vm};
            window.Show();window.UpdateLayout();Pump();
            var result=new[]{new WeakReference(window),new WeakReference(vm),new WeakReference(image)};
            window.Close();return result;
        }
        finally {Application.Current.MainWindow=previous;shell.Close();}
    }

    private static void CheckCaptureDispatcher()
    {
        using var release=new ManualResetEventSlim();var dispatcher=Dispatcher.CurrentDispatcher;
        var capture=new WaitingCapture(Image(64,48),dispatcher,release);var export=new Export();using var pins=new PinManager(export);
        var renderer=new AnnotationRenderer();var features=new FeatureHub(renderer,export,capture,pins);
        using var coordinator=new CaptureCoordinator(capture,renderer,export,features);
        bool uiResponded=false;
        var previous=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        capture.Enter=()=>dispatcher.BeginInvoke(new Action(()=>{uiResponded=true;coordinator.Dispose();release.Set();}));
        try
        {
            var task=coordinator.CaptureAsync();TextWindowChecks.PumpUntil(()=>task.IsCompleted,TimeSpan.FromSeconds(8));task.GetAwaiter().GetResult();
            Require(uiResponded && capture.Background && !Application.Current.Windows.OfType<SelectionWindow>().Any(),"Desktop capture blocked dispatcher or created late overlays after disposal.");
            using var resources=JsonDocument.Parse(JsonSerializer.Serialize(DesktopDiagnostics.ResourceUsage()));
            Require(resources.RootElement.GetProperty("WorkingSetBytes").GetInt64()>0 && resources.RootElement.GetProperty("ManagedHeapBytes").GetInt64()>0,"Resource diagnostics did not include current process counters.");
        }
        finally {release.Set();SynchronizationContext.SetSynchronizationContext(previous);}
        Console.WriteLine("PASS blocked desktop capture stays off dispatcher; disposal prevents late overlays; diagnostics report memory and cumulative CPU without forcing GC.");
    }

    public static int Benchmark(string workload,string mode)
    {
        var renderer=new AnnotationRenderer();bool legacy=mode=="previous-algorithm";
        var image=Image(workload=="tile" ? 1537 : 320,workload=="tile" ? 8305 : 240);
        _=renderer.Render(Image(40,30),[Shape(AnnotationTool.Rectangle)]);
        var editor=workload=="pen" && !legacy ? Editor(image) : null;if(editor is not null)editor.Tool=AnnotationTool.Pen;
        byte[]? png=workload=="sticker" ? Png(1024,768,123) : null;
        DrawingGroup? drawing=null;BitmapSource? output=null;Annotation? stroke=null;
        if(workload=="tile")drawing=renderer.BuildDrawing(image,[Shape(AnnotationTool.Rectangle)]);
        var process=Process.GetCurrentProcess();process.Refresh();long initial=process.PrivateMemorySize64,allocated=GC.GetTotalAllocatedBytes(true);
        var watch=Stopwatch.StartNew();
        if(workload=="pen")
        {
            if(legacy)
            {
                stroke=Shape(AnnotationTool.Pen) with {Start=StrokePoint(0),End=StrokePoint(0),Points=new[]{StrokePoint(0)}};
                for(int i=1;i<=10000;i++)stroke=stroke with {End=StrokePoint(i),Points=stroke.Points.Append(StrokePoint(i)).ToArray()};
                var history=new EditHistory();history.Commit([stroke]);stroke=history.Current.Single();
            }
            else
            {
                editor!.BeginPointer(StrokePoint(0),false);for(int i=1;i<=10000;i++)editor.MovePointer(StrokePoint(i));
                editor.EndPointer(StrokePoint(10000));stroke=editor.BaseAnnotations.Single();
            }
            Require(stroke.Points.Count==10001,"Pen workload lost points.");
        }
        else if(workload=="sticker")
        {
            for(int i=0;i<24;i++)
            {
                var sticker=Shape(AnnotationTool.Sticker) with {Start=new(5+i,6+i),End=new(205+i,156+i),StickerPng=png!};
                drawing=legacy ? PreviousStickerDrawing(image,sticker) : renderer.BuildDrawing(image,[sticker]);
            }
        }
        else if(workload=="tile")output=legacy ? PreviousTileRasterize(drawing!,image.PixelWidth,image.PixelHeight) : renderer.Render(image,[Shape(AnnotationTool.Rectangle)]);
        else throw new ArgumentException("Unknown benchmark workload.");
        watch.Stop();long managed=GC.GetTotalAllocatedBytes(true)-allocated;process.Refresh();long final=process.PrivateMemorySize64,peak=process.PeakWorkingSet64;
        output??=workload=="pen" ? renderer.Render(image,[stroke!]) : Rasterize(drawing!,image.PixelWidth,image.PixelHeight);
        Console.WriteLine(JsonSerializer.Serialize(new Measurement(workload,mode,Math.Round(watch.Elapsed.TotalMilliseconds,2),managed,initial,final,peak,Hash(output))));
        GC.KeepAlive(editor);return 0;
    }
    private sealed record Measurement(string Workload,string Mode,double ElapsedMilliseconds,long ManagedAllocatedBytes,long InitialPrivateBytes,long FinalPrivateBytes,long PeakWorkingSetBytes,string OutputHash);
    private static Measurement MeasureChild(string workload,string mode)
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        if(Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(InteractionPerformanceCheck).Assembly.Location);
        start.ArgumentList.Add("--interaction-benchmark");start.ArgumentList.Add(workload);start.ArgumentList.Add(mode);
        using var child=Process.Start(start)!;var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
        if(!child.WaitForExit(60000)){child.Kill(true);throw new TimeoutException("Interaction benchmark timed out.");}
        Require(child.ExitCode==0,"Interaction benchmark failed: "+error.GetAwaiter().GetResult());
        return JsonSerializer.Deserialize<Measurement>(output.GetAwaiter().GetResult())!;
    }
    private static DrawingGroup PreviousStickerDrawing(BitmapSource image,Annotation a)
    {
        var group=new DrawingGroup();group.Children.Add(new ImageDrawing(image,new Rect(0,0,image.PixelWidth,image.PixelHeight)));
        using var stream=new MemoryStream(a.StickerPng.ToArray());var decoded=new BitmapImage();decoded.BeginInit();decoded.CacheOption=BitmapCacheOption.OnLoad;decoded.StreamSource=stream;decoded.EndInit();decoded.Freeze();
        var layer=new DrawingGroup();using(var dc=layer.Open())dc.DrawImage(decoded,AnnotationRenderer.Rect(a.Bounds));group.Children.Add(layer);group.Freeze();return group;
    }
    private static BitmapSource PreviousTileRasterize(Drawing drawing,int width,int height)
    {
        var output=new WriteableBitmap(width,height,96,96,PixelFormats.Pbgra32,null);
        for(int y=0;y<height;y+=2048)for(int x=0;x<width;x+=2048)
        {
            int w=Math.Min(2048,width-x),h=Math.Min(2048,height-y);var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen()){dc.PushTransform(new TranslateTransform(-x,-y));dc.DrawDrawing(drawing);dc.Pop();}
            var tile=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);tile.Render(visual);var buffer=new byte[w*h*4];tile.CopyPixels(buffer,w*4,0);
            output.WritePixels(new Int32Rect(x,y,w,h),buffer,w*4,0);
        }
        output.Freeze();return output;
    }
    private static BitmapSource Rasterize(Drawing drawing,int width,int height)
    {var visual=new DrawingVisual();using(var dc=visual.RenderOpen())dc.DrawDrawing(drawing);var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;}
    private static Annotation Shape(AnnotationTool tool)=>new(Guid.NewGuid(),tool,new(5,6),new(55,36),"#FF3B30",4,24,12,"",[]);
    private static PixelPoint StrokePoint(int i)=>new(i%300+1,i/300%200+1);
    private static EditorViewModel Editor(BitmapSource image)=>new(image,new AnnotationRenderer(),new Export(),new Dialogs());
    private static BitmapSource Image(int width,int height)
    {var data=new byte[width*height*4];for(int i=0;i<data.Length;i+=4){data[i]=(byte)(i%251);data[i+1]=(byte)(i%193);data[i+2]=(byte)(i%127);data[i+3]=255;}var result=BitmapSource.Create(width,height,96,96,PixelFormats.Pbgra32,null,data,width*4);result.Freeze();return result;}
    private static byte[] Png(int width,int height,int seed)
    {var data=new byte[width*height*4];new Random(seed).NextBytes(data);for(int i=3;i<data.Length;i+=4)data[i]=255;var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray();}
    private static string Hash(BitmapSource image)
    {var data=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(data,image.PixelWidth*4,0);return Convert.ToHexString(SHA256.HashData(data));}
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    private static void Collect(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private sealed class Export:IImageExportService{public void SavePng(BitmapSource image,string path){}public Task CopyAsync(BitmapSource image)=>Task.CompletedTask;}
    private sealed class Dialogs:IEditorDialogs{public string? ChoosePngPath()=>null;public TextInput? EditText(string text,string color,double fontSize)=>null;public CloseChoice ConfirmClose()=>CloseChoice.Discard;public void ShowError(string message)=>throw new InvalidOperationException(message);}
    private sealed class WaitingCapture(BitmapSource image,Dispatcher dispatcher,ManualResetEventSlim release):IDesktopCaptureService
    {
        public Action? Enter;public bool Background;
        public DesktopFrame Capture(){Background=!dispatcher.CheckAccess();Enter!();if(!release.Wait(TimeSpan.FromSeconds(3)))throw new TimeoutException("Dispatcher could not respond during capture.");return new(image,new(0,0,64,48),GetDisplays());}
        public IReadOnlyList<DisplayInfo> GetDisplays()=>[new("Public fixture",new(0,0,64,48),true)];public PixelPoint CursorPosition=>new(1,1);public void PlaceOverlay(Window window,PixelRect bounds){}
    }
}
