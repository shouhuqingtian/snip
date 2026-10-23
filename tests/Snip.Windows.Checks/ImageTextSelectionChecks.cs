using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class ImageTextSelectionChecks
{
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.EnsureTheme();
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string folder = Path.Combine(Path.GetTempPath(), "Snip.ImageText-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string stage = "initialize";
        using var watchdog = new System.Threading.Timer(_ => { Console.WriteLine("::error title=Image text deadline::" + stage); Environment.Exit(1); }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
        var export = new Export();
        try
        {
            var image = Article(); var engine = new Ocr(); engine.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var journal = new DiagnosticJournal(Path.Combine(folder,"logs"));
            var editor = new EditorViewModel(image,new AnnotationRenderer(),export,new Dialogs());
            var window = new EditorWindow(engine,diagnostics:journal) { DataContext=editor }; window.Show(); window.Activate(); window.UpdateLayout();
            var canvas=(EditorCanvas)window.FindName("EditorSurface"); var viewport=(ScrollViewer)window.FindName("Viewport");
            var selection=editor.TextSelection!;
            stage="opt-in responsive recognition";
            Require(engine.Calls==0 && !selection.IsActive,"Opening an editor eagerly recognized its image.");
            Click(window,"FitWidthButton"); Click(window,"SelectImageTextButton"); Wait(()=>engine.Calls==1);
            bool pulse=false; app.Dispatcher.BeginInvoke(()=>pulse=true,DispatcherPriority.Background); Wait(()=>pulse);
            Require(selection.Busy && editor.BaseAnnotations.Count==0,"Pending OCR blocked the dispatcher or changed image annotations.");
            engine.Pending!.SetResult(Ocr.Result); engine.Pending=null; Wait(()=>!selection.Busy && selection.Document is not null);
            Require(engine.Payloads[0].Width==800 && engine.Payloads[0].Height==1800,"OCR received scaled display pixels.");
            Console.WriteLine("PASS opt-in local recognition, responsive dispatcher, source-resolution OCR and no annotation mutation.");

            stage="native drag and keyboard text clipboard";
            var alpha=selection.Document!.Glyphs.Where(g=>selection.Document.Text.Substring(g.WordStart,g.WordLength)=="Alpha").ToArray();
            Drag(canvas,new(alpha[0].Bounds.X+1,alpha[0].Bounds.Y+15),new(alpha[^1].Bounds.Right-1,alpha[^1].Bounds.Y+15));
            Wait(()=>selection.SelectedText=="Alpha");
            HotkeyChecks.Press(0x11,0x43); Wait(()=>selection.Status.Contains("已复制"));
            Require(Clipboard.GetText()=="Alpha" && export.Copies==0 && !editor.IsDirty,"Ctrl+C copied image bytes, dirtied the document or lost partial-word selection.");
            var chinese=selection.Document.Glyphs.Last(g=>selection.Document.Text.Substring(g.WordStart,g.WordLength)=="中文测试");
            Drag(canvas,new(chinese.Bounds.Right-1,chinese.Bounds.Y+10),new(alpha[0].Bounds.X+1,alpha[0].Bounds.Y+10));
            Require(selection.SelectedText.Contains("Alpha") && selection.SelectedText.Contains("中文测试") && selection.SelectedText.IndexOf("Alpha",StringComparison.Ordinal)<selection.SelectedText.IndexOf("中文测试",StringComparison.Ordinal),"Reverse drag changed reading order or lost Chinese text.");
            HotkeyChecks.Press(0x11,0x43); Wait(()=>Clipboard.GetText()==selection.SelectedText);
            string outputBefore=Hash(editor.Compose()); Click(window,"CopyImageTextButton"); Wait(()=>selection.Status.Contains("已复制"));
            Require(Hash(editor.Compose())==outputBefore,"Selection highlight entered rendered/exported PNG pixels.");
            Preview(window,"49-image-text-selection");
            Console.WriteLine("PASS actual mouse drag, Ctrl+C native Unicode clipboard, partial words, reverse multi-row Chinese/English selection, source layout and highlight-free PNG rendering.");

            stage="zoom scrolling cache and auto-scroll";
            int calls=engine.Calls; Click(window,"SelectImageTextButton"); Click(window,"SelectImageTextButton");
            Require(engine.Calls==calls && selection.Document is not null,"Returning to unchanged selection reran OCR.");
            editor.Zoom*=1.1; window.UpdateLayout(); viewport.ScrollToVerticalOffset(1400*editor.Zoom); window.UpdateLayout();
            var gamma=selection.Document!.Glyphs.Where(g=>selection.Document.Text.Substring(g.WordStart,g.WordLength)=="Gamma").ToArray();
            Drag(canvas,new(gamma[0].Bounds.X+1,gamma[0].Bounds.Y+10),new(gamma[^1].Bounds.Right-1,gamma[^1].Bounds.Y+10));
            Require(selection.SelectedText=="Gamma","Zoom/scroll mapped selection to another image location.");
            viewport.ScrollToTop(); window.UpdateLayout(); Move(canvas,new(alpha[0].Bounds.X+1,alpha[0].Bounds.Y+10)); Down();
            var edge=viewport.PointToScreen(new Point(90,viewport.ActualHeight+35)); MoveScreen(edge); Wait(()=>viewport.VerticalOffset>200);
            Up(); Wait(()=>!canvas.IsSelectingText); Require(engine.Calls==calls,"Dragging/zooming/scrolling reran recognition.");
            HotkeyChecks.Press(0x1B); Wait(()=>!selection.IsActive);
            Require(!editor.IsDirty && editor.BaseAnnotations.Count==0,"Esc from selection altered annotations.");
            Click(window,"SelectImageTextButton");
            var rectangle=TextWindowChecks.Descendants<RadioButton>(window.Toolbar).First(b=>Equals(b.ToolTip,"矩形")); NativeClick(rectangle);
            Wait(()=>!selection.IsActive); Require(editor.Tool==AnnotationTool.Rectangle,"Clicking the already-selected drawing tool did not return to annotation mode.");
            Console.WriteLine("PASS zoom/scroll coordinate mapping, long-image edge auto-scroll, cache reuse without polling, Esc exit and already-selected tool switching.");

            stage="mosaic and late results";
            editor.Tool=AnnotationTool.Mosaic; editor.BeginPointer(new(340,50),false); editor.EndPointer(new(550,115));
            Click(window,"SelectImageTextButton"); Wait(()=>!selection.Busy && selection.Document is not null);
            selection.SelectAllCommand.Execute(null); Click(window,"CopyImageTextButton"); Wait(()=>selection.Status.Contains("已复制"));
            Require(!Clipboard.GetText().Contains("SECRET") && !selection.Document!.Text.Contains("SECRET") && selection.SelectedText.Contains("Alpha"),"Mosaic retained hidden/guessed text in the selectable layer.");
            Require(!engine.Payloads[0].PngBytes.Span.SequenceEqual(engine.Payloads[^1].PngBytes.Span),"Recognition used pre-mosaic source pixels.");
            engine.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
            int expected=engine.Calls+1; selection.RetryCommand.Execute(null); Wait(()=>engine.Calls>=expected && selection.Busy);
            editor.Tool=AnnotationTool.Rectangle; editor.BeginPointer(new(20,200),false); editor.EndPointer(new(80,250));
            engine.Pending.SetResult(Ocr.Result); engine.Pending=null; Pump(150);
            Require(!selection.IsActive && selection.Document is null && selection.SelectedText.Length==0,"A late OCR response restored pre-edit/hidden text.");
            window.CancelScreenshot();
            var blocking=new Ocr { Pending=new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var closingEditor=new EditorViewModel(image,new AnnotationRenderer(),export,new Dialogs());
            var closingWindow=new EditorWindow(blocking) { DataContext=closingEditor }; closingWindow.Show(); var closing=closingEditor.TextSelection!;
            Click(closingWindow,"SelectImageTextButton"); Wait(()=>blocking.Calls==1); closingWindow.CancelScreenshot();
            blocking.Pending!.SetResult(Ocr.Result); Pump(150);
            Require(!closing.IsActive && closing.Document is null && closingEditor.TextSelection is null,"Closing the editor retained a late selectable text layer.");
            Console.WriteLine("PASS composed-image recognition, conservative mosaic exclusion despite guessed original words, annotation invalidation, cancellation and closing without late text layers.");

            stage="new selection cancels queued clipboard publication";
            var delayedClipboard=new DelayedClipboard();
            var retryEditor=new EditorViewModel(image,new AnnotationRenderer(),export,new Dialogs());
            var retryWindow=new EditorWindow(new Ocr(),delayedClipboard) {DataContext=retryEditor};retryWindow.Show();
            Click(retryWindow,"SelectImageTextButton"); Wait(()=>retryEditor.TextSelection is {Busy:false,Document:not null});
            var retrySelection=retryEditor.TextSelection!;
            retrySelection.Begin(new(50,75),word:true); Click(retryWindow,"CopyImageTextButton"); Wait(()=>delayedClipboard.Text=="Alpha");
            retrySelection.Begin(new(210,75),word:true);
            Require(delayedClipboard.Token.IsCancellationRequested && retrySelection.SelectedText=="beta","New selection did not cancel pending clipboard publication.");
            delayedClipboard.Release.TrySetResult(true); Pump(150);
            Require(delayedClipboard.Copies==0 && !retrySelection.Status.Contains("失败"),"Old selection was published or cancellation reported as a copy failure.");
            retryWindow.CancelScreenshot();
            Console.WriteLine("PASS changing the selected word cancels an in-flight clipboard retry without publishing stale text or reporting a false failure.");

            stage="pin right-click edit copy";
            using(var pins=new PinManager(export))
            {
                var hub=new FeatureHub(new AnnotationRenderer(),export,new FakeDesktop(image),pins,ocr:new Ocr(),models:new ModelStore(Path.Combine(folder,"unused-models")));
                pins.Add(image); var pin=app.Windows.OfType<PinWindow>().Single(); pin.Left=0;pin.Top=0;pin.Width=120;pin.Height=80;
                string original=Hash(pins.ViewModel.Entries.Single().Image);
                var edit=pin.ContextMenu!.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"编辑（副本）…"));
                edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var editing=app.Windows.OfType<EditorWindow>().Single(); var copy=(EditorViewModel)editing.DataContext;
                Click(editing,"SelectImageTextButton"); Wait(()=>copy.TextSelection is { Busy:false, Document:not null });
                copy.TextSelection!.SelectAllCommand.Execute(null); Click(editing,"CopyImageTextButton"); Wait(()=>copy.TextSelection.Status.Contains("已复制"));
                Require(Clipboard.GetText().Contains("Alpha") && pin.IsVisible && pins.ViewModel.Entries.Count==1,"Pin edit cannot select text or lost/changed the original pin.");
                Preview(editing,"50-pin-edit-text-selection");
                copy.TextSelection.Deactivate(); copy.BeginPointer(new(10,10),false);copy.EndPointer(new(100,40));
                TextWindowChecks.Descendants<Button>(editing.Toolbar).Single(b=>Equals(b.ToolTip,"钉在桌面")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(pins.ViewModel.Entries.Count==2 && Hash(pins.ViewModel.Entries[1].Image)==original && Hash(pins.ViewModel.Entries[0].Image)!=original,"Re-pinning replaced the original or lost edited pixels.");
                pin.Close(); Require(editing.IsVisible,"Closing the original pin closed its independent edit copy.");
                editing.Toolbar.RequestComplete(); Wait(()=>!editing.IsVisible);
                Require(export.Copies==1 && export.Last is { PixelWidth:800, PixelHeight:1800 },"Edited copy could not complete as an image after text selection.");
            }
            Console.WriteLine("PASS actual pin context-menu edit copy, shared editor text selection, unchanged original, re-pin new image, original close independence and ordinary image completion.");

            stage="native Windows OCR word geometry";
            var native=new OfflineOcrService();
            if(!Task.Run(()=>native.Languages).GetAwaiter().GetResult().Any(l=>l.StartsWith("en",StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("English native OCR is required for this regression.");
            var white=BitmapSource.Create(600,150,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)255,600*150*4).ToArray(),2400);white.Freeze();
            var hello=new AnnotationRenderer().Render(white,[new(Guid.NewGuid(),AnnotationTool.Text,new(20,25),new(550,120),"#000000",2,48,8,"Hello world",[])]);
            var nativeEditor=new EditorViewModel(hello,new AnnotationRenderer(),export,new Dialogs());
            var nativeWindow=new EditorWindow(native) { DataContext=nativeEditor };nativeWindow.Show();
            Click(nativeWindow,"SelectImageTextButton"); Wait(()=>nativeEditor.TextSelection is { Busy:false,Document:not null });
            var doc=nativeEditor.TextSelection!.Document!; Require(doc.Text.Contains("Hello") && doc.Text.Contains("world"),"Native OCR failed its semantic assertion.");
            var world=doc.Glyphs.First(g=>doc.Text.Substring(g.WordStart,g.WordLength)=="world");
            nativeEditor.TextSelection.Begin(new(world.Bounds.X+world.Bounds.Width/2,world.Bounds.Y+world.Bounds.Height/2),word:true);
            Click(nativeWindow,"CopyImageTextButton");Wait(()=>nativeEditor.TextSelection.Status.Contains("已复制"));
            Require(Clipboard.GetText()=="world","Native recognized word geometry did not copy the selected word.");nativeWindow.CancelScreenshot();
            stage="failed local OCR preserves editor and private diagnostics";
            var failedEditor=new EditorViewModel(image,new AnnotationRenderer(),export,new Dialogs());
            var failing=new Ocr { Error=new InvalidOperationException("PUBLIC PRIVATE OCR TEXT Alpha SECRET") };
            var failedWindow=new EditorWindow(failing,diagnostics:journal) {DataContext=failedEditor};failedWindow.Show();
            Click(failedWindow,"SelectImageTextButton");Wait(()=>failedEditor.TextSelection is {Busy:false} failed && failed.Status.Contains("未完成"));
            Require(failedEditor.TextSelection!.Document is null && failedWindow.IsVisible && !failedEditor.IsDirty,"Failed OCR destroyed or changed the editor.");
            failedEditor.TextSelection.Deactivate();failedEditor.BeginPointer(new(10,10),false);failedEditor.EndPointer(new(100,50));
            Require(failedEditor.BaseAnnotations.Count==1,"Failed OCR blocked ordinary annotation.");failedWindow.CancelScreenshot();
            journal.FlushAsync().GetAwaiter().GetResult(); string logs=File.ReadAllText(Path.Combine(folder,"logs","events.jsonl"));
            Require(!logs.Contains("Alpha") && !logs.Contains("SECRET") && !logs.Contains("Hello"),"Text selection diagnostics contain screenshot text.");
            Console.WriteLine("PASS real Windows English OCR coordinates, double-click word span and actual word clipboard; logs contain no screenshot/OCR text. No online OCR or paid provider contacted.");
            return 0;
        }
        catch(Exception error)
        { Console.WriteLine("::error title=Image text selection::"+stage+": "+error.GetType().Name+": "+error.Message.Replace("%","%25").Replace("\r","%0D").Replace("\n","%0A"));return 1; }
        finally
        {
            Up(); foreach(Window w in app.Windows.Cast<Window>().ToArray()) { if(w is EditorWindow editor)editor.CancelScreenshot();else w.Close(); }
            SynchronizationContext.SetSynchronizationContext(previous); Directory.Delete(folder,true);
        }
    }
    private static BitmapSource Article()
    {
        var image=BitmapSource.Create(800,1800,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)255,800*1800*4).ToArray(),3200);image.Freeze();
        return new AnnotationRenderer().Render(image,new[]{(new PixelPoint(40,60),"Alpha beta SECRET"),(new PixelPoint(40,150),"中文测试"),(new PixelPoint(40,1500),"Gamma delta")}.Select(a=>new Annotation(Guid.NewGuid(),AnnotationTool.Text,a.Item1,a.Item1+new PixelPoint(600,50),"#000000",2,40,12,a.Item2,[])));
    }
    private static string Hash(BitmapSource image) { var bytes=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(bytes,image.PixelWidth*4,0);return Convert.ToHexString(SHA256.HashData(bytes)); }
    private static void Click(Window window,string name)
    {
        var button=(ButtonBase)window.FindName(name);Require(button.Command?.CanExecute(null)==true,"Disabled UI command: "+name);button.Command!.Execute(null);window.UpdateLayout();
    }
    private static void NativeClick(FrameworkElement element) { var p=element.PointToScreen(new Point(element.ActualWidth/2,element.ActualHeight/2));MoveScreen(p);Down();Up();Pump(40); }
    private static void Drag(EditorCanvas canvas,PixelPoint first,PixelPoint last) { Move(canvas,first);Down();Move(canvas,last);Up();Pump(40); }
    private static void Move(EditorCanvas canvas,PixelPoint point) => MoveScreen(canvas.PointToScreen(new Point(point.X,point.Y)));
    private static void MoveScreen(Point point) { Require(SetCursorPos((int)Math.Round(point.X),(int)Math.Round(point.Y)),"Native cursor movement failed.");Pump(25); }
    private static void Down() { mouse_event(0x0002,0,0,0,UIntPtr.Zero);Pump(25); }
    private static void Up() { mouse_event(0x0004,0,0,0,UIntPtr.Zero);Pump(25); }
    private static void Pump(int milliseconds) { var task=Task.Delay(milliseconds);Wait(()=>task.IsCompleted); }
    private static void Wait(Func<bool> condition) => TextWindowChecks.PumpUntil(condition,TimeSpan.FromSeconds(20));
    private static void Require(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
    private static void Preview(Window window,string name)
    {
        window.UpdateLayout();var a=window.PointToScreen(new Point());var b=window.PointToScreen(new Point(window.ActualWidth,window.ActualHeight));var capture=new DesktopCaptureService();
        var bounds=PixelRect.FromPoints(new(a.X,a.Y),new(b.X,b.Y)).Intersect(capture.GetDisplays().First(d=>d.Primary).Bounds);
        Directory.CreateDirectory("artifacts/validation/ui-previews");new ImageExportService().SavePng(capture.CaptureRegion(bounds),"artifacts/validation/ui-previews/"+name+".png");
    }
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
    private sealed class Dialogs:IEditorDialogs
    { public string? ChoosePngPath()=>null;public TextInput? EditText(string text,string color,double fontSize)=>null;public CloseChoice ConfirmClose()=>CloseChoice.Discard;public void ShowError(string message)=>throw new InvalidOperationException("Unexpected image export error."); }
    private sealed class Export:IImageExportService
    { public int Copies;public BitmapSource? Last;public Task CopyAsync(BitmapSource image){Copies++;Last=image;return Task.CompletedTask;}public void SavePng(BitmapSource image,string path)=>Last=image; }
    private sealed class DelayedClipboard:ITextClipboardService
    {
        public string Text=""; public CancellationToken Token; public int Copies;
        public readonly TaskCompletionSource<bool> Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task CopyAsync(string text,CancellationToken token,IProgress<int>? retries=null)
        { Text=text;Token=token;await Release.Task.WaitAsync(token);token.ThrowIfCancellationRequested();Copies++; }
    }
    private sealed class FakeDesktop(BitmapSource image):IDesktopCaptureService
    { public IReadOnlyList<DisplayInfo> GetDisplays()=>[new("PUBLIC",new(0,0,800,1800),true)];public DesktopFrame Capture()=>new(image,new(0,0,800,1800),GetDisplays());public BitmapSource CaptureRegion(PixelRect region)=>image;public PixelPoint CursorPosition=>new(20,20);public void PlaceOverlay(Window window,PixelRect bounds){} }
    private sealed class Ocr:IOcrService
    {
        public int Calls;public TaskCompletionSource<OcrResult>? Pending;public Exception? Error; public readonly List<ImagePayload> Payloads=[];
        public IReadOnlyList<string> Languages=>["en-US"];
        public static OcrResult Result=>new([
            new("Alpha beta",new(40,60,260,45),null,[new("Alpha",new(40,60,120,45)),new("beta",new(190,60,110,45))]),
            new("SECRET",new(350,60,180,45),null,[new("SECRET",new(350,60,180,45))]),
            new("中文测试",new(40,150,180,45),null),new("Gamma delta",new(40,1500,260,45),null,[new("Gamma",new(40,1500,140,45)),new("delta",new(210,1500,90,45))])]);
        public Task<OcrResult> RecognizeAsync(ImagePayload payload,CancellationToken token)=>RecognizeAsync(payload,null,token);
        public Task<OcrResult> RecognizeAsync(ImagePayload payload,string? language,CancellationToken token) { var pending=Pending;Payloads.Add(payload);Interlocked.Increment(ref Calls);return Error is { } error ? Task.FromException<OcrResult>(error) : pending?.Task??Task.FromResult(Result); }
    }
}
