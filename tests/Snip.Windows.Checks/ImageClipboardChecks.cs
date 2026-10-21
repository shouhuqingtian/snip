using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
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

internal static class ImageClipboardChecks
{
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    private const string Sentinel = "Public image clipboard sentinel";

    public static int Run()
    {
        var app=Application.Current??new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var previous=SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        using var deadline=new System.Threading.Timer(_=> { Console.Error.WriteLine("FAIL native image clipboard deadline."); Environment.Exit(1); },null,TimeSpan.FromSeconds(60),Timeout.InfiniteTimeSpan);
        string folder=Path.Combine(Path.GetTempPath(),"Snip.ImageClipboard-"+Guid.NewGuid().ToString("N"));
        using var diagnostics=new DiagnosticJournal(folder); using var notifications=new CopyNotificationService(app.Dispatcher);
        try
        {
            Ui.EnsureTheme(); var first=Pattern(747,588,3); var second=Pattern(731,589,11);
            var service=new ImageExportService();
            VerifyLegacyFlush(first);
            Await(service.CopyAsync(first)); AssertImage(first);
            double transient=VerifyTransient(first,second,service);
            double persistent=VerifyPersistent(first,service,diagnostics,notifications);
            VerifyCancelledSelection(diagnostics,notifications);
            VerifyPersistence();
            for(int i=0;i<16;i++) { var image=Pattern(97+i,63+i,i); Await(service.CopyAsync(image)); AssertImage(image); }
            var transparent=BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,new byte[]{30,60,90,128,7,8,9,0},8); transparent.Freeze();
            Await(service.CopyAsync(transparent)); Require(ReadDib().Pixels.SequenceEqual(new byte[]{30,60,90,128,7,8,9,0}),"Native alpha channel or RGB bytes were changed.");
            var large=Pattern(3072,1920,19); var clock=Stopwatch.StartNew(); Await(service.CopyAsync(large)); double largeMs=clock.Elapsed.TotalMilliseconds; AssertImage(large);
            Await(diagnostics.FlushAsync());
            var rows=File.ReadAllLines(Path.Combine(folder,"events.jsonl")).Select(s=>JsonDocument.Parse(s)).ToArray();
            try
            {
                Require(rows.Any(d=>d.RootElement.GetProperty("Stage").GetString()=="clipboard-busy") && rows.Any(d=>d.RootElement.GetProperty("Stage").GetString()=="clipboard-published") && rows.Any(d=>d.RootElement.GetProperty("Stage").GetString()=="cancelled"),"Image copy stages or cancellation were not recorded.");
                Require(!File.ReadAllText(Path.Combine(folder,"events.jsonl")).Contains(Sentinel,StringComparison.Ordinal),"Clipboard contents leaked into technical diagnostics.");
            }
            finally { foreach(var row in rows)row.Dispose(); }
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/image-clipboard.json",JsonSerializer.Serialize(new {
                LegacyFlushFailureReproduced=true, WpfBitmapCompatible=true, NativeDibCompatible=true, ExactOriginalPixels=true,
                TransientLockRecovers=true, BusyProcessIdRecorded=true, UiResponsiveWhileBusy=true, PersistentLockBounded=true,
                FailureRetainsImageAndAnnotations=true, RetryAfterRelease=true, CancellationHasNoLateCopy=true,
                CancelledCopyHasNoSuccessNotice=true, SurvivesProducerExit=true, ConsecutiveCopies=true, AlphaPreserved=true, LargeImage=true,
                TechnicalStagesRecorded=true, NoClipboardContentsInDiagnostics=true
            },new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText("artifacts/validation/image-clipboard-performance.json",JsonSerializer.Serialize(new {TransientLockMilliseconds=transient,PersistentLockMilliseconds=persistent,LargeImageMilliseconds=largeMs,BurstCopies=16}));
            Console.WriteLine($"PASS native image clipboard: exact WPF/native pixels, transient/persistent locks, editing preserved, cancellation, process-exit persistence, 16 consecutive copies, alpha and 3072x1920 image; transient={transient:F1}ms persistent={persistent:F1}ms large={largeMs:F1}ms.");
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { notifications.Hide(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void VerifyLegacyFlush(BitmapSource image)
    {
        var data=new DataObject(); data.SetData(DataFormats.Bitmap,image); Clipboard.SetDataObject(data,false);
        using var holder=new ClipboardWindowChecks.ClipboardLock();
        bool reproduced=false; var clock=Stopwatch.StartNew();
        try { Clipboard.Flush(); }
        catch(ExternalException error) when(error.HResult==TextClipboardService.ClipboardBusy) { reproduced=true; }
        Require(reproduced,"The diagnostic packet's original WPF Flush failure did not reproduce.");
        Console.WriteLine($"PASS original Clipboard.Flush reproduces 0x800401D0 with a real external holder; elapsed={clock.ElapsedMilliseconds}ms.");
    }

    private static double VerifyTransient(BitmapSource first,BitmapSource second,IImageExportService service)
    {
        Await(service.CopyAsync(first));
        using var holder=new ClipboardWindowChecks.ClipboardLock();
        var trace=new TraceProgress(); int pulses=0; double gap=0; var pulseClock=Stopwatch.StartNew();
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(20) };
        timer.Tick+=(_,_)=> { gap=Math.Max(gap,pulseClock.Elapsed.TotalMilliseconds); pulseClock.Restart(); if(++pulses==12)holder.Release(); };
        var clock=Stopwatch.StartNew(); timer.Start();
        try
        {
            var copy=service.CopyAsync(second,CancellationToken.None,trace); Await(copy);
            Require(pulses>=12 && gap<500 && trace.Rows.Any(v=>v.Stage=="clipboard-busy" && v.BlockingProcessId==holder.ProcessId),"Transient contention blocked UI or did not identify the technical holder PID.");
            Require(trace.Rows.Count(v=>v.Stage=="image-ready")==1,"Retry rebuilt image data."); AssertImage(second);
            Console.WriteLine($"PASS second screenshot recovers after external clipboard release without rebuilding pixels; UI pulses={pulses}, maximum gap={gap:F1}ms.");
            return clock.Elapsed.TotalMilliseconds;
        }
        finally { timer.Stop(); }
    }

    private static double VerifyPersistent(BitmapSource image,IImageExportService service,DiagnosticJournal diagnostics,CopyNotificationService notifications)
    {
        Await(new TextClipboardService().CopyAsync(Sentinel,CancellationToken.None));
        var export=new CopyFeedbackExport(new DiagnosticImageExport(service,diagnostics),notifications);
        var vm=new EditorViewModel(image,new AnnotationRenderer(),export,new Dialogs());
        vm.BeginPointer(new(20,20),false); vm.MovePointer(new(120,90)); vm.EndPointer(new(120,90));
        var composed=vm.Compose(); var window=new EditorWindow { DataContext=vm }; window.Show();
        double elapsed;
        try
        {
            using(var holder=new ClipboardWindowChecks.ClipboardLock())
            {
                var clock=Stopwatch.StartNew(); var copy=vm.CopyAndMarkAsync(); Wait(()=>copy.IsCompleted);
                bool busy=false; try { copy.GetAwaiter().GetResult(); } catch(ExternalException error) when(error.HResult==TextClipboardService.ClipboardBusy) { busy=true; }
                elapsed=clock.Elapsed.TotalMilliseconds;
                Require(busy && elapsed<3500 && window.IsVisible && vm.IsDirty && !vm.IsExporting && vm.BaseAnnotations.Count==1,"Persistent lock lost the editor/annotations or exceeded bounded retry.");
                Require(!Application.Current.Windows.OfType<CopyNotificationWindow>().Any(),"Failed copy displayed success.");
            }
            Require(Clipboard.GetText()==Sentinel,"Persistent lock cleared unrelated clipboard contents.");
            Require(Await(vm.CopyAndMarkAsync()),"Copy could not be retried after release."); AssertImage(composed);
            Wait(()=>Application.Current.Windows.OfType<CopyNotificationWindow>().Any()); notifications.Hide();
        }
        finally { window.CancelScreenshot(); }
        Console.WriteLine($"PASS persistent image lock fails in {elapsed:F1}ms, preserves clipboard, screenshot and annotations, and retries successfully with success-only feedback.");
        return elapsed;
    }

    private static void VerifyCancelledSelection(DiagnosticJournal diagnostics,CopyNotificationService notifications)
    {
        var desktop=new DesktopCaptureService();
        var owner=new Window { Style=null,WindowStyle=WindowStyle.None,Left=80,Top=100,Width=450,Height=300,Background=Brushes.CornflowerBlue };
        owner.Show(); owner.Activate(); owner.UpdateLayout();
        var target=DesktopWindows.Snapshot(true).Single(w=>w.Handle==new WindowInteropHelper(owner).Handle);
        var export=new TraceExport(new CopyFeedbackExport(new DiagnosticImageExport(new ImageExportService(),diagnostics),notifications));
        try
        {
            Await(new TextClipboardService().CopyAsync(Sentinel,CancellationToken.None));
            using var selection=new SelectionSession(desktop.Capture(),desktop,new AnnotationRenderer(),export,[target]);
            selection.Show(); selection.ViewModel.SetSelection(target.Bounds);
            using(var holder=new ClipboardWindowChecks.ClipboardLock())
            {
                selection.Confirm(); Wait(()=>export.Progress.Rows.Any(p=>p.Stage=="clipboard-busy"));
                HotkeyChecks.Press(0x1B); Wait(()=>selection.Completion.IsCompleted && selection.Editor?.IsExporting==false);
            }
            var delay=Task.Delay(180); Wait(()=>delay.IsCompleted);
            Require(Clipboard.GetText()==Sentinel && !Application.Current.Windows.OfType<CopyNotificationWindow>().Any(),"Escape caused late image publication or a success notice.");
            Console.WriteLine("PASS native Escape during image retry cancels pending publication, retains prior clipboard, and produces no late success notice.");
        }
        finally { owner.Close(); }
    }

    private static void VerifyPersistence()
    {
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,CreateNoWindow=true };
        start.ArgumentList.Add(typeof(ImageClipboardChecks).Assembly.Location); start.ArgumentList.Add("--image-clipboard-persist");
        using var child=Process.Start(start)!;
        if(!child.WaitForExit(8000)) { child.Kill(true); throw new TimeoutException("Image producer did not exit."); }
        Require(child.ExitCode==0,"Image producer failed."); AssertImage(Pattern(747,588,23));
        Console.WriteLine("PASS native DIB and WPF bitmap remain readable after their producing process exits; no OLE Flush.");
    }

    internal static int PublishFixture()
    {
        _=Application.Current??new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { Await(new ImageExportService().CopyAsync(Pattern(747,588,23))); return 0; }
        catch(Exception error) { Console.Error.WriteLine(error.GetType().FullName); return 1; }
    }

    private static BitmapSource Pattern(int width,int height,int seed)
    {
        var bytes=new byte[width*height*4];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        { int i=(y*width+x)*4; bytes[i]=(byte)(x*31+y*13+seed); bytes[i+1]=(byte)(x*7+y*11+seed); bytes[i+2]=(byte)(x*17+y*3+seed); bytes[i+3]=255; }
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,bytes,width*4); image.Freeze(); return image;
    }
    private static void AssertImage(BitmapSource expected)
    {
        Require(Clipboard.ContainsImage(),"Standard bitmap format is unavailable to WPF consumers.");
        var actual=Clipboard.GetImage(); var converted=new FormatConvertedBitmap(actual,PixelFormats.Bgra32,null,0);
        var expectedPixels=new FormatConvertedBitmap(expected,PixelFormats.Bgra32,null,0);
        byte[] a=new byte[converted.PixelWidth*converted.PixelHeight*4], b=new byte[expectedPixels.PixelWidth*expectedPixels.PixelHeight*4];
        converted.CopyPixels(a,converted.PixelWidth*4,0); expectedPixels.CopyPixels(b,expectedPixels.PixelWidth*4,0);
        var dib=ReadDib();
        Require(actual.PixelWidth==expected.PixelWidth && actual.PixelHeight==expected.PixelHeight && a.SequenceEqual(b) && dib.Width==expected.PixelWidth && dib.Height==expected.PixelHeight && dib.Pixels.SequenceEqual(b),"Native or WPF clipboard consumer saw different dimensions, orientation, channels or pixels.");
    }
    private static (int Width,int Height,byte[] Pixels) ReadDib()
    {
        Require(IsClipboardFormatAvailable(2) && IsClipboardFormatAvailable(8) && IsClipboardFormatAvailable(17),"Windows bitmap/DIB compatibility formats unavailable.");
        Require(OpenClipboard(IntPtr.Zero),"Could not read public fixture clipboard.");
        try
        {
            Require(GetClipboardData(8)!=IntPtr.Zero,"Independent DIB consumer could not obtain a standard image.");
            var memory=GetClipboardData(17); var pointer=GlobalLock(memory); Require(pointer!=IntPtr.Zero,"DIB memory unavailable.");
            try
            {
                int width=Marshal.ReadInt32(pointer,4), height=-Marshal.ReadInt32(pointer,8), header=Marshal.ReadInt32(pointer);
                Require(header==124 && width>0 && height>0,"DIB header invalid."); var bytes=new byte[width*height*4]; Marshal.Copy(IntPtr.Add(pointer,header),bytes,0,bytes.Length); return(width,height,bytes);
            }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }
    private static void Wait(Func<bool> done)=>TextWindowChecks.PumpUntil(done,TimeSpan.FromSeconds(6));
    private static void Await(Task task) { Wait(()=>task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task) { Wait(()=>task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Require(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }
    private sealed class TraceProgress : IProgress<ImageCopyProgress>
    { public readonly List<ImageCopyProgress> Rows=[]; public void Report(ImageCopyProgress value)=>Rows.Add(value); }
    private sealed class TraceExport(IImageExportService inner) : IImageExportService
    {
        public TraceProgress Progress { get; }=new();
        public void SavePng(BitmapSource image,string path)=>inner.SavePng(image,path);
        public Task CopyAsync(BitmapSource image)=>inner.CopyAsync(image);
        public Task CopyAsync(BitmapSource image,CancellationToken token,IProgress<ImageCopyProgress>? progress=null)=>inner.CopyAsync(image,token,Progress);
    }
    private sealed class Dialogs : IEditorDialogs
    {
        public string? ChoosePngPath()=>null; public TextInput? EditText(string text,string color,double size)=>null;
        public CloseChoice ConfirmClose()=>CloseChoice.Discard; public void ShowError(string message)=>throw new InvalidOperationException(message);
    }
}
