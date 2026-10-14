using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class WindowsShareChecks
{
    public static int Run(bool native)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Exception? dispatcherError = null;
        DispatcherUnhandledExceptionEventHandler handler = (_, e) => { dispatcherError = e.Exception; e.Handled = true; };
        app.DispatcherUnhandledException += handler;
        var image = Image();
        try
        {
            if (native) return Native(app, image);
            var export = new FakeExport(); var fake = new FakeShare();
            var window = new WindowsShareWindow(image, export, createSession: _ => fake);
            window.Show(); Pump(() => fake.Shown == 1);
            Require(fake.Prepared == 1 && fake.Width == 80 && fake.Height == 60, "Share opened without the screenshot.");
            Require(window.Icon is not null, "Sheep window icon was not loaded.");
            Require(window.IsVisible && window.Owner is null, "Share window depends on screenshot overlay.");
            FindButton(window, "打开系统分享").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(() => fake.Shown == 2);
            Require(fake.Prepared == 1, "Reopening share redundantly encoded screenshot.");
            FindButton(window, "复制图片").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(() => export.Copies == 1);
            window.Close(); Require(fake.Disposed, "Closing share did not detach native events.");
            Console.WriteLine("PASS share opens with screenshot, reuses prepared data and keeps copy fallback.");

            window = new WindowsShareWindow(image, export, createSession: _ => throw new NotSupportedException("Public unavailable fixture"));
            window.Show(); Pump(() => TextWindowChecks.Descendants<TextBlock>(window).Any(t => t.Text.Contains("Public unavailable fixture")));
            Require(window.IsVisible && TextWindowChecks.Descendants<System.Windows.Controls.Image>(window).Single().Source is not null, "Native API failure discarded screenshot.");
            Require(FindButton(window, "复制图片").IsEnabled && FindButton(window, "保存 PNG").IsEnabled, "Fallback actions were disabled.");
            window.Close(); Console.WriteLine("PASS native share failure keeps screenshot and actionable fallback.");

            fake = new FakeShare { Hold = true };
            window = new WindowsShareWindow(image, export, createSession: _ => fake);
            window.Show(); Pump(() => fake.Prepared == 1); window.Close(); Pump(() => fake.Cancelled);
            Require(fake.Disposed && fake.Shown == 0, "Closed window opened a late native panel.");
            Console.WriteLine("PASS closing share cancels preparation and ignores late results.");

            var capture = new FakeCapture(image); var renderer = new AnnotationRenderer();
            using var pins = new PinManager(export); fake = new FakeShare();
            var hub = new FeatureHub(renderer, export, capture, pins, models: new ModelStore("unused-models"), shareFactory: _ => fake);
            using var selection = new SelectionSession(new(image, new(0,0,80,60), capture.GetDisplays()), capture, renderer, export, []);
            selection.Show(); selection.ViewModel.SetSelection(new(10,10,40,30)); selection.BeginEditing();
            var overlay = app.Windows.OfType<SelectionWindow>().Single();
            TextWindowChecks.Descendants<Button>(overlay).Single(b => Equals(b.ToolTip,"Windows 系统分享")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(selection.Completion.IsCompleted && !app.Windows.OfType<SelectionWindow>().Any(), "Share did not close the selection overlays.");
            var result = selection.Completion.GetAwaiter().GetResult(); Require(result?.Action == SelectionAction.Share, "Toolbar still uses old sharing action.");
            var workflow = hub.HandleAsync(result!); Pump(() => workflow.IsCompleted && fake.Shown == 1); workflow.GetAwaiter().GetResult();
            window = app.Windows.OfType<WindowsShareWindow>().Single(); window.Close();
            Console.WriteLine("PASS actual selection toolbar routes to Windows share with the selected image.");

            using (var tray = new TrayService())
            {
                var notify = (System.Windows.Forms.NotifyIcon)typeof(TrayService).GetField("icon", BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(tray)!;
                Require(notify.Visible && notify.Icon is not null, "Notification-area icon is missing.");
                using var bitmap = notify.Icon!.ToBitmap();
                Require(bitmap.Width >= 16 && bitmap.GetPixel(bitmap.Width/2, bitmap.Height-3).G > bitmap.GetPixel(bitmap.Width/2, bitmap.Height-3).R, "Tray does not contain the sheep artwork.");
                Require(notify.ContextMenuStrip!.Items.Count >= 6, "Tray menu was lost.");
            }
            Require(dispatcherError is null, "Unhandled dispatcher error: " + dispatcherError?.GetType().Name);
            Console.WriteLine("PASS actual tray icon and menu use embedded sheep artwork.");
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            foreach(var window in app.Windows.Cast<Window>().ToArray()) window.Close();
            app.DispatcherUnhandledException -= handler; SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
    private static int Native(Application app, BitmapSource image)
    {
        string folder = Path.Combine(Path.GetTempPath(), "Snip.Share.Check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder,"public-fixture.png"); new ImageExportService().SavePng(image,path);
            var read = StorageFile.GetFileFromPathAsync(path).AsTask(); Pump(() => read.IsCompleted); var file = read.GetAwaiter().GetResult();
            var package = new DataPackage(); WindowsShareSession.FillPackage(package,file); var view = package.GetView();
            Require(view.Contains(StandardDataFormats.StorageItems) && view.Contains(StandardDataFormats.Bitmap), "Share payload lacks file or bitmap.");
            var items = view.GetStorageItemsAsync().AsTask(); Pump(() => items.IsCompleted);
            Require(items.Result.Count == 1 && items.Result[0].Path == path, "Native payload refers to the wrong file.");
            var bitmap = view.GetBitmapAsync().AsTask(); Pump(() => bitmap.IsCompleted);
            var open = bitmap.Result.OpenReadAsync().AsTask(); Pump(() => open.IsCompleted);
            using(var stream = open.Result)
            {
                var decode = global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(); Pump(() => decode.IsCompleted);
                Require(decode.Result.PixelWidth == 80 && decode.Result.PixelHeight == 60, "Native share image has wrong dimensions.");
            }
            Console.WriteLine("PASS actual WinRT DataPackage exposes readable PNG file and bitmap.");
            if (!DataTransferManager.IsSupported()) { Console.WriteLine("UNRUN native Windows share panel: OS reports sharing unsupported; payload check passed."); return 2; }
            var owner = new Window { Width=420, Height=240, Title="Public native share fixture" }; owner.Show();
            var handle = new WindowInteropHelper(owner).EnsureHandle(); var manager = DataTransferManagerInterop.GetForWindow(handle);
            bool requested=false; Exception? failure=null;
            global::Windows.Foundation.TypedEventHandler<DataTransferManager,DataRequestedEventArgs> observe = (_,args) =>
            {
                try { var nativeView=args.Request.Data.GetView(); Require(nativeView.Contains(StandardDataFormats.Bitmap) && nativeView.Contains(StandardDataFormats.StorageItems), "Native panel request was not supplied."); requested=true; }
                catch(Exception error) { failure=error; }
            };
            using var session = new WindowsShareSession(handle);
            var prepare=session.PrepareAsync(image,CancellationToken.None); Pump(() => prepare.IsCompleted); prepare.GetAwaiter().GetResult();
            manager.DataRequested += observe;
            try
            {
                session.Show(); Pump(() => requested || failure is not null);
                if(failure is not null) throw failure;
                Require(requested,"System panel did not request screenshot data.");
                Console.WriteLine("PASS actual desktop HWND opens native Windows sharing and requests PNG; no target selected or sent.");
            }
            finally { manager.DataRequested -= observe; owner.Close(); }
            return 0;
        }
        finally { Directory.Delete(folder,true); }
    }
    private static Button FindButton(Window window,string content) => TextWindowChecks.Descendants<Button>(window).Single(b => Equals(b.Content,content));
    private static void Pump(Func<bool> done)=>TextWindowChecks.PumpUntil(done,TimeSpan.FromSeconds(15));
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static BitmapSource Image()
    {
        var pixels=new byte[80*60*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=12;pixels[i+1]=160;pixels[i+2]=40;pixels[i+3]=255;}
        var bitmap=BitmapSource.Create(80,60,96,96,PixelFormats.Bgra32,null,pixels,320);bitmap.Freeze();return bitmap;
    }
    private sealed class FakeShare : IWindowsShareSession
    {
        public int Prepared,Shown,Width,Height;public bool Hold,Disposed,Cancelled;
        public async Task PrepareAsync(BitmapSource image,CancellationToken token)
        {
            Prepared++;Width=image.PixelWidth;Height=image.PixelHeight;
            if(!Hold)return;
            try { await Task.Delay(Timeout.Infinite,token); }catch(OperationCanceledException){Cancelled=true;throw;}
        }
        public void Show()=>Shown++;
        public void Dispose()=>Disposed=true;
    }
    private sealed class FakeExport : IImageExportService { public int Copies;public void SavePng(BitmapSource image,string path){} public Task CopyAsync(BitmapSource image){Copies++;return Task.CompletedTask;} }
    private sealed class FakeCapture(BitmapSource image) : IDesktopCaptureService
    {
        public IReadOnlyList<DisplayInfo> GetDisplays()=>[new("fixture",new(0,0,80,60),true)];
        public DesktopFrame Capture()=>new(image,new(0,0,80,60),GetDisplays());
        public Snip.Core.PixelPoint CursorPosition=>new(20,20);public void PlaceOverlay(Window window,Snip.Core.PixelRect bounds){}
    }
}
