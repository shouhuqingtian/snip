using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class ApplicationSettingsChecks
{
    public static int Run()
    {
        _=Application.Current ?? new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string folder=Path.Combine(Path.GetTempPath(),"Snip Settings 测试 "+Guid.NewGuid().ToString("N"));
        string registry=@"Software\SelfSnip\Checks\"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(folder);
        try
        {
            string executable=Path.Combine(folder,"Snip.exe");File.WriteAllBytes(executable,[0]);
            var startup=new AutoStartService(executable,registry);var prefs=new TranslationPreferencesStore(Path.Combine(folder,"preferences.json"));
            Require(!startup.IsEnabled && prefs.Read() is null,"Fresh settings created an active configuration.");
            var vm=new ApplicationSettingsViewModel(startup,prefs,new(TranslationMode.Offline));
            var settingsWindow=new ApplicationSettingsWindow(vm);settingsWindow.Show();settingsWindow.UpdateLayout();
            using(var key=Registry.CurrentUser.CreateSubKey(registry))key.SetValue("OtherApplication","untouched");
            ((CheckBox)settingsWindow.FindName("StartupCheckBox")).IsChecked=true;
            using(var key=Registry.CurrentUser.OpenSubKey(registry))
                Require((string?)key!.GetValue(AutoStartService.ValueName)=="\""+executable+"\" --startup","Startup registration omitted path quoting or startup argument.");
            Require(new AutoStartService(executable,registry).IsEnabled,"Startup did not survive a fresh service instance.");
            ((ComboBox)settingsWindow.FindName("DefaultModeBox")).SelectedValue=TranslationMode.Basic;
            ((ComboBox)settingsWindow.FindName("DefaultDirectionBox")).SelectedValue="zh-en";
            vm.SaveCommand.Execute(null);
            Require(new TranslationPreferencesStore(Path.Combine(folder,"preferences.json")).Read()==new TranslationPreferences(TranslationMode.Basic,"zh-en"),"Actual settings selector did not persist mode/direction.");
            ((CheckBox)settingsWindow.FindName("StartupCheckBox")).IsChecked=false;
            using(var key=Registry.CurrentUser.OpenSubKey(registry))
                Require(key!.GetValue(AutoStartService.ValueName) is null && (string?)key.GetValue("OtherApplication")=="untouched","Disabling startup changed another application's value.");
            settingsWindow.Width=520;settingsWindow.Height=420;settingsWindow.UpdateLayout();settingsWindow.Close();
            string previous=File.ReadAllText(Path.Combine(folder,"preferences.json"));
            try{prefs.Save(new((TranslationMode)999));throw new Exception("Invalid preference accepted.");}catch(ArgumentException){}
            Require(File.ReadAllText(Path.Combine(folder,"preferences.json"))==previous,"Rejected settings replaced previous preference.");
            Console.WriteLine("PASS actual settings XAML persists mode/direction; isolated HKCU startup registration quotes paths, restores state and removes only its own value.");

            var refused=new ApplicationSettingsViewModel(new RefusedStartup(),prefs,new(TranslationMode.Offline));
            refused.StartWithWindows=true;
            Require(!refused.StartWithWindows && refused.Status.Contains("失败"),"Failed startup write displayed enabled state.");
            Console.WriteLine("PASS failed startup registration retains unchecked state and reports failure.");
            CheckDirectionMigration(folder);
            CheckDuplicateStartup();
            CheckTranslationModes(folder);
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally
        {
            foreach(var window in Application.Current!.Windows.Cast<Window>().ToArray())window.Close();
            Registry.CurrentUser.DeleteSubKeyTree(registry,false);Directory.Delete(folder,true);SynchronizationContext.SetSynchronizationContext(context);
        }
    }
    private static void CheckDirectionMigration(string folder)
    {
        string path=Path.Combine(folder,"legacy-directions.json");var store=new TranslationPreferencesStore(path);
        foreach(var mode in Enum.GetValues<TranslationMode>())
            foreach(var direction in new[]{"en-zh","zh-en"})
            {
                File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new { Mode=mode,Direction=direction }));
                Require(store.Read()==new TranslationPreferences(mode,"auto"),"Legacy direction did not migrate or changed translation mode.");
                store.Save(new(mode,"auto"));Require(new TranslationPreferencesStore(path).Read()==new TranslationPreferences(mode,"auto"),"Automatic direction did not survive restart.");
                store.Save(new(mode,direction));Require(new TranslationPreferencesStore(path).Read()==new TranslationPreferences(mode,direction),"New explicit direction override did not survive restart.");
            }
        foreach(string version in new[]{"null","\"invalid\"","{}"})
        {
            File.WriteAllText(path,"{\"SchemaVersion\":"+version+",\"Mode\":1,\"Direction\":\"auto\"}");
            try{store.Read();throw new Exception("Corrupt schema version accepted.");}catch(InvalidOperationException){}
        }
        Console.WriteLine("PASS legacy en/zh directions migrate to automatic without changing mode; new automatic and explicit preferences survive restart; invalid schema fails safely.");
    }
    private static void CheckDuplicateStartup()
    {
        using var mutex=new Mutex(true,@"Local\SelfSnip.Instance.v1",out bool owns);
        if(!owns)throw new InvalidOperationException("Dedicated startup regression requires no existing Snip instance.");
        try
        {
            using var activation=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\SelfSnip.Activate.v1");activation.Reset();
            var start=new System.Diagnostics.ProcessStartInfo(Path.GetFullPath("src/Snip.App/bin/Release/net8.0-windows10.0.19041.0/Snip.exe")){UseShellExecute=false};
            start.ArgumentList.Add("--startup");
            using var child=System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Startup fixture did not launch.");
            if(!child.WaitForExit(10000)){child.Kill(true);throw new InvalidOperationException("Duplicate startup displayed a dialog or did not exit.");}
            Require(child.ExitCode==0 && !activation.WaitOne(0),"Duplicate startup activated the existing instance.");
        }
        finally{mutex.ReleaseMutex();}
        Console.WriteLine("PASS real application --startup exits quietly when an instance already owns the mutex, without activation/dialog.");
    }
    private static void CheckTranslationModes(string folder)
    {
        var image=System.Windows.Media.Imaging.BitmapSource.Create(100,80,96,96,System.Windows.Media.PixelFormats.Bgra32,null,new byte[100*80*4],400);image.Freeze();var export=new FakeExport();using var pins=new PinManager(export);
        var models=new ModelStore(Path.Combine(folder,"missing-models"));
        var onlineSettings=new OnlineTranslationSettingsStore(Path.Combine(folder,"online.json"));
        onlineSettings.Save(OnlineTranslationProviders.Defaults(OnlineTranslationProviders.DeepSeek) with {DefaultOnline=true},"PUBLIC-TEST-KEY");
        string prefsPath=Path.Combine(folder,"mode-preferences.json");var offline=new CountedTranslation(false);var online=new CountedTranslation(true);
        FeatureHub Hub()=>new(new AnnotationRenderer(),export,new FakeCapture(image),pins,ocr:new TextWindowChecks.FakeOcr(),models:models,
            translator:offline,onlineSettings:onlineSettings,onlineTranslator:online,translationPreferences:new TranslationPreferencesStore(prefsPath));
        TextResultWindow Open()
        {
            var window=Hub().ShowText(new(image,new AnnotationRenderer(),export,new EditorDialogs(()=>null)),true)!;
            TextWindowChecks.PumpUntil(()=>!((TextResultViewModel)window.DataContext).Busy && ((TextResultViewModel)window.DataContext).Source.Length>0,TimeSpan.FromSeconds(8));return window;
        }
        var window=Open();var text=(TextResultViewModel)window.DataContext;
        Require(text.Mode==TranslationMode.Online && online.Calls==1 && text.Status.Contains("失败") && window.IsVisible,"Legacy online preference or failure retention failed.");
        ((ComboBox)window.FindName("TranslationModeBox")).SelectedValue=TranslationMode.Offline;
        ((Button)window.FindName("SaveTranslationSettingsButton")).Command.Execute(null);
        Require(new TranslationPreferencesStore(prefsPath).Read()!.Mode==TranslationMode.Offline,"Workbench did not save offline selection.");window.Close();
        window=Open();text=(TextResultViewModel)window.DataContext;
        Require(text.Mode==TranslationMode.Offline && text.Translated=="离线译文" && offline.Calls==1 && online.Calls==1,"Reopened offline mode contacted the failed online server.");
        ((ComboBox)window.FindName("TranslationModeBox")).SelectedValue=TranslationMode.Basic;
        text.SaveTranslationSettingsCommand.Execute(null);var lookup=text.TranslateAsync();TextWindowChecks.PumpUntil(()=>lookup.IsCompleted,TimeSpan.FromSeconds(8));lookup.GetAwaiter().GetResult();
        Require(text.Translated.Contains("你好") && text.Translated.Contains("世界") && text.TranslationResultTitle=="基础查词结果" && !text.CanUseLocalCompute && !text.DownloadCommand.CanExecute(null) && !text.DownloadGpuCommand.CanExecute(null),"Basic dictionary result or mode controls failed.");window.Close();
        var onlineEditor=new OnlineTranslationSettingsViewModel(onlineSettings);onlineEditor.SaveCommand.Execute(null);onlineEditor.Dispose();
        window=Open();text=(TextResultViewModel)window.DataContext;
        Require(text.Mode==TranslationMode.Basic && text.Translated.Contains("世界") && online.Calls==1 && offline.Calls==1 && !Directory.Exists(models.DirectoryPath),"Saved dictionary mode used a server/model or online settings overrode the preference.");window.Close();
        File.WriteAllText(prefsPath,"broken public fixture");window=Open();text=(TextResultViewModel)window.DataContext;
        Require(text.Mode==TranslationMode.Offline && online.Calls==1,"Corrupt preferences silently re-enabled online service.");window.Close();
        Console.WriteLine("PASS failed online → save offline → fresh FeatureHub stays offline; save basic → reopen stays dictionary with zero additional online/model calls; online profile save cannot override choice.");
    }
    private sealed class RefusedStartup : IAutoStartService
    { public bool IsEnabled=>false;public void SetEnabled(bool enabled)=>throw new UnauthorizedAccessException(); }
    private sealed class CountedTranslation(bool fail) : ITranslationService
    {
        public int Calls;
        public Task<string> TranslateAsync(string text,string direction,CancellationToken token)
        { Calls++;if(fail)throw new InvalidOperationException("PUBLIC service unavailable fixture");return Task.FromResult("离线译文"); }
    }
    private sealed class FakeExport : IImageExportService
    {public void SavePng(System.Windows.Media.Imaging.BitmapSource image,string path){}public Task CopyAsync(System.Windows.Media.Imaging.BitmapSource image)=>Task.CompletedTask;}
    private sealed class FakeCapture(System.Windows.Media.Imaging.BitmapSource image) : IDesktopCaptureService
    {
        public IReadOnlyList<DisplayInfo> GetDisplays()=>[new("Fixture",new(0,0,800,600),true)];
        public DesktopFrame Capture()=>new(image,new(0,0,800,600),GetDisplays());public PixelPoint CursorPosition=>new(100,100);
        public void PlaceOverlay(Window window,PixelRect bounds){}
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
