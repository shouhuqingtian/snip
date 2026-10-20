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

internal static class CaptureFeedbackChecks
{
    [DllImport("user32.dll")] private static extern bool SetPhysicalCursorPos(int x,int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out NativeRect bounds);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count,Input[] input,int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X,Y; public uint Wheel,Flags,Time; public UIntPtr Extra; }
    public static int Run()
    {
        var app=Application.Current??new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var previous=SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var desktop=new DesktopCaptureService(); var cursor=desktop.CursorPosition;
        using var notifications=new CopyNotificationService(app.Dispatcher);
        var owner=new Window { Style=null,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Left=60,Top=100,Width=620,Height=440,Background=Brushes.CornflowerBlue };
        using var deadline=new System.Threading.Timer(_=> { Console.Error.WriteLine("FAIL native copy-feedback deadline."); Environment.Exit(1); },null,TimeSpan.FromSeconds(60),Timeout.InfiniteTimeSpan);
        try
        {
            Ui.EnsureTheme(); owner.Show(); owner.Activate(); owner.UpdateLayout(); Move(new(180,200)); Pause(100);
            var target=DesktopWindows.Snapshot(includeCurrentProcess:true).Single(w=>w.Handle==new WindowInteropHelper(owner).Handle);
            VerifyHoverRevisions(desktop,target,owner);
            VerifyMiddleAndNotice(desktop,target,owner,notifications);
            VerifyCoordinator(desktop,target,owner,notifications);
            VerifyStandalone(desktop,owner,notifications);
            VerifyFailureAndDisposal(notifications);
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/capture-feedback.json",JsonSerializer.Serialize(new {
                StaleOverlappingResultIgnored=true, ManualClickLocksRegion=true, MiddleCopiesFrozenPixels=true,
                WheelDoesNotCopy=true, DragMiddleIgnored=true, DuplicateEnterMiddleIgnored=true,
                FeedbackAfterSuccess=true, FailureAndSaveHaveNoFeedback=true, NotificationNoActivation=true,
                NotificationInputTransparent=true, NotificationAutoCloses=true, NextCaptureExcludesNotification=true,
                StandaloneMiddleCopies=true, PendingNotificationCanBeCancelled=true
            },new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { notifications.Hide(); owner.Close(); SetPhysicalCursorPos((int)cursor.X,(int)cursor.Y); SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void VerifyHoverRevisions(DesktopCaptureService desktop,WindowCaptureInfo target,Window owner)
    {
        Move(new(target.Bounds.X+80,target.Bounds.Y+90));
        var probe=new SequenceProbe();
        using(var selection=new SelectionSession(desktop.Capture(),desktop,new AnnotationRenderer(),new GateExport(),[target],()=>probe))
        {
            selection.Show(); Pause(180); Require(probe.Calls==0 && selection.ViewModel.DisplayedSelection==target.Bounds,"Stationary screenshot changed its initial window.");
            Move(new(target.Bounds.X+86,target.Bounds.Y+90)); Wait(()=>probe.Calls==1);
            Move(new(target.Bounds.X+100,target.Bounds.Y+90));
            var old=new PixelRect(target.Bounds.X+20,target.Bounds.Y+20,320,220);
            probe.First.SetResult(old); Wait(()=>probe.Calls==2);
            Require(selection.ViewModel.DisplayedSelection==target.Bounds,"Old overlapping hover result was applied after the pointer moved.");
            var latest=new PixelRect(target.Bounds.X+70,target.Bounds.Y+60,240,140);
            probe.Second.SetResult(latest); Wait(()=>selection.ViewModel.DisplayedSelection==latest);
            HotkeyChecks.Press(0x1B); Wait(()=>selection.Completion.IsCompleted);
        }
        owner.Activate(); Move(new(target.Bounds.X+80,target.Bounds.Y+90)); probe=new SequenceProbe();
        using(var selection=new SelectionSession(desktop.Capture(),desktop,new AnnotationRenderer(),new GateExport(),[target],()=>probe))
        {
            selection.Show(); Move(new(target.Bounds.X+86,target.Bounds.Y+90)); Wait(()=>probe.Calls==1);
            Mouse(2); Mouse(4); Wait(()=>selection.Editing);
            var locked=selection.ViewModel.Selection; Require(locked==target.Bounds && probe.Disposed,"Click did not lock current region and release pending helper.");
            probe.First.SetResult(new(target.Bounds.X+30,target.Bounds.Y+30,240,140)); Pause(120);
            Require(selection.ViewModel.Selection==locked,"Late result changed a clicked region.");
            HotkeyChecks.Press(0x1B); Wait(()=>selection.Completion.IsCompleted);
        }
        Console.WriteLine("PASS native stationary entry, overlapping stale request rejection, latest request and left-click locking; no delayed overwrite.");
    }
    private static void VerifyMiddleAndNotice(DesktopCaptureService desktop,WindowCaptureInfo target,Window owner,CopyNotificationService notifications)
    {
        owner.Activate(); Move(new(target.Bounds.X+100,target.Bounds.Y+100));
        var frame=desktop.Capture(); var gate=new GateExport { Gate=new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var selection=new SelectionSession(frame,desktop,new AnnotationRenderer(),new CopyFeedbackExport(gate,notifications),[target]);
        selection.Show(); Mouse(0x800,120); Pause(100);
        Require(gate.Copies==0 && !selection.Completion.IsCompleted,"Rotating wheel copied the screenshot.");
        Mouse(2); Wait(()=>selection.ViewModel.IsDragging); Middle(); Pause(80);
        Require(gate.Copies==0 && selection.ViewModel.IsDragging,"Middle click exported an unfinished selection drag.");
        Mouse(4); Wait(()=>selection.Editing);
        Middle(); Wait(()=>gate.Copies==1);
        Require(Toast() is null && !selection.Completion.IsCompleted,"Feedback appeared before clipboard export succeeded.");
        HotkeyChecks.Press(0x0D); Middle(); Pause(80); Require(gate.Copies==1,"Enter/middle started duplicate image exports.");
        gate.Gate.SetResult(); Wait(()=>selection.Completion.IsCompleted && Toast() is not null);
        Require(!Application.Current.Windows.OfType<SelectionWindow>().Any(),"Feedback appeared while selection overlay remained open.");
        AssertClipboard(frame,target.Bounds);
        var toast=Toast()!; var foreground=DesktopWindows.Foreground;
        Require(!toast.IsActive && foreground!=new WindowInteropHelper(toast).Handle && DesktopWindows.IsInputTransparent(new WindowInteropHelper(toast).Handle),"Copy notice stole activation or mouse input.");
        SavePreview(toast);
        Wait(()=>Toast() is null);
        Require(DesktopWindows.Foreground==foreground,"Notification dismissal changed focus.");
        Console.WriteLine("PASS native middle-button copy, wheel/drag/duplicate guards and frozen pixels; success-only feedback appears after overlay closes, never activates, passes input and closes automatically.");
    }
    private static void VerifyCoordinator(DesktopCaptureService desktop,WindowCaptureInfo target,Window owner,CopyNotificationService notifications)
    {
        owner.Activate(); Move(new(target.Bounds.X+100,target.Bounds.Y+100)); notifications.NotifyCopied(); Wait(()=>Toast() is not null);
        GetWindowRect(new WindowInteropHelper(Toast()!).Handle,out var noticeBounds);
        var recorder=new RecordingDesktop(); var export=new CopyFeedbackExport(new ImageExportService(),notifications); var renderer=new AnnotationRenderer();
        using var pins=new PinManager(export); var features=new FeatureHub(renderer,export,recorder,pins);
        using var coordinator=new CaptureCoordinator(recorder,renderer,export,features,beforeCapture:notifications.Hide);
        var capture=coordinator.CaptureAsync(); Wait(()=>recorder.Last is not null && Application.Current.Windows.OfType<SelectionWindow>().Any(w=>w.IsActive));
        var point=new PixelPoint(noticeBounds.Left+10,noticeBounds.Top+10);
        var image=new CroppedBitmap(recorder.Last!.Image,new((int)(point.X-recorder.Last.Bounds.X),(int)(point.Y-recorder.Last.Bounds.Y),1,1));
        Require(Pixels(image).SequenceEqual(new byte[]{237,149,100}),"Prior success notice leaked into next frozen screenshot.");
        Middle(); Wait(()=>capture.IsCompleted && Toast() is not null); capture.GetAwaiter().GetResult();
        AssertClipboard(recorder.Last,target.Bounds);
        Require(DesktopWindows.Foreground==new WindowInteropHelper(owner).Handle,"New success notice stole focus from restored capture target.");
        notifications.Hide();
        Console.WriteLine("PASS actual capture coordinator hides prior feedback before freezing desktop; next image is clean; middle-copy restores target focus and displays feedback.");
    }
    private static void VerifyStandalone(DesktopCaptureService desktop,Window owner,CopyNotificationService notifications)
    {
        var image=TextWindowChecks.Image(400,280); var export=new CopyFeedbackExport(new ImageExportService(),notifications); var renderer=new AnnotationRenderer();
        using var pins=new PinManager(export); var hub=new FeatureHub(renderer,export,desktop,pins);
        var vm=new EditorViewModel(image,renderer,export,new Dialogs()); var window=hub.OpenEditor(vm);
        try
        {
            window.UpdateLayout(); var canvas=TextWindowChecks.Descendants<EditorCanvas>(window).Single();
            var point=canvas.PointToScreen(new(50,50)); Move(new(point.X,point.Y)); Middle(); Wait(()=>!window.IsVisible && Toast() is not null);
            Require(Clipboard.GetImage().PixelWidth==400 && Pixels(Clipboard.GetImage()).SequenceEqual(Pixels(image)),"Standalone middle copy changed pixels or left editor open.");
            notifications.Hide(); owner.Activate();
        }
        finally { window.CancelScreenshot(); }
        Console.WriteLine("PASS real independent editor middle button copies and completes through the same toolbar action.");
    }
    private static void VerifyFailureAndDisposal(CopyNotificationService notifications)
    {
        var image=TextWindowChecks.Image(20,20); var failing=new CopyFeedbackExport(new GateExport{Fail=true},notifications);
        var task=failing.CopyAsync(image); Wait(()=>task.IsCompleted);
        bool failed=false; try { task.GetAwaiter().GetResult(); } catch(IOException) { failed=true; }
        failing.SavePng(image,"public-unused.png"); Pause(100);
        Require(failed && Toast() is null,"Failure or save displayed a copy-success notice.");
        notifications.NotifyCopied(); notifications.Hide(); Pause(100); Require(Toast() is null,"Cancelled pending notice still appeared.");
        using(var disposed=new CopyNotificationService(Application.Current.Dispatcher)) { disposed.NotifyCopied(); }
        Pause(100); Require(Toast() is null,"Disposed notification service showed queued feedback.");
        Console.WriteLine("PASS failed copy and PNG save do not claim clipboard success; pending and disposed notifications cannot appear later.");
    }
    private static CopyNotificationWindow? Toast()=>Application.Current.Windows.OfType<CopyNotificationWindow>().SingleOrDefault();
    private static void SavePreview(CopyNotificationWindow window)
    {
        window.UpdateLayout(); var content=(FrameworkElement)window.Content;
        var image=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32); image.Render(content);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); Directory.CreateDirectory("artifacts/validation/ui-previews");
        using var file=File.Create("artifacts/validation/ui-previews/43-copy-notification.png"); png.Save(file);
    }
    private static void AssertClipboard(DesktopFrame frame,PixelRect expected)
    {
        var wanted=new CroppedBitmap(frame.Image,new((int)(expected.X-frame.Bounds.X),(int)(expected.Y-frame.Bounds.Y),(int)expected.Width,(int)expected.Height));
        var actual=Clipboard.GetImage(); Require(actual.PixelWidth==wanted.PixelWidth && actual.PixelHeight==wanted.PixelHeight && Pixels(actual).SequenceEqual(Pixels(wanted)),"Copied pixels differ from selected frozen region.");
    }
    private static byte[] Pixels(BitmapSource image)
    { var converted=new FormatConvertedBitmap(image,PixelFormats.Bgr24,null,0); var pixels=new byte[converted.PixelWidth*converted.PixelHeight*3]; converted.CopyPixels(pixels,converted.PixelWidth*3,0); return pixels; }
    private static void Middle() { Mouse(0x20); Mouse(0x40); }
    private static void Mouse(uint flags,uint wheel=0)
    { Input[] inputs=[new(){Type=0,Data=new(){Mouse=new(){Flags=flags,Wheel=wheel}}}]; Require(SendInput(1,inputs,Marshal.SizeOf<Input>())==1,"Native mouse input failed."); }
    private static void Move(PixelPoint point) { Require(SetPhysicalCursorPos((int)point.X,(int)point.Y),"Native pointer move failed."); Pause(40); }
    private static void Wait(Func<bool> done)=>TextWindowChecks.PumpUntil(done,TimeSpan.FromSeconds(5));
    private static void Pause(int milliseconds) { var task=Task.Delay(milliseconds); Wait(()=>task.IsCompleted); }
    private static void Require(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
    private sealed class SequenceProbe:IHoverRegionProbe
    {
        public int Calls; public bool Disposed;
        public TaskCompletionSource<PixelRect?> First=new(TaskCreationOptions.RunContinuationsAsynchronously),Second=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PixelRect?> FindAsync(WindowCaptureInfo target,PixelPoint point,CancellationToken token)=>++Calls==1?First.Task:Second.Task;
        public void Dispose()=>Disposed=true;
    }
    private sealed class GateExport:IImageExportService
    {
        public TaskCompletionSource? Gate; public int Copies; public bool Fail;
        public void SavePng(BitmapSource image,string path) { }
        public async Task CopyAsync(BitmapSource image)
        { Copies++; if(Fail)throw new IOException("Public copy failure fixture."); if(Gate is not null)await Gate.Task; await new ImageExportService().CopyAsync(image); }
    }
    private sealed class RecordingDesktop:IDesktopCaptureService
    {
        private readonly DesktopCaptureService inner=new(); public DesktopFrame? Last;
        public IReadOnlyList<DisplayInfo> GetDisplays()=>inner.GetDisplays(); public DesktopFrame Capture()=>Last=inner.Capture();
        public PixelPoint CursorPosition=>inner.CursorPosition; public void PlaceOverlay(Window window,PixelRect bounds)=>inner.PlaceOverlay(window,bounds);
    }
    private sealed class Dialogs:IEditorDialogs
    {
        public string? ChoosePngPath()=>null; public TextInput? EditText(string text,string color,double size)=>null;
        public CloseChoice ConfirmClose()=>CloseChoice.Discard; public void ShowError(string message)=>throw new InvalidOperationException(message);
    }
}
