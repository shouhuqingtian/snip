using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class WindowPlacementChecks
{
    public static int Run()
    {
        var app=Application.Current ?? new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Exception? unhandled=null;
        DispatcherUnhandledExceptionEventHandler errorHandler=(_,args)=>{unhandled=args.Exception;args.Handled=true;};
        app.DispatcherUnhandledException+=errorHandler;
        try
        {
            var window=Show(); var handle=new WindowInteropHelper(window).Handle;
            var area=WorkArea(handle);Require(Inside(handle,area),"Initial window is outside the real monitor work area.");
            Require(GetDpiForWindow(handle)>0,"Window has no actual HWND DPI.");
            Require(((Button)window.FindName("CloseResultButton")).IsVisible,"Visible close action is missing.");
            Console.WriteLine("PASS normal result initially fits actual physical work area and keeps close button visible.");

            // Real HWND placement recreates the reported inaccessible title bar, not just a mocked calculator.
            Require(SetWindowPos(handle,IntPtr.Zero,area.Left-240,area.Top-300,area.Right-area.Left+400,area.Bottom-area.Top+400,0x0014),"Failed to create offscreen native regression fixture.");
            TextWindowChecks.PumpUntil(()=>Inside(handle,area),TimeSpan.FromSeconds(4));
            Require(window.IsVisible,"Recovering offscreen placement closed the result.");
            Console.WriteLine("PASS actual oversized HWND with negative top is resized and brought back inside monitor work area.");

            window.Close();
            window=Show(compact:true);window.UpdateLayout();
            var scroll=(ScrollViewer)window.FindName("WorkspaceScroll");
            TextWindowChecks.PumpUntil(()=>scroll.ScrollableHeight>0,TimeSpan.FromSeconds(3));
            var close=(Button)window.FindName("CloseResultButton");
            var content=(FrameworkElement)window.Content;
            var closeBounds=close.TransformToAncestor(content).TransformBounds(new Rect(0,0,close.ActualWidth,close.ActualHeight));
            Require(closeBounds.Top>=0 && closeBounds.Bottom<=content.ActualHeight,"Close button was clipped in a low-height window.");
            scroll.ScrollToBottom();window.UpdateLayout();Require(scroll.VerticalOffset>0,"Compact content cannot scroll.");
            Require(((TextBox)window.FindName("RecognizedText")).ActualHeight>=70,"Compact text area became unreadable.");
            Console.WriteLine("PASS low-height result keeps close control accessible and scrolls remaining readable content.");

            var source=(TextBox)window.FindName("RecognizedText");source.Focus();
            var escape=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(source)!,0,Key.Escape) { RoutedEvent=Keyboard.PreviewKeyDownEvent };
            source.RaiseEvent(escape);
            Require(escape.Handled && !window.IsVisible,"Esc did not close while a text field had focus.");
            Console.WriteLine("PASS routed Esc closes the result even when editable text has keyboard focus.");

            var slow=new SlowOcr();window=Show(slow);var vm=(TextResultViewModel)window.DataContext;
            TextWindowChecks.PumpUntil(()=>slow.Started,TimeSpan.FromSeconds(4));
            ((Button)window.FindName("CloseResultButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TextWindowChecks.PumpUntil(()=>slow.Cancelled && !vm.Busy,TimeSpan.FromSeconds(4));
            Require(!window.IsVisible,"Busy OCR blocked closing result.");
            Require(unhandled is null,"Unhandled dispatcher failure: "+unhandled?.GetType().Name);
            Console.WriteLine("PASS visible close button cancels active OCR without waiting for native result or closing app.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally
        {
            foreach(var window in app.Windows.Cast<Window>().ToArray())window.Close();
            app.DispatcherUnhandledException-=errorHandler;SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
    private static TextResultWindow Show(IOcrService? ocr=null,bool compact=false)
    {
        var image=BitmapSource.Create(80,60,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)255,80*60*4).ToArray(),320);image.Freeze();
        var window=new TextResultWindow(new(image,ocr ?? new TextWindowChecks.FakeOcr(),new ModelStore("unused-models"),new TextWindowChecks.FakeTranslation()){ AutoTranslate=true });
        if(compact){window.MinWidth=420;window.MinHeight=280;window.Width=540;window.Height=420;}
        window.Show();window.UpdateLayout();return window;
    }
    private static NativeRect WorkArea(IntPtr handle)
    {
        var info=new MonitorInfo { Size=Marshal.SizeOf<MonitorInfo>() };Require(GetMonitorInfo(MonitorFromWindow(handle,2),ref info),"No monitor work area.");return info.Work;
    }
    private static bool Inside(IntPtr handle,NativeRect area)
    {
        if(!GetWindowRect(handle,out var rect))return false;
        return rect.Left>=area.Left && rect.Top>=area.Top && rect.Right<=area.Right && rect.Bottom<=area.Bottom;
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private sealed class SlowOcr : IOcrService
    {
        public volatile bool Started,Cancelled;
        public IReadOnlyList<string> Languages=>["en-US"];
        public Task<OcrResult> RecognizeAsync(ImagePayload image,CancellationToken token)=>RecognizeAsync(image,null,token);
        public async Task<OcrResult> RecognizeAsync(ImagePayload image,string? language,CancellationToken token)
        {
            Started=true;try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){Cancelled=true;throw;}
            return new([]);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size;public NativeRect Monitor,Work;public uint Flags; }
    [DllImport("user32.dll")]private static extern IntPtr MonitorFromWindow(IntPtr handle,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetWindowRect(IntPtr handle,out NativeRect rect);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetWindowPos(IntPtr handle,IntPtr after,int x,int y,int width,int height,uint flags);
}
