using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
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

internal static class PinChecks
{
    public static int Run()
    {
        var app=Application.Current ?? new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};Ui.EnsureTheme();
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string folder=Path.Combine(Path.GetTempPath(),"Snip.Pin.Check-"+Guid.NewGuid());Directory.CreateDirectory(folder);
        string stage="initialization";Exception? dispatcherError=null;
        DispatcherUnhandledExceptionEventHandler onError=(_,args)=>{dispatcherError=args.Exception;args.Handled=true;};app.DispatcherUnhandledException+=onError;
        using var watchdog=new System.Threading.Timer(_=>{Console.WriteLine("::error title=Pin deadline::"+stage);Environment.Exit(1);},null,TimeSpan.FromSeconds(55),Timeout.InfiniteTimeSpan);
        try
        {
            var image=Image(240,160);var export=new Export();using var pins=new PinManager(export);
            stage="open empty manager before adding a real selection pin";
            pins.ShowManager();var manager=app.Windows.OfType<PinManagerWindow>().Single();manager.UpdateLayout();
            Require(((FrameworkElement)manager.FindName("EmptyPins")).IsVisible,"Empty state is missing.");
            var capture=new FakeCapture(image);var renderer=new AnnotationRenderer();
            var hub=new FeatureHub(renderer,export,capture,pins,models:new ModelStore(Path.Combine(folder,"unused-models")));
            using(var selection=new SelectionSession(new(image,new(0,0,240,160),capture.GetDisplays()),capture,renderer,export,[]))
            {
                selection.Show();selection.ViewModel.SetSelection(new(10,10,180,100));selection.BeginEditing();
                var overlay=app.Windows.OfType<SelectionWindow>().Single();
                TextWindowChecks.Descendants<Button>(overlay).Single(button=>Equals(button.ToolTip,"钉在桌面")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var result=selection.Completion.GetAwaiter().GetResult();Require(result?.Action==SelectionAction.Pin,"Actual toolbar did not choose pin.");
                Await(hub.HandleAsync(result!));
            }
            manager.UpdateLayout();var list=(ListBox)manager.FindName("PinList");
            Require(list.Items.Count==1 && !((FrameworkElement)manager.FindName("EmptyPins")).IsVisible && pins.ViewModel.Summary.Contains("显示 1 张"),"Open manager kept a stale empty snapshot after toolbar pin.");
            pins.ShowManager();Require(app.Windows.OfType<PinManagerWindow>().Count()==1,"Manager duplicates itself.");
            Console.WriteLine("PASS actual selection pin immediately refreshes an already-open empty manager, counts and thumbnails; reopening reuses one manager.");

            stage="close and restore presentation";
            var first=pins.ViewModel.Entries.Single();pins.Add(image);var second=pins.ViewModel.Entries[0];
            var secondWindow=app.Windows.OfType<PinWindow>().Single(window=>ReferenceEquals(((Image)((Border)window.Content).Child).Source,second.Image) && window.IsActive);
            secondWindow.Left+=18;secondWindow.Top+=12;secondWindow.UpdateLayout();
            ((MenuItem)secondWindow.ContextMenu!.Items.Cast<MenuItem>().Single(item=>Equals(item.Header,"透明度 70%"))).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var bounds=PinWindowPlacement.Bounds(secondWindow);
            secondWindow.SetClickThrough(true);Require(second.ClickThrough,"Click-through state did not reach manager.");
            InvokeRow(manager,second.CloseCommand,()=>!second.IsOpen);manager.UpdateLayout();
            Require(list.Items.Count==2 && pins.ViewModel.RestoreCommand.CanExecute(null),"Closing a pin lost the restore record.");
            Invoke((Button)manager.FindName("RestorePinButton"),()=>second.IsOpen);manager.UpdateLayout();
            var restored=app.Windows.OfType<PinWindow>().Single(window=>window.IsActive);
            Require(PinWindowPlacement.Bounds(restored)==bounds && ((Image)((Border)restored.Content).Child).Opacity==.7 && !restored.IsClickThrough,"Restore lost physical bounds, scale or opacity, or kept mouse passthrough.");
            Require(ReferenceEquals(((Image)((Border)restored.Content).Child).Source,second.Image),"Restore copied the full screenshot unnecessarily.");
            Console.WriteLine("PASS close retains recoverable image; restore reuses pixels, restores physical position/size and opacity, and enables interaction.");

            stage="cycle preserves other visible pins";
            Invoke((Button)manager.FindName("PreviousPinButton"),()=>first.IsCurrent);
            Invoke((Button)manager.FindName("NextPinButton"),()=>second.IsCurrent);
            pins.Cycle(1);Require(first.IsCurrent && app.Windows.OfType<PinWindow>().Count(window=>window.IsVisible)==2,"Cycling hid or lost other pins.");
            foreach(var window in app.Windows.OfType<PinWindow>().ToArray())window.Close();
            pins.RestoreLast();Require(pins.ViewModel.Entries.Count(entry=>entry.IsOpen)==1,"Restore did not choose one most-recently closed pin.");
            Console.WriteLine("PASS previous/next wrap across pins, keep all other pins visible, and restore the most-recently closed item.");

            stage="native configurable shortcuts and rollback";
            using var keys=new PinHotkeyService();using var captureKey=new HotkeyService();using var occupied=new HotkeyService();
            var shot=new HotkeyGesture(3,0x78);var taken=new HotkeyGesture(3,0x79);
            Require(captureKey.Register(shot) && occupied.Register(taken),"Could not reserve public hotkey fixtures.");
            var settings=new PinHotkeySettings(new(3,0x75),new(3,0x76),new(3,0x77));
            Require(keys.Apply(settings),"Could not register three pin keys.");
            var triggered=new List<PinHotkeyAction>();keys.Triggered+=(_,action)=>{triggered.Add(action);if(action==PinHotkeyAction.Restore)pins.RestoreLast();else pins.Cycle(action==PinHotkeyAction.Previous ? -1 : 1);};
            Require(!keys.Apply(settings with {Restore=taken}) && keys.Current==settings && captureKey.Current==shot,"Conflict lost pin or screenshot keys.");
            HotkeyChecks.Press(0x11,0x12,0x75);Pump(()=>triggered.Count==1);Require(triggered[0]==PinHotkeyAction.Restore,"Restore key did not dispatch to pin manager.");
            HotkeyChecks.Press(0x11,0x12,0x76);Pump(()=>triggered.Count==2);
            HotkeyChecks.Press(0x11,0x12,0x77);Pump(()=>triggered.Count==3);
            Require(triggered.SequenceEqual(new[]{PinHotkeyAction.Restore,PinHotkeyAction.Previous,PinHotkeyAction.Next}),"Pin keys dispatch the wrong actions.");
            var swapped=settings with {Previous=settings.Next,Next=settings.Previous};Require(keys.Apply(swapped) && keys.Apply(settings),"Binding swaps fail against the application's own reservations.");
            var store=new PinHotkeySettingsStore(Path.Combine(folder,"pin-hotkeys.json"));store.Save(settings);Require(new PinHotkeySettingsStore(Path.Combine(folder,"pin-hotkeys.json")).Read()==settings,"Restart lost pin settings.");
            var failed=new PinHotkeySettingsViewModel(keys,new BadStore(),settings);failed.SetGesture(PinHotkeyAction.Restore,new(3,0x74));failed.SaveCommand.Execute(null);
            Require(keys.Current==settings && failed.Status.Contains("恢复"),"Write failure lost working bindings.");
            Console.WriteLine("PASS real global restore/previous/next keys, external conflict rollback, self-binding swap, persisted restart and write-failure rollback preserve screenshot hotkey.");

            stage="actual shortcut settings keyboard capture";
            var vm=new PinHotkeySettingsViewModel(keys,store,settings);var keyWindow=new PinHotkeySettingsWindow(vm);keyWindow.Show();keyWindow.Activate();keyWindow.UpdateLayout();
            ((TextBox)keyWindow.FindName("RestoreGestureBox")).Focus();
            HotkeyChecks.Press(0x11,0x12,0x74);Pump(()=>vm.RestoreText=="Ctrl+Alt+F5");
            Invoke((Button)keyWindow.FindName("SavePinShortcutsButton"),()=>keys.Current.Restore==new HotkeyGesture(3,0x74));
            Require(store.Read().Restore==new HotkeyGesture(3,0x74),"Actual XAML did not save key.");
            vm.ResetCommand.Execute(null);vm.SaveCommand.Execute(null);Require(keys.Current==new PinHotkeySettings() && store.Read()==keys.Current,"Clearing optional keys did not persist and unregister.");
            using(var released=new HotkeyService())Require(released.Register(settings.Previous!),"Cleared key remained reserved.");
            keyWindow.Close();Console.WriteLine("PASS actual pin shortcut input/save/clear XAML; cleared bindings are available to another native owner.");

            stage="bounded closed-image history and clear";
            using(var bounded=new PinManager(export,historyLimit:3,historyBudget:240L*160*4*2))
            {
                for(int i=0;i<4;i++){bounded.Add(image);bounded.ViewModel.Entries[0].CloseCommand.Execute(null);}
                Require(bounded.ViewModel.Entries.Count==2 && bounded.ViewModel.Entries.All(entry=>!entry.IsOpen),"Closed screenshot cache exceeded its byte budget.");
                bounded.ClearHistory();Require(bounded.ViewModel.IsEmpty && !bounded.ViewModel.RestoreCommand.CanExecute(null),"Clear history retained closed image records.");
            }
            pins.ClearHistory();Require(pins.ViewModel.Entries.All(entry=>entry.IsOpen),"Clear closed history also removed live pins.");
            pins.Dispose();Require(pins.ViewModel.Entries.Count==0 && !app.Windows.OfType<PinWindow>().Any(),"Exit kept pin windows or recovery image references.");
            Require(dispatcherError is null,"Unhandled pin dispatcher error: "+dispatcherError?.GetType().Name);
            Console.WriteLine("PASS bounded closed-image cache, explicit clear and disposal release records; live pins are retained when clearing history.");return 0;
        }
        catch(Exception error){Console.WriteLine("::error title=Pin checks::"+stage+": "+error.ToString().Replace("%","%25").Replace("\r","%0D").Replace("\n","%0A"));return 1;}
        finally
        {
            foreach(Window window in app.Windows.Cast<Window>().ToArray())window.Close();app.DispatcherUnhandledException-=onError;
            SynchronizationContext.SetSynchronizationContext(previous);if(Directory.Exists(folder))Directory.Delete(folder,true);
        }
    }
    private static void Pump(Func<bool> done)=>TextWindowChecks.PumpUntil(done,TimeSpan.FromSeconds(5));
    private static void Await(Task task){Pump(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    private static void Invoke(Button button,Func<bool> done)
    {Require(button.IsEnabled,"Expected available pin action.");((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();Pump(done);}
    private static void InvokeRow(Window window,System.Windows.Input.ICommand command,Func<bool> done)
    {window.UpdateLayout();Invoke(TextWindowChecks.Descendants<Button>(window).Single(button=>ReferenceEquals(button.Command,command)),done);}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static BitmapSource Image(int width,int height)
    {var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)230,width*height*4).ToArray(),width*4);image.Freeze();return image;}
    private sealed class Export : IImageExportService {public void SavePng(BitmapSource image,string path){}public Task CopyAsync(BitmapSource image)=>Task.CompletedTask;}
    private sealed class FakeCapture(BitmapSource image) : IDesktopCaptureService
    {public IReadOnlyList<DisplayInfo> GetDisplays()=>[new("PublicPinDisplay",new(0,0,240,160),true)];public DesktopFrame Capture()=>new(image,new(0,0,240,160),GetDisplays());public PixelPoint CursorPosition=>new(20,20);public void PlaceOverlay(Window window,PixelRect bounds){}}
    private sealed class BadStore : IPinHotkeySettingsStore {public PinHotkeySettings Read()=>new();public void Save(PinHotkeySettings settings)=>throw new IOException("Public write failure");}
}
