using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Snip.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Translation;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class OnlineSettingsChecks
{
    public static int Run()
    {
        string folder=Path.Combine(Path.GetTempPath(),"Snip.Online.Settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"settings.json"), secret="PUBLIC-TEST-KEY-"+Guid.NewGuid().ToString("N");
        _ = Application.Current ?? new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var store=new OnlineTranslationSettingsStore(path);
            Require(store.Read() is null,"Fresh settings unexpectedly configured.");
            var settings=new OnlineTranslationSettings("public-provider","https://api.example.com/v1","public-model",true);
            store.Save(settings,secret);
            string raw=File.ReadAllText(path);
            Require(!raw.Contains(secret),"Key was stored in plain text.");
            var encrypted=Convert.FromBase64String(JsonNode.Parse(raw)!["Profiles"]![0]!["ProtectedKey"]!.GetValue<string>());
            var plain=ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser);
            try { Require(Encoding.UTF8.GetString(plain)==secret,"Native Windows DPAPI did not recover the original key."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Require(new OnlineTranslationSettingsStore(path).ReadCredentials().ApiKey==secret,"Restart did not retain encrypted settings.");
            Require(store.Read() is { HasApiKey:true,DefaultOnline:true },"Metadata did not describe configured state.");
            Require(!store.ReadCredentials().ToString().Contains(secret),"Credential ToString leaks a key.");
            Console.WriteLine("PASS actual current-user DPAPI storage encrypts key, restores after restart and redacts credentials.");

            store.Save(settings with { Model="other-public-model" },null);
            Require(store.ReadCredentials().ApiKey==secret,"Changing model lost saved key.");
            bool rejected=false;
            try { store.Save(settings with { Endpoint="https://other.example.com/v1" },null); }catch(InvalidOperationException){rejected=true;}
            Require(rejected && store.ReadCredentials().ApiKey==secret,"Changing server silently sent or reused a previous credential.");
            store.Save(settings with { Endpoint="https://other.example.com/v1",DefaultOnline=false },null);
            Require(store.Read() is { HasApiKey:false },"Changed server retained old credential.");
            Console.WriteLine("PASS changing model keeps key; changing provider endpoint requires a new key and preserves previous settings on failure.");

            store.Save(settings,secret);store.Save(settings with { DefaultOnline=false },null,removeKey:true);
            Require(store.Read() is { HasApiKey:false },"Explicit key removal failed.");
            store.Delete();Require(store.Read() is null,"Settings delete failed.");
            Console.WriteLine("PASS explicit key removal and configuration deletion work.");
            CheckWindows(path);
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{foreach(var window in Application.Current!.Windows.Cast<Window>().ToArray())window.Close();SynchronizationContext.SetSynchronizationContext(context);Directory.Delete(folder,true);}
    }
    private static void CheckWindows(string path)
    {
        var store=new OnlineTranslationSettingsStore(path);
        var settings=new OnlineTranslationSettingsViewModel(store);
        var window=new OnlineTranslationSettingsWindow(settings);window.Show();window.UpdateLayout();
        Require(settings.Provider==OnlineTranslationProviders.DeepSeek && settings.Model=="deepseek-chat","DeepSeek defaults missing.");
        ((PasswordBox)window.FindName("ApiKeyBox")).Password="PUBLIC-KEY-DEEPSEEK";settings.SaveCommand.Execute(null);
        Require(store.ReadCredentials(OnlineTranslationProviders.DeepSeek).ApiKey=="PUBLIC-KEY-DEEPSEEK","Actual password control did not save key.");
        Require(((PasswordBox)window.FindName("ApiKeyBox")).Password.Length==0,"Save did not clear visible password buffer.");
        settings.Provider=OnlineTranslationProviders.Qwen;
        Require(settings.Model=="qwen-plus" && !settings.HasSavedKey,"Switching provider reused previous key.");
        ((PasswordBox)window.FindName("ApiKeyBox")).Password="PUBLIC-KEY-QWEN";settings.DefaultOnline=true;settings.SaveCommand.Execute(null);
        settings.Provider=OnlineTranslationProviders.DeepSeek;
        Require(settings.HasSavedKey && ((PasswordBox)window.FindName("ApiKeyBox")).Password.Length==0,"Saved provider key should remain encrypted and not be repopulated.");
        Require(store.ReadCredentials(OnlineTranslationProviders.Qwen).ApiKey=="PUBLIC-KEY-QWEN" && store.ReadCredentials(OnlineTranslationProviders.DeepSeek).ApiKey=="PUBLIC-KEY-DEEPSEEK","Provider profiles did not persist independently.");
        window.Close();Console.WriteLine("PASS actual settings XAML saves independent provider keys, clears password display and restores profiles.");

        var image=BitmapSource.Create(100,80,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)255,100*80*4).ToArray(),400);image.Freeze();
        var online=new FakeOnline();var offline=new OfflineTrap();
        using var vm=new TextResultViewModel(image,new TextWindowChecks.FakeOcr(),new ModelStore("unused-models"),offline,onlineTranslator:online,defaultOnline:true){ AutoTranslate=true };
        var result=new TextResultWindow(vm);result.Show();
        TextWindowChecks.PumpUntil(()=>!vm.Busy && vm.Translated.Length>0,TimeSpan.FromSeconds(8));
        Require(vm.Source=="Hello world" && vm.Translated=="在线译文" && online.Calls==1 && offline.Calls==0,"Online selection still required offline model.");
        Require(((TextBox)result.FindName("TranslatedText")).Text=="在线译文" && vm.TranslationNotice.Contains("发送"),"Online result or transmission notice is missing.");
        online.Fail=true;vm.TranslateCommand.Execute(null);TextWindowChecks.PumpUntil(()=>!vm.Busy && vm.Status.Contains("失败"),TimeSpan.FromSeconds(4));
        Require(vm.Source=="Hello world" && vm.Translated=="在线译文" && result.IsVisible,"Online failure discarded prior text or window.");
        result.Close();Console.WriteLine("PASS online OCR-to-result bypasses model download, displays notice and retains original/result on service failure.");
    }
    private sealed class OfflineTrap : ITranslationService
    {
        public int Calls;
        public Task<string> TranslateAsync(string text,string direction,CancellationToken token){Calls++;throw new InvalidOperationException("Offline route unexpectedly used.");}
    }
    private sealed class FakeOnline : ITranslationService
    {
        public int Calls;public bool Fail;
        public Task<string> TranslateAsync(string text,string direction,CancellationToken token){Calls++;token.ThrowIfCancellationRequested();if(Fail)throw new InvalidOperationException("PUBLIC service limit fixture");return Task.FromResult("在线译文");}
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
